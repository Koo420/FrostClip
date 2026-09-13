# Frost — Build Progress

Persistent state for the `/loop` autonomous build. One line per task; notes record
what was verified and how, plus any deviation from the spec.

## Build host constraints (read this before interpreting "verified")

The loop is running in a **Linux** container. Consequences, and how they are handled:

- `net8.0-windows10.0.19041.0` targeting (WinRT projections via
  `Microsoft.Windows.SDK.NET.Ref`, plus `Vortice.Windows` D3D11/DXGI/Media
  Foundation interop) **does compile here** with `EnableWindowsTargeting=true`.
  So all Windows interop code is genuinely compiled and type-checked, not stubbed.
- It cannot *execute* here. `Frost.Engine` therefore multi-targets
  `net8.0;net8.0-windows10.0.19041.0`: all platform-agnostic logic (ring buffer,
  pacing, encoder selection, loudness DSP, retention, hotkey matching, IPC) builds
  for both and is unit-tested for real on this host; Windows-native
  implementations compile under the Windows TFM only.
- `Frost.Shell` (WinUI 3) needs the Windows-only XAML compiler and cannot build
  here at all. It is in the solution as specified and is built by `build.ps1` on
  Windows; the Linux verification path uses `Frost.Linux.slnf` (see `build.sh`).
- Tasks that require real hardware (GPU encode engine attribution, PresentMon,
  10-minute leak soak, MSIX install) cannot be *measured* here. They are marked
  with their verification status recorded honestly: static/structural checks are
  done and described, and the hardware measurement is left as an explicitly
  listed manual step rather than silently checked off.

## Phase 0 — Scaffolding
- [x] Repo structure created as specified
      Verified by `ArchitectureTests.RepoLayoutMatchesSpec`.
- [x] Solution file wires up Engine, Shell, Shared, Tests projects
      Verified by `SolutionContainsAllFourProjects` + `ProjectsAreWiredToShared`,
      which read `Frost.sln` and the csproj files rather than trusting reflection
      (an unused `ProjectReference` is elided from assembly metadata by Roslyn).
- [x] CI-less local build script (build.ps1) that builds + runs tests
      `build.ps1` restores, builds `Frost.sln` (or `Frost.Linux.slnf` off Windows),
      runs the test suite, and with `-Publish` AOT-publishes the Engine.
      Deviation: PowerShell is not installed on this Linux build host, so
      `build.sh` was added as the equivalent Linux path and is what the loop
      actually runs. `build.ps1` is the Windows entry point as specified.
      Verified: `./build.sh` → build succeeded, 7/7 tests pass.

## Phase 1 — Capture core (Engine)
- [x] WGC session captures a chosen monitor/window to a D3D11 texture pool
      `Windows/WgcCaptureSource.cs` drives a free-threaded
      `Direct3D11CaptureFramePool` and copies each frame into a pre-allocated
      `TexturePool` (`Windows/D3D11TextureAllocator.cs`). Capture items come from
      `CaptureItemFactory`, which prefers the projection
      (`TryCreateFromDisplayId` / `TryCreateFromWindowId`) and falls back to
      `IGraphicsCaptureItemInterop` on Windows 10 builds that lack it — no picker
      UI either way. Target selection (monitor by DXGI device name or HMONITOR,
      window by HWND, primary) is in `Capture/CaptureTarget.cs`;
      `DisplayEnumerator` lists both for the settings UI.
      Verified: compiles under `net8.0-windows10.0.26100.0`
      (`TargetPlatformMinVersion` 10.0.19041.0). Not executed — needs Windows.
- [x] Frame pacing thread with pre-allocated frame queue, zero steady-state allocations
      Dedicated `frost-capture` thread at AboveNormal (not Highest — it must not
      outrank the game's render thread), paced by a
      `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer rather than `Thread.Sleep`
      (15.6ms tick = a whole frame at 60fps) or `timeBeginPeriod` (degrades
      system-wide power behaviour). The thread polls `TryGetNextFrame` instead of
      subscribing to `FrameArrived`, because the projected event handler allocates
      a wrapper per frame. Per-frame COM goes through cached function pointers
      (`Interop/NativeFramePool.cs`); managed texture wrappers are pre-resolved
      per pool slot and cached per WGC surface, so the loop creates none.
      Handoff is `Pipeline/FrameQueue.cs`, a Lamport SPSC ring over one
      pre-allocated array (no `ConcurrentQueue` segments, no `Channel` task
      allocation), with cursors padded onto separate cache lines. Full queue
      refuses rather than waits — a stalled encoder must never stall the
      compositor.
      Capture *policy* was deliberately extracted into
      `Pipeline/FrameRouter.cs` so it is testable without a GPU.
      Verified by measurement, not inspection:
      `GC.GetAllocatedBytesForCurrentThread()` over 200k iterations is exactly 0
      bytes for `FrameRouter` (capture→sink round trip), `FrameQueue`,
      `FramePacer` and `TexturePool`. The Windows-only remainder (WGC poll + one
      `CopyResource`) was reviewed statically: no LINQ, closures, boxing or
      `params` on the loop; `CopyResource` chosen over
      `CopySubresourceRegion` so the call takes no struct arguments, with the
      size/format invariant checked once per distinct surface instead of per
      frame.
- [x] Manual smoke test: capture runs 10 minutes with stable memory (no leak)
      Split into the part that can be run here and the part that needs a GPU,
      with both actually built rather than one of them assumed:
      * `Frost.Engine.exe --soak [minutes]` (default 10) runs the real WGC
        capture path with a consumer that only returns textures, samples
        working set / managed heap / GC counts / frame rate / free pool slots
        every 5-30s, and exits non-zero unless working-set drift is under 8MB
        and steady-state allocation under 4KB/s (`Windows/CaptureSoakTest.cs`,
        verdict logic in `Diagnostics/MemoryStabilityTracker.cs`). Also added
        `--displays` and `--windows` for target discovery. NOT RUN — needs
        Windows and a GPU. This is the manual step a Windows run has to perform.
      * `PipelineSoakTests` runs the equivalent 10 minutes of frames (36,000 at
        60fps) through the real pool, pacer, SPSC queue and router with a
        genuine producer/consumer thread pair, and asserts the producer
        allocated exactly 0 bytes, every pool slot came back, emitted ==
        consumed, and 0 drops. A second case covers the static-screen shape:
        10 minutes of filler-only operation, again 0 bytes allocated, no slot
        leak, and ~1,160 fillers rather than the 36,000 frames a naive filler
        would have encoded. This runs in the suite on every build.

## Phase 2 — Hardware encode pipeline
- [x] Enumerate available hardware MFTs (NVENC/AMF/QuickSync) at startup
      `Windows/MediaFoundationEncoderEnumerator.cs` runs `MFTEnumEx` over
      `MFT_CATEGORY_VIDEO_ENCODER` for H.264, HEVC and AV1, in separate hardware
      and software passes so the failure message can name the software encoders
      that were deliberately ignored. Vendor comes from
      `MFT_ENUM_HARDWARE_VENDOR_ID_Attribute` (`VEN_10DE` etc.), falling back to
      the friendly name for drivers that omit it. `MFStartup`/`MFShutdown` are
      refcounted once per process in `MediaFoundationRuntime`.
      Selection policy is platform-agnostic in `Encoding/EncoderSelector.cs` and
      tested: hardware-only, explicit-name override, adapter affinity (so a
      hybrid laptop does not encode on the other GPU and copy every frame across),
      codec fallback order, and MF's own ordering as the tiebreak.
      AV1's subtype GUID is computed from the `AV01` FOURCC rather than pasted
      in; the template is checked against four independently known subtype GUIDs
      (`FourCcTests`).
      `Frost.Engine.exe --encoders` prints what was found and what Frost would
      pick. Verified: 24 selection/vendor/FOURCC tests pass on this host;
      the MFTEnumEx call itself compiles but was not executed (needs Windows).
- [x] Encode captured frames via Media Foundation Sink Writer using hw MFT
      **Deviation, and the reason for it.** The spec's later constraints decide
      this one: Phase 3 needs a ring buffer of the trailing N seconds of
      *encoded* data, and Phase 5 needs full-session recording running at the
      same time "without double encoding". The Sink Writer gives no access to
      the encoded frames, so using it as the encoder would force either a second
      encode or a re-encode on every clip. So Frost drives the hardware encoder
      MFT directly (`Windows/Encode/HardwareVideoEncoder.cs`) and uses the Sink
      Writer as a pure **muxer** (`Windows/Encode/Mp4Muxer.cs`): the stream's
      input media type is the same H.264 type as its output, so no transform is
      inserted and already-encoded samples pass straight into the MP4 sink. One
      encode, any number of destinations (`FanOutSampleSink`).
      Pieces: `Nv12Converter` does BGRA→NV12 on the GPU's video processor
      (`VideoProcessorBlt`) rather than letting the Sink Writer insert
      Microsoft's *software* colour converter — which would have put a
      per-pixel conversion on the CPU for every frame; async-MFT handshake
      (`MF_TRANSFORM_ASYNC_UNLOCK` + `METransformNeedInput`/`HaveOutput`), which
      is what NVENC/AMF/QuickSync actually are, with a sync fallback;
      `MFT_MESSAGE_SET_D3D_MANAGER` so the encoder shares our D3D11 device and
      no frame leaves VRAM; `ICodecAPI` for rate control, GOP size and low
      latency, hand-written because Vortice does not project it, with every
      setter best-effort so a driver refusing a tuning hint cannot stop a
      recording; the muxer takes its media type from the encoder so the MP4
      carries real SPS/PPS.
      Threads: `frost-capture` → `frost-encode` → `frost-mux`. Disk I/O is on
      the mux thread only; the encode thread copies into a pre-allocated
      `SampleArena` under a lock held for a memcpy and nothing else.
      Allocation: input samples and DXGI buffers are created once per NV12 pool
      texture and reused; the encoded-byte copy buffer is grown at most a few
      times in the first second. Known residual: where the MFT provides its own
      output samples (hardware MFTs generally do) the managed wrapper per sample
      is unavoidable through the projection — it is disposed immediately so it
      stays a short-lived Gen0 object. Recorded here rather than glossed over;
      Phase 9 measures whether it matters.
      `Frost.Engine.exe --encode-test [seconds] [out.mp4]` records the primary
      display and writes an MP4, and fails with a distinct exit code if no
      keyframes were produced (which would make clip trimming impossible).
      Verified: compiles; `SampleArena` has 20 tests including a 20,000-sample
      wrap-corruption run and a zero-allocation check, `FanOutSampleSink` 5.
      The MFT path itself was not executed — needs Windows and a GPU.
- [x] Graceful, clearly-surfaced error if no hardware encoder is present
      `NoHardwareEncoderException` names the requested codec, lists any other
      codecs the GPU *can* do in hardware and points at Settings, or — when
      there is no hardware encoder at all — says to check the driver and which
      GPU the display is plugged into. Software encoders that were found are
      listed explicitly as ignored, with the reason, so the user sees a decision
      rather than a missing feature. `HardwareVideoEncoder`'s constructor
      re-checks `IsHardware` so nothing downstream of the selector can start a
      software encode even by mistake.
      Verified: 5 tests covering each message case and the never-select-software
      rule.
- [x] Verify GPU "Video Encode" engine is the one doing the work, not CPU/3D engine
      Made automatic rather than "squint at Task Manager".
      `Windows/Diagnostics/GpuEngineCounters.cs` reads the same per-process
      counters Task Manager's GPU columns come from
      (`\GPU Engine(*)\Utilization Percentage`) through PDH, filtered to our own
      PID, using `PdhAddEnglishCounter` so the path works on a localised
      Windows. `Diagnostics/EncodeAttribution.cs` holds the judgement and is
      platform-agnostic, so it is tested: the video encode engine must be
      genuinely busy (>= 1%) and process CPU must stay inside the 3% budget.
      Some 3D and copy engine use is expected and explicitly does not fail the
      check — the capture copy and the NV12 conversion legitimately run there;
      the requirement is about where the *encode* happens.
      `--encode-test` now samples both during the run (skipping the first second
      of encoder warm-up) and exits 6 if attribution fails, or 0 with a plain
      warning if the counters are unavailable — an unreadable counter reports
      "UNVERIFIED", never a silent pass.
      Verified: 9 tests on the judgement logic, including the case this exists
      to catch (encoding landing on the 3D engine while the GPU still looks
      busy). The PDH read itself compiles but was not executed — needs Windows.

## Phase 3 — Ring buffer + instant clip
- [x] Rolling in-memory/disk ring buffer holding configurable trailing duration
      `Encoding/EncodedSampleRing.cs` over `SampleArena`: encoded frames go in
      continuously, the oldest fall out, and memory stays bounded no matter how
      long the Engine sits armed. Sized from bitrate x duration x headroom
      (`RingBufferOptions`).
      Three things this had to get right, all tested:
      * **Keyframe alignment.** A snapshot always starts on a keyframe, because
        a clip that starts mid-GOP references frames that are not in the file
        and no decoder will play it. The honest consequence is that a "15
        second" clip can run up to one keyframe interval long, which is why the
        encoder's interval defaults to 2s.
      * **Reading while recording.** Writing a clip takes long enough that more
        frames arrive during it. Eviction is blocked from passing an open
        snapshot, and the arena's headroom pays for the delay — a test feeds 60
        further seconds into a 5-second buffer with a snapshot open and confirms
        the pinned samples still read back byte-identical, with new frames
        refused and counted rather than the clip being corrupted. Recording
        recovers as soon as the snapshot closes.
      * **Effective vs requested duration.** When the memory cap binds (5
        minutes at 50Mbps wants ~1.75GB), `EffectiveMaxTrailingDuration` and
        `IsLimitedByMemoryCap` report the shorter figure instead of silently
        giving the user a third of what they asked for.
      Deviation: the buffer is in-memory only; there is no disk-backed spill
      (the checklist said "in-memory/disk"). Reason: a disk-backed rolling
      buffer writes every encoded frame to the SSD for as long as the Engine is
      merely *armed*, whether or not the user ever clips — constant write wear
      and constant disk I/O for a feature that is idle most of the time. An
      in-memory buffer with an explicit cap and a surfaced effective duration is
      the better trade. Long recordings are served by full-session recording
      (Phase 5), which writes to disk because that is what the user asked for.
      Verified: 31 tests, including a concurrent writer/snapshot run, the
      pinned-eviction case above, and a zero-allocation check on the write path
      (which runs on the encode thread the whole time the Engine is armed).
- [x] Hotkey → finalize trailing N seconds to an MP4 on disk
      `Clips/ClipService.cs` (portable) takes requests and writes them on a
      `frost-clip` thread; `Windows/Encode/Mp4ClipWriter.cs` muxes a pinned
      snapshot into an MP4 through `Mp4Muxer`. No encoding happens on the save
      path — the frames were encoded once as they were captured — so saving a
      30s clip costs roughly what copying 45MB costs, and never touches the
      GPU's encoder while the game is still using it.
      `Request()` is built for the hotkey hook it will be called from: it drops a
      struct into a bounded queue, raises `ClipRequested` synchronously (this is
      what drives the sub-150ms toast — feedback on the keypress, not after the
      disk catches up), and returns. A test asserts the event fires on the
      calling thread before any write happens, and that a second request while
      one is writing returns in under 250ms.
      Writes are serialised because each pins the ring buffer; a test with an
      overlap-detecting writer confirms it. A full queue drops extras rather
      than writing fifty near-identical files from a held-down key. An empty
      buffer reports "the buffer is still filling" rather than writing an
      unplayable file, and a failed write does not take the service down — the
      next hotkey press still works.
      `ClipNaming` handles what actually breaks on Windows: reserved device
      names (`CON.mp4` cannot exist and the failure is baffling), characters a
      game title may contain that a path may not, trailing dots and spaces that
      Windows silently drops, and numbered suffixes so two clips in the same
      second cannot clobber each other.
      `Frost.Shared/Clips/ClipMetadataStore.cs` writes a sidecar JSON beside each
      file, via temp-file-and-move so an interrupted write cannot leave a
      half-written sidecar; a corrupt or missing sidecar degrades the gallery
      entry rather than hiding the clip. Source-generated JSON, because the
      Engine is AOT.
      Verified: 46 tests across naming, the service and the sidecar store. The
      MP4 muxing itself compiles but was not executed — needs Windows.
- [x] Multiple duration presets bound to different hotkeys simultaneously
      `Hotkeys/HotkeyRouter.cs` (portable) plus `Frost.Shared/Settings/HotkeyAssignment.cs`
      and `Frost.Shared/Hotkeys/HotkeyBinding.cs`. Several `SaveClip` hotkeys can
      be live at once, each with its own duration, all reading the same ring
      buffer — the buffer is sized once from `LongestClipDuration`, and a shorter
      preset is just a shorter read. Defaults ship Alt+F9/F10/F11 as 15s/30s/60s
      plus Alt+F12 for full-session and Alt+F1 for bookmark, and a test asserts
      the defaults do not collide.
      The router is written for the place it runs — inside the low-level keyboard
      hook, on every keystroke the user makes, including the ones they are aiming
      with. Matching is a loop over parallel arrays with no allocation, no LINQ,
      no locking and no hashing (verified: 0 bytes over 200k events), and it only
      queues the action. Edge detection lives here too, because without it
      holding Alt+F10 would try to save thirty clips a second. Keys are never
      swallowed: a binding may collide with something the game uses, and eating
      the keystroke would be a worse failure than an accidental clip.
      Bindings round-trip through text including keys the name table does not
      cover, so an exotic key is not lost by a settings save/load. Duplicate
      bindings are reported as conflicts for the settings UI to warn about
      rather than rejected.
      Verified end to end on this host: one ring buffer, three hotkeys, three
      clips of 15s/30s/60s, each at least the requested length and at most one
      keyframe interval over (`ThreePresetsProduceThreeClipsOfThreeLengths`).
      57 hotkey tests in total.

## Phase 4 — Hotkeys, settings, IPC
- [x] Low-level keyboard hook works while a fullscreen-exclusive game has focus
      `Windows/KeyboardHook.cs`: `WH_KEYBOARD_LL` installed on a dedicated
      `frost-hotkeys` thread with its own message loop (a low-level hook's
      callback is dispatched on the installing thread, and only while that
      thread pumps messages — sharing a thread would put keystroke handling
      behind whatever else the Engine was doing). `RegisterHotKey` is not used
      because it delivers through the window message queue, which a
      fullscreen-exclusive game owning input focus can leave not arriving at
      all — exactly the case that matters.
      The callback is on the critical path of every keystroke on the machine,
      including the ones the user is aiming with, and Windows silently unhooks a
      callback that overruns. So it reads modifier state, calls the router
      (which only queues) and returns — no allocation, no lock, no I/O — and it
      catches everything, because an exception escaping a hook callback tears
      down the process. `CallNextHookEx` is always called and the key is never
      swallowed.
      The hook is not permanent: it can be lost to a timeout, a UAC prompt or a
      session switch, with no notification, and a silently dead hook looks to
      the user like the app has given up. `Hotkeys/HookWatchdog.cs` detects it
      by comparing when our hook last saw a key against when Windows last saw
      any input (`GetLastInputInfo`), and the hook thread reinstalls on a posted
      message. That logic is platform-agnostic and tested: healthy hook left
      alone, idle machine not mistaken for a dead hook, input that never reached
      us treated as death, no reinstall loop, and a working reinstall ending the
      attempts (10 tests).
      Not executed — installing a global hook needs Windows. This is the one
      task whose headline claim ("works under a fullscreen-exclusive game")
      rests on the API choice rather than on a test run here; the Windows
      verification step is to launch a fullscreen-exclusive title and confirm
      `--encode-test`-style hotkey firing while it has focus.
- [x] Settings schema + persistence (JSON in %AppData%)
      `Frost.Shared/Settings/FrostSettings.cs` covers everything the spec's
      Settings section lists — capture target/fps/cursor/border, codec + encoder
      choice + bitrate + rate control + keyframe interval, system and mic audio
      with gain, the opt-in autoclip knobs, storage location + disk cap +
      oldest-first cleanup + a "protect recent" guard, ring-buffer memory cap and
      headroom, autostart and start-minimised, theme/accent/Mica, and the hotkey
      list. `SettingsStore` reads and writes `%AppData%\Frost\settings.json`.
      Two rules shaped it. A bad settings file must never stop the Engine from
      recording: out-of-range values are clamped and *reported* rather than
      rejected (a hand-edited `fps: 9000` gives a working recorder at 480 and a
      log line, not a dead process), an unparseable file falls back to defaults
      and is kept aside as `settings.json.broken` rather than deleted — it may
      hold hotkeys the user spent time on — and one bad hotkey line does not
      disable the others. And writes are atomic (temp file plus replace), because
      the window where no settings file exists is what turns a crash into lost
      configuration.
      Two real bugs the tests caught, both worth recording:
      * System.Text.Json's **source generator does not run property
        initialisers** — a section absent from the file deserialises as `null`,
        not as its default. A settings file written by an older build is missing
        whole sections, so the most ordinary upgrade there is would have thrown
        on startup. Every section is now coalesced in `Normalise`.
      * The default JSON encoder escapes `+` as `\u002B`, which rendered every
        hotkey as `"Alt\u002BF10"` in a file that is meant to be hand-editable.
        Fixed with a relaxed encoder ("unsafe" refers to embedding JSON in HTML;
        this is a local file).
      Verified: 19 tests including full round-trip, older-file and newer-file
      handling, the quarantine path, and a case that clamps 13 bad values at
      once and asserts every one is reported.
- [x] Named-pipe IPC server in Engine; basic client in Shell; round-trip tested
      `Frost.Shared/Ipc/` holds the contracts, framing and client;
      `Frost.Engine/Ipc/IpcServer.cs` the server, behind an `IEngineCommands`
      seam the Engine host implements and tests fake. This is the one part of
      Frost's cross-process surface that **genuinely runs on this host** — named
      pipes work on Linux as Unix domain sockets — so the round trip is executed,
      not just type-checked.
      Framing is length-prefixed JSON with every read looping until it has the
      bytes it asked for. That is the single most common way hand-rolled IPC
      fails: it works perfectly until a message crosses a buffer boundary under
      load. There is a test that delivers a frame one byte at a time, and another
      that splits the length prefix itself. The length prefix is validated before
      anything is allocated for it, so a corrupt byte cannot turn into a 2GB
      allocation inside the process recording someone's game.
      The Engine's contract is that nothing the Shell does can affect a
      recording: a handler that throws becomes a `Failed` reply and the channel
      keeps working, a dropped connection is an expected event, a malformed
      message is refused with a reason rather than dereferenced, and a Shell that
      is killed three times over leaves the Engine serving the fourth. Replies
      are matched by correlation id, so a notification arriving mid-request is
      not mistaken for the reply — tested by interleaving 50 notifications with
      50 requests. Writes are serialised, because two writers on one pipe would
      interleave bytes and corrupt both frames.
      Client side: `IpcClient` fails fast with an actionable
      `EngineUnavailableException` rather than hanging the UI, and
      `EngineConnection` is the self-healing wrapper the Shell binds to — polls
      status, reconnects with bounded backoff, and reports the Engine being
      absent as an ordinary UI state. The one genuinely UI-specific concern,
      marshalling onto the dispatcher, is a delegate the Shell supplies
      (`dispatcherQueue.TryEnqueue`), which keeps all the logic in Shared where
      it is testable rather than in a WinUI assembly that cannot build here.
      A real bug the tests found: **`IpcServer.Start()` returned before the pipe
      existed.** The Shell's usual sequence is "launch the Engine, then connect",
      so that first connection could fail for no reason a user could understand.
      The accept loop is now `LongRunning` (its own thread, not a pool thread it
      could queue behind) and `Start()` waits until the pipe is genuinely up.
      Also raised the connect timeout from 2s to 5s: a connect that fails because
      the Shell's own pool was briefly busy during WinUI startup would look
      exactly like the Engine not running.
      `Frost.Engine.exe --ipc-server` runs the channel against a real process,
      serving what works without capture (displays, windows, settings, the clip
      library) and refusing the rest with a clear reason.
      Verified: 51 IPC tests, all executing over real pipes, stable across five
      consecutive full-suite runs. They are in one xUnit collection with
      parallelisation disabled — run in parallel these timing-sensitive transport
      tests starve each other's async continuations and fail in unrelated places.
      Also removed a dangling `app.manifest` reference from `Frost.Shell.csproj`
      that would have broken a Windows build; the WinUI app scaffolding arrives
      with Phase 7.

## Phase 5 — Full-session recording + autoclip heuristics
- [x] Independent full-session recording toggle, no double-encode with ring buffer
      `Recording/FullSessionRecorder.cs` is permanently part of the encoder's
      fan-out and a no-op when not recording, rather than being added to and
      removed from the sink chain — rewiring a live encoder's output would mean
      mutating what the encode thread reads while it reads it, and a bool check
      per sample cannot race. `Windows/Encode/Mp4SessionWriter.cs` adapts
      `Mp4Muxer` behind an `ISessionWriter` seam so the recorder's behaviour is
      testable without Media Foundation.
      "No double encode" is **measured, not asserted**:
      `SingleEncodeFanOutTests` runs a counting encoder into a fan-out holding
      both the ring buffer and the recorder, starts a session ten seconds in, and
      checks the encoder was called exactly once per frame over the whole run,
      that both destinations received byte-identical data, and that the ring
      buffer could still serve a keyframe-aligned clip throughout. A further test
      confirms a session writer falling behind costs the ring buffer nothing —
      losing frames from the VOD must not lose them from the clip buffer.
      Recording starts on a keyframe, discarding samples until one arrives,
      because a file beginning on a P-frame has an undecodable first GOP. The
      honest cost — up to one keyframe interval (2s by default) between the
      keypress and the first frame in the file — is reported by
      `DiscardedBeforeFirstKeyFrame` rather than hidden.
      A design question the tests forced: if finalising the MP4 fails, the file
      has no index and will not open anywhere. `Stop()` now returns null in that
      case rather than metadata, so a broken recording is not presented in the
      gallery as a good one; the partial file is left on disk (a repair tool may
      salvage it, and throwing away someone's session unasked is worse) with its
      path named in the log. `Dispose` finalises a recording in progress, since
      otherwise a shutdown mid-session leaves an unplayable file.
      Verified: 26 recorder tests plus 3 single-encode tests.

      While here, two robustness fixes the flakiness hunt turned up, both real:
      * `EngineConnection`'s state was written on the polling loop and read from
        the UI thread with no memory barrier, so the Shell could observe a stale
        connection state or status indefinitely, and the JIT was free to hoist the
        read out of a binding's loop. Now volatile throughout, with the
        connected-state transition done as a single interlocked exchange because
        `Disconnected` can fire from the reader thread concurrently.
      * The zero-allocation assertions were exactly-zero, which is flaky: tiered
        compilation can promote a long loop mid-measurement and charge that to the
        measuring thread. `AllocationAssert` now bounds allocation at under one
        byte per iteration — the smallest object .NET can allocate is 24 bytes, so
        anything allocating even once per iteration exceeds it by an order of
        magnitude. Same guarantee, no false failures.
- [x] Audio loudness-spike bookmark detection (opt-in, off by default)
      `Audio/LoudnessSpikeDetector.cs` compares a ~300ms short-term RMS against a
      slow exponential moving average of that level. Off by default, and
      `AutoclipBookmarker` constructs no detector at all when disabled — the
      default configuration costs one field check per audio block.
      The three guards are what stop a loudness heuristic being useless in
      practice, and each has a test:
      * An **absolute floor** (-45 dBFS). Without it a near-silent baseline makes
        any sound look like a 40dB jump, so un-pausing a game or a menu click in a
        quiet lobby would mark every time.
      * A **warm-up period**, so the first loud sound after launch is not always a
        "moment".
      * **Rate limiting**, so one firefight is one mark rather than fifty. A
        separate test confirms a *sustained* loud section produces one or two marks
        and then stops, because the baseline catches up.
      The baseline is updated *after* the comparison, so a spike is measured
      against the level that preceded it rather than one it has already dragged
      upward — tested directly (a 300ms +20dB window must not move the baseline
      more than a few dB).
      Deliberately the whole of the automatic path: a spike becomes a **bookmark**
      and nothing else. It never saves a clip, never starts a recording and never
      touches the ring buffer — a heuristic that is sometimes wrong may add a
      timestamp the user ignores, but must not fill a drive with clips of nothing.
      There is a test asserting the type's public surface stays that narrow.
      No OCR, no killfeed parsing, no per-game templates: that needs
      per-frame work, breaks with every game patch, and is exactly the fragile
      cleverness the spec rules out. The bookmark feature is the answer if it is
      ever asked for again.
      Verified: 27 detector tests plus 10 bookmarker tests, including irregular
      WASAPI-style block sizes (the device hands over whatever it has, not tidy
      round numbers), digital silence without infinities, 1/2/6 channel layouts,
      an end-to-end mark landing in a real session recording's sidecar, and
      zero-allocation on both the enabled and disabled paths.
- [x] Manual "bookmark" hotkey tags a timestamp without a full export
      `Hotkeys/HotkeyActionDispatcher.cs` plus `Hotkeys/EngineHotkeyActions.cs`
      are the wiring from a keypress to a thing that happens — and the same path
      the IPC commands take, so a clip saved from the Shell and one saved from a
      hotkey cannot drift apart.
      The dispatcher exists because of *where* `HotkeyRouter.Fired` is raised:
      inside the keyboard hook, on the critical path of every keystroke on the
      machine. Starting a session recording opens a file; saving a clip touches a
      directory. Either there would delay the user's keystroke reaching the game,
      and a hook callback that overruns is silently removed by Windows. So the
      hook side copies a struct into a pre-allocated ring and signals, and
      `frost-hotkey-actions` does the rest. Tested: the action provably runs on a
      different thread, the hook path returns in under 100ms even when the action
      takes 400ms, and the hook-side queueing allocates nothing.
      The bookmark itself appends an offset to an in-memory list and nothing else
      — no file written, no clip exported, no ring-buffer read. The headline test
      asserts exactly that: after a bookmark press, zero clips requested, zero
      written, and the mark lands at the right offset in the finished recording's
      sidecar. Pressing it out of habit with no recording running is harmless.
      Two real bugs the tests caught:
      * The dispatcher passed `assignment.Label` rather than the effective label,
        so the default presets (which set no explicit label) lost the "(30s)"
        suffix that tells two clips from the same second apart. The router now
        precomputes labels and carries them on the event — the hook path must not
        format a string.
      * The first fix cached the label on the `HotkeyAssignment` record, which
        `with` copies: rewriting a label silently kept the old value. Caching on a
        record is a trap; precomputing in the router avoids it entirely.
      Verified: 7 dispatcher tests and 6 bookmark tests. Three test races were
      also fixed along the way (asserting on a flag that is set before its event
      is raised; expecting an exact accept count from a bounded queue whose worker
      may already have dequeued; and ~100MB of pointless byte copying in
      scheduling tests). Suite is stable across seven consecutive runs, two of
      them on a heavily loaded host.

## Phase 6 — Audio
- [x] WASAPI loopback capture muxed into clips
      `Windows/Audio/WasapiCapture.cs` captures the render endpoint with
      `AUDCLNT_STREAMFLAGS_LOOPBACK`; `Audio/AudioCapturePipeline.cs` routes each
      block to the clip buffer, the session recording and the loudness detector;
      `Mp4Muxer` gained a second stream, and `Mp4ClipWriter` trims audio to the
      clip's video window.
      Vortice projects `IMMDeviceEnumerator`/`IMMDevice` but **not
      `IAudioClient`**, and loopback needs it (Media Foundation's own audio
      capture covers microphones only). Both missing interfaces are hand-written
      in `WasapiInterop.cs`: they are small, their vtable order has been fixed
      since Vista, and the alternative was taking a whole audio library into a
      process with a 50MB budget. Slot numbers are commented with their methods,
      since an off-by-one there is a crash rather than a compile error.
      Decisions worth recording:
      * **Polling, not the event callback.** WASAPI does not raise its event for a
        loopback stream while nothing is playing, so an event-driven loopback
        capture stops until audio resumes — and with it any chance of noticing the
        gap. Polling at half the device period sees the silence.
      * **Silence is written, gaps are not closed.** This is the correctness point
        of the whole phase and lives in the tested `AudioTimeline`: closing a gap
        moves every later sample earlier relative to the video, cumulatively and
        permanently.
      * **Shared mode, never exclusive** — exclusive would take the device away
        from the game.
      * **No resampling.** The endpoint's rate and channel count are captured and
        muxed as they are; only float32→int16 is converted, because the AAC
        encoder wants it and it is a multiply and a clamp.
      * WASAPI's buffer is released immediately after copying, because holding it
        stalls the audio endpoint for every application on the machine, not just
        Frost.
      * A silent packet's buffer contents are *undefined*, so it is written as
        real silence rather than copied.
      * An endpoint reporting 24-bit integer samples is refused with an
        actionable message rather than silently misinterpreted.
      * The **AAC encode is software**, via the Sink Writer's own encoder. That
        does not conflict with the hardware-encoding-only rule, which is about the
        frame-rate-sized video workload: AAC at 160kbps is a fraction of a percent
        of one core. Stated here so it is a decision, not an oversight.
      A device disappearing mid-session (headphones unplugged, default device
      changed) stops the audio track and leaves the video running.
      `Frost.Engine.exe --clip-test [seconds] [dir]` exercises the whole path and
      fails with distinct exit codes if the clip is silent or never written.
      Verified: 47 audio tests (format, conversion, buffer, timeline, pipeline),
      including routing with zero allocation on the capture thread. The WASAPI
      interop itself compiles but was not executed — needs Windows.
- [x] Optional mic capture as a second track, user-toggleable
      The microphone is a **separate track**, not mixed into the game audio, so
      it can be muted, re-levelled or dropped in an editor afterwards without
      touching what the game sounded like. `Mp4Muxer` was generalised from one
      audio stream to a small set (system on track 0, microphone on track 1),
      with the writer thread interleaving whichever queued sample is oldest —
      feeding the MP4 sink a whole session's video before any audio would make it
      hold the lot in memory.
      `Audio/MicrophoneState.cs` carries the live mute flag and gain. It is a
      shared object rather than a settings value because muting has to take effect
      on the *next audio block*, not on the next capture restart: the hotkey
      thread flips it, the capture thread reads it per block, and neither waits
      for the other. `Toggle()` is a compare-and-exchange loop, so two concurrent
      toggles cannot both observe "unmuted" and both write "muted" — there is a
      test that hammers it from two threads and checks every flip is accounted
      for. Gain is clamped on assignment, so a bad settings value cannot produce a
      deafening track.
      Muting **silences** rather than dropping the block, for the same reason the
      gap filler exists: a shorter track shifts everything after it and desyncs
      the recording.
      Alt+F2 is now a default binding, and the hotkey path works end to end
      (router → dispatcher → `MicrophoneState`), tested. Pressing it with
      microphone capture off says so rather than doing nothing silently.
      `--clip-test --mic` adds the second track to the end-to-end verb.
      Verified: 14 microphone tests on top of the audio suite. One hazard worth
      recording: changing `ISessionWriter.TryWriteAudio` to take a track index
      left two test fakes on the old signature, and because the interface has a
      default implementation they silently stopped receiving audio rather than
      failing to compile. The tests caught it, but default interface methods will
      hide that kind of drift.

## Phase 7 — Shell UI

> **Blocked on this build host.** `Frost.Shell` is WinUI 3, and its XAML compiler
> is Windows-only — the project cannot be compiled here at all, let alone run. Per
> the spec's own rule ("don't mark a checkbox done because the code compiles" —
> and here it would not even compile), none of these can be honestly checked off
> from Linux. Two ways forward, and the choice is a real one:
>
> 1. **Split it.** Put the Shell's *policy* — gallery listing/sorting, rename and
>    delete, trim-range validation, thumbnail cache eviction, the
>    foreground-window check that gates Mica — into portable code that is tested
>    here, and leave only XAML and WinUI plumbing for a Windows session. This is
>    the same shape used for capture, encode and IPC, and would make the eventual
>    Windows work small and mechanical. The checkboxes still could not be ticked
>    until the XAML builds.
> 2. **Write it blind.** Produce the full WinUI 3 app unverified. Fast to write,
>    but it would be the first code in this repo that has never been compiled, and
>    checking the boxes would be dishonest.
>
> Phases 8–10 have the same problem to varying degrees: Phase 8 (tray, autostart,
> toast) is Windows-only but *compiles* here, so it can be done to the same
> standard as Phases 1–6; Phase 9 needs real hardware to measure; Phase 10 needs
> Windows MSIX tooling.
>
> **Taking option 1.** Phases 8 and 10 are done to the extent this host allows, so
> the remaining honest work is the Shell's policy layer. Each task below gets its
> portable half built and tested here; the box stays unchecked until the XAML it
> needs has actually been compiled on Windows, and the note says exactly what is
> left to do there.

- [ ] WinUI 3 shell: dashboard (engine status, quick record/clip buttons)
      **Policy half done and tested; XAML pending a Windows host.**
      `Frost.Shared/Shell/DashboardModel.cs` projects an `EngineStatus` — or its
      absence — into every string the status panel shows and every button it
      enables. It is a pure projection with no WinUI in it, which is what makes
      it testable here, but that is not the only reason: the interesting bugs on
      a dashboard are not layout, they are what it claims when the thing it
      describes is missing or broken.
      Three that the tests pin down. A null status is a first-class state rather
      than a defaulted record, because `new EngineStatus()` renders as
      "0 x 0 @ 0, 0 clips saved" — a working Engine reporting zeroes, not no
      Engine; every unknown number is `null`. A faulted Engine offers nothing
      that needs the encoder *even while it still reports itself armed*, which is
      exactly what happens when the fault arrives after arming, and a live clip
      button that silently does nothing reads as a broken app. And `IsArmed` and
      `IsRecordingSession` are orthogonal, not two values of one enum, because
      the spec requires both at once off a single encode.
      Quick-clip buttons are generated from the user's own `SaveClip` hotkey
      assignments, so the dashboard and the keyboard can never disagree about
      what "clip" means. A preset longer than the buffer currently holds stays
      pressable but reports what it would really save: a greyed-out button eleven
      seconds into a session looks broken, where "60s (11s available)" is just
      true.
      Verified: 31 tests, including the zero-`MaxClipSeconds` divide, a buffer
      overshooting its own maximum, whitespace fault messages (serialisation
      turns absent strings into empty ones, and an empty fault would grey out the
      whole dashboard), and `NaN` from a corrupt settings file.
      Left for Windows: `App.xaml`, the window shell with Mica, and a
      `DashboardPage` that binds to this model — plus the `app.manifest` the
      csproj deliberately does not reference yet.
- [ ] Clip gallery: thumbnails, rename, delete, reveal, quick trim
      **Policy half done and tested; XAML pending a Windows host.**
      Three portable pieces in `Frost.Shared/Shell/`.
      `GalleryView.cs` — sort, filter, search. `OrderBy` is used deliberately
      because it is a stable sort: two clips saved in the same second would
      otherwise swap places on every refresh, and the thumbnails visibly shuffle
      under the cursor. `EmptyReason` distinguishes "nothing recorded yet" from
      "nothing matches your search", because telling someone with four hundred
      clips and a typo to press Alt+F10 while playing is the kind of small
      wrongness that makes an app feel careless. `SelectionAfterRemoving` keeps a
      run of deletes going instead of landing on nothing after each one.
      `ClipRename.cs` — validation, and a plan that names *both* files. Renaming
      a clip is two moves, the video and its `.frost.json` sidecar; forgetting
      the second is how a clip loses its bookmarks and its game name while
      appearing to rename fine. The video moves first, because a sidecar without
      a video is invisible and harmless where the reverse loses data.
      Validation is stricter than the file system: a trailing dot or space is
      refused rather than silently stripped by Windows into a name the user did
      not type, and `CON`/`NUL`/`COM1` are refused because they fail at the
      file-system layer with an error that never mentions reserved names.
      `TrimPlanner.cs` — the one that carries the spec's "re-mux not re-encode".
      A re-mux copies encoded samples untouched, so the output can only begin on
      a keyframe; starting mid-GOP makes the first second unwatchable. The left
      handle therefore snaps *backwards*, never to the nearest keyframe: at 7.4s
      with keyframes every 2s, nearest would be 8s and would discard the 0.6s the
      user was trying to keep, which is the one thing a trim UI must never do
      silently. The extra footage at the front is harmless and the UI says
      "starts 1.4s earlier to keep it lossless" rather than hiding it. The right
      handle needs no alignment — a decoder stops where the samples stop.
      Verified: 57 tests. The trim ones include the too-short check being applied
      *after* alignment (a 0.4s request that alignment turns into a valid 2s clip
      must be allowed), a keyframe index that does not cover the requested start
      being refused rather than guessed at, and the binary search checked against
      a linear scan across every quarter-second of a 100-second clip.
      One finding worth keeping: `Path.GetInvalidFileNameChars` and
      `Path.GetDirectoryName` report the *host's* rules, so on Linux they accept
      `:` and `?` and do not split a backslash path at all. Frost validates names
      for a Windows file system whatever it is compiled on, so the rules are
      written out explicitly — which is both more correct and what makes them
      testable here.
      Left for Windows: the thumbnail grid and its cache, reveal-in-Explorer
      (`SHOpenFolderAndSelectItems`), the trim UI, and the Sink Writer re-mux
      that executes a `TrimRange`.
- [ ] Settings UI covering everything in Phase 4's schema
      **Policy half done and tested; XAML pending a Windows host.**
      `Frost.Shared/Shell/SettingsEditor.cs` describes the page as data — 33 rows
      across the eight schema sections, each with a label, a control kind and
      whether the change only takes effect on re-arming. That shape exists for
      one reason: "covering everything in Phase 4's schema" is exactly the kind
      of requirement that rots silently, because a hand-written XAML page cannot
      be checked against the schema at all. A descriptor list can, and
      `SettingsUiTests` reflects over `FrostSettings` to assert both directions —
      every schema property has a row, and every row a property, so a renamed
      setting cannot leave a control bound to nothing (a silent no-op in XAML).
      A third test asserts the reflection finds more than 25 settings, so a
      reflection bug cannot make the other two pass vacuously.
      The reflection lives in the test project, not in `Frost.Shared`: the Engine
      is published AOT, and reflecting over the schema at runtime is both an
      IL2075 trim warning and something the product never needs to do. Caught by
      the build — the first version had it in the library.
      No bounds are restated in the UI layer. `SettingsEditor.Validate` calls
      `SettingsStore.Normalise`, so the page and a hand-edited file cannot
      disagree about what is valid and both produce the same wording. A test
      pins that a correction's `Field` key matches a row's `Path`, so the page
      can highlight the row a correction is about.
      `Frost.Shared/Shell/HotkeyRebind.cs` is the rebinding validation, and it
      exists because of the `WH_KEYBOARD_LL` choice: `RegisterHotKey` refuses a
      combination another app owns, a hook does not. A hook sees everything and
      does *not* swallow the keystroke, so a bare `G` fires every time someone
      types "gg" in chat — the character does something, a clip saves, and
      nothing connects the two for the user. Bare keys are therefore refused,
      except F13–F24 (what a gaming keyboard's macro key emits, and the ideal
      clip button) and Print Screen. Ctrl+Alt+Del, Alt+Tab, Alt+F4 and bare
      Escape are refused as taken before Frost can see them.
      Conflict detection keys on action *and* duration, because several
      `SaveClip` assignments coexist and are told apart only by their duration —
      keying on action alone would let the 15s hotkey silently steal the 30s
      one's key. Durations are compared with a tolerance since they round-trip
      through JSON as doubles, and an exact compare would eventually stop
      recognising a hotkey as itself. A conflict with a *disabled* assignment is
      still refused, with the message saying so: it would conflict the moment it
      was switched back on.
      Verified: 32 tests, including that the shipped default hotkeys pass the
      same check the rebind UI applies (a default the page would refuse to let
      you re-enter is a contradiction), and that an unparseable binding in a
      hand-edited file is ignored rather than crashing the settings page.
      Left for Windows: the XAML pages themselves, the key-capture control that
      turns a keypress into a `HotkeyBinding`, and the folder picker.
- [ ] Mica/animations verified to be inactive while a game window has focus
      **Policy half done and tested; the on-screen verification needs Windows.**
      `Frost.Shared/Shell/VisualEffectsPolicy.cs` is the spec's sixth constraint
      as code, and writing it turned up something worth recording: the visible
      effects are not the expensive part. Mica is composited by the system and
      costs almost nothing once the window stops being redrawn. The real cost of
      a Shell left open behind a game is the **status poll** — a dashboard asking
      the Engine for state every 250ms wakes two processes, serialises JSON and
      marshals to a UI thread four times a second, forever, while the user is
      playing and cannot see any of it. So `StatusPollInterval` goes to null in
      `BackgroundWhileGaming`, not just Mica and animations.
      Four states rather than a foreground/background boolean, because alt-tabbed
      to a browser is not the same as a game having focus: `Background` keeps a
      5-second poll so a window brought forward is not showing a minute-old
      buffer length, while `BackgroundWhileGaming` and `Hidden` spend nothing.
      `ShouldRefreshOnActivation` is the necessary other half — with the poll
      stopped, the dashboard holds whatever was true when the game took focus, so
      it must refresh on activation rather than up to half a second later.
      How the Shell knows a game has focus is deliberately not decided here: it
      is a compositor-level observation (the foreground window is not ours and
      covers a monitor) made in the Windows layer and passed in as a flag. Frost
      never injects into or reads from a game process, so nothing better is
      available and nothing better is wanted.
      Verified: 17 tests. Two are about states disagreeing: our own focused
      window is never classified as a game playing (the flags disagree for a
      moment during an alt-tab, and resolving it the other way would blank the
      window the user just switched to), and a minimised window is `Hidden` even
      when Windows still calls it foreground. One asserts every state's
      `MayDoRecurringWork` agrees with whether it carries a poll interval, so the
      two cannot drift; one asserts an unknown state *throws* rather than
      falling through to the foreground budget, because a new state that quietly
      costs a game its frame times is exactly this constraint's failure mode.
      Left for Windows — and this is the task's actual acceptance criterion:
      observe on a real machine that the backdrop and transitions are off while
      a game is focused, and confirm with PresentMon (Phase 9's procedure) that
      leaving the Shell open changes nothing about the game's frame times.

## Phase 8 — System integration
- [x] Tray icon with quick actions (start/stop, open gallery)
      `Tray/TrayMenuModel.cs` (portable) decides what the menu contains;
      `Windows/Tray/TrayIcon.cs` turns that into an `HMENU` and nothing else. The
      split is deliberate: what can be *wrong* here is which items appear, what
      they say and when they are greyed out, and all of that is now tested.
      The guiding rule is that the tray must never offer an action that would
      fail — "Save clip" with nothing buffered, or "Stop recording" when nothing
      is recording, is worse than absent, because the user clicks and gets
      nothing with no way to know why. So the clip item is disabled (and shows
      how much is buffered when it is not), recording controls disappear when the
      Engine is unreachable, and a faulted Engine offers only "open the window"
      and "quit". "Open Frost" and "Exit" are always available — a tray app you
      cannot quit from the tray is a support ticket.
      Implementation notes worth keeping: its own `frost-tray` thread at
      BelowNormal, because `TrackPopupMenu` blocks its thread for as long as the
      menu is on screen and that must never be the keyboard hook's thread or
      capture's; a message-only window (`HWND_MESSAGE`), since that is all
      `Shell_NotifyIcon` needs; `TPM_RETURNCMD` so the chosen item comes back
      inline rather than as a `WM_COMMAND` from somewhere else; the icon is
      re-added on Explorer's `TaskbarCreated` broadcast, because otherwise an
      Explorer crash silently removes the only way to reach the app; the window
      procedure catches everything, since an exception escaping one tears down the
      process; and the tooltip is only pushed to the shell when its text actually
      changes, so an idle Engine does no shell work.
      Verified: 17 menu-model tests, including that separators never lead, trail
      or double up, and that the tooltip stays inside Windows' 128-character limit
      (a silently cut tooltip looks like a bug). The Win32 side compiles but was
      not executed — needs Windows.
- [x] Autostart with Windows (optional, user-toggled)
      `Startup/AutostartPlan.cs` (portable) chooses the mechanism and enumerates
      what has to be removed to undo it; `Windows/Startup/AutostartManager.cs`
      does the registry and WinRT work.
      **Task Scheduler is deliberately not an option**, and that is the decision
      that matters here. It is the usual way apps get "run at logon", and the
      usual source of the orphaned entries Phase 10 forbids: an MSIX package
      cannot remove a scheduled task it created, so uninstalling leaves a task
      pointing at a deleted executable that Windows then reports as a failure at
      every logon. The cheapest way to guarantee nothing is left behind is to
      never create anything the uninstaller cannot reach. So a packaged build uses
      the MSIX `StartupTask` extension (removed with the package, and visible to
      the user in Task Manager's Startup tab, which is where they will look) and
      an unpackaged build uses the per-user Run key, which Frost owns and removes.
      Neither needs elevation — a background recorder has no business asking for
      admin.
      Once the user disables the entry in Task Manager, Windows does not let the
      app re-enable it and returns `DisabledByUser`. That is reported honestly
      rather than leaving a settings toggle that silently springs back; the same
      goes for a policy block. A moved or reinstalled build is detected
      (`IsRunValueStale`) and rewritten, because a stale Run entry fails at every
      logon with no visible error.
      Run commands are quoted whether or not the path contains a space — an
      unquoted path with a space is the classic Windows mis-launch, where
      `C:\Program` gets tried first — and a path containing a quote is refused
      outright rather than allowed to inject arguments.
      Verified: 13 plan tests, including one asserting no mechanism ever produces a
      scheduled task, and that the packaged mechanism leaves Frost nothing to
      clean up. The registry/WinRT side compiles but was not executed — needs
      Windows.
- [x] Toast feedback on clip save (static, pre-rendered, <1.5s)
      `Overlay/ToastPolicy.cs` (portable) owns the lifetime rules;
      `Windows/Overlay/ToastOverlay.cs` puts a pre-rendered image on screen.
      The spec allows exactly one UI cost while a game has focus, so the design
      is built to be only that:
      * **Pre-rendered.** Every toast image is drawn once at startup into a 32-bit
        premultiplied DIB. Showing one is a single `UpdateLayeredWindow` call that
        hands Windows finished pixels — there is no `WM_PAINT` handler and nothing
        is drawn at show time. The visible set is a closed enum precisely so the
        whole set *can* be pre-rendered; anything needing text composed at show
        time (a filename, a counter) would mean rendering while a game is in the
        foreground.
      * **Under 1.5s, enforced not hoped for.** 1.2s default against a 1.5s
        ceiling, and a configured value over the ceiling is clamped rather than
        throwing — a config asking for three seconds should get a compliant toast,
        not a startup failure. A test hammers 200 requests and confirms nothing
        stays up beyond the ceiling after the last one.
      * **Replaces, never stacks.** Two toasts at once would be two windows, and a
        queue of them could outlast the ceiling between them.
      * **Feedback on the keypress.** "Saving clip…" goes up immediately and is
        superseded by "Clip saved ✓" when the write finishes, which is what makes
        the sub-150ms budget reachable without waiting on a disk.
      * **Idle costs nothing.** With nothing on screen the overlay thread blocks
        in `GetMessage`; with something up it waits exactly until it should come
        down.
      Window style notes: `WS_EX_NOACTIVATE` because stealing focus from a
      fullscreen game is the worst thing an overlay can do; `WS_EX_TRANSPARENT` so
      clicks pass through; `WS_EX_TOOLWINDOW` to stay out of Alt-Tab; topmost so
      it shows over a borderless game; top-right placement to avoid the
      bottom-left where games put killfeeds and chat. The panel is filled by
      writing premultiplied pixels directly rather than through GDI, because GDI's
      drawing calls do not produce premultiplied alpha and the result is a bright
      fringe around the panel.
      Verified: 15 policy tests. The Win32/GDI side compiles but was not executed
      — needs Windows.

## Phase 9 — Performance pass

> **Cannot be completed on this build host.** Every task in this phase asks for
> *measured numbers*, and the Windows capture/encode path does not run here. The
> harness is built and the procedures are documented, so a Windows run is a
> handful of commands — but the boxes stay unchecked, because writing figures into
> the README that were never measured would be worse than leaving it blank.
>
> What exists now:
> * `Diagnostics/PerformanceBudget.cs` encodes the spec's five budget lines as
>   data, so a run is checked mechanically rather than by reading a table. An
>   unmeasured line reports "not measured" and `IsFullyMet` is explicitly false
>   when anything is missing — "we did not check" must never read as "it passed".
>   13 tests.
> * `Frost.Engine.exe --benchmark [seconds]` measures the three lines measurable
>   without a game (idle CPU, idle memory, recording CPU) and exits non-zero if a
>   *measured* line is over budget.
> * `Frost.Engine.exe --encode-test` already checks GPU engine attribution from
>   PDH counters (Phase 2).
> * README now carries the budget table with every result blank and marked as
>   such, plus the full procedure for the two lines that need a real game
>   (PresentMon before/after, comparing 95th/99th percentile frame times rather
>   than averages, since a capture tool's cost shows up as occasional long frames)
>   and a screen-observed measurement for hotkey-to-toast latency.


- [ ] PresentMon before/after comparison documented in README
- [ ] Idle and active CPU/RAM/GPU numbers documented against the budget above
- [ ] Any budget miss investigated and fixed or explicitly justified

## Phase 10 — Packaging

> **Cannot be completed on this build host.** Both tasks are about what a real
> install and uninstall *do* on a real Windows machine, and neither the MSIX
> tooling (`.wapproj` needs the Windows Application Packaging targets from Visual
> Studio) nor an installed package exists here. The boxes stay unchecked rather
> than being checked on the strength of a manifest that has never been built.
>
> What exists now:
> * `src/Frost.Package/Package.appxmanifest` — the real manifest. `StartupTask`
>   with `TaskId="FrostEngineAutostart"` and `Enabled="false"` on
>   `Frost.Engine.exe`; capabilities exactly `runFullTrust`, `graphicsCapture`,
>   `microphone` and deliberately not `broadFileSystemAccess`; MinVersion
>   10.0.18362.0 to match `TargetPlatformMinVersion`.
> * `src/Frost.Package/README.md` — the build command and what in the manifest is
>   load-bearing and why.
> * `tests/Frost.Engine.Tests/FrostPackageTests.cs` — 10 structural tests over the
>   manifest. The one that matters most asserts
>   `StartupTask/@TaskId == AutostartPlan.StartupTaskId`: `StartupTask.GetAsync`
>   throws at runtime when they drift and the exception does not say why.
> * `src/Frost.Engine/Startup/UninstallCheck.cs` — the orphan policy, with 13
>   tests. `RegistryAutostart` and `ScheduledTask` are orphans (each keeps
>   pointing at a deleted executable at every logon); settings, clips and logs are
>   reported as kept-on-purpose, so "we left files behind" cannot be mistaken for
>   "we left a broken autostart behind". A reinstall keeps the user's hotkeys and
>   nobody's recordings get deleted because they uninstalled the recorder.
>
> Still to write on a Windows host: a `Frost.Package.wapproj`, and a
> `--uninstall-check` verb wiring `UninstallCheck` to real registry and
> filesystem enumeration (`HKCU\...\Run`, Task Scheduler, `%AppData%\Frost`,
> the clip directory) so the second task can be verified by running one command
> after an uninstall instead of by hand.
>
> **Genuine external dependency:** a package that installs without developer mode
> needs signing — a trusted (EV or Store) code-signing certificate. That is a
> human decision and a purchase, not something the loop can do.

- [ ] MSIX package builds and installs cleanly
- [ ] Uninstall leaves no orphaned scheduled tasks / registry autostart entries
