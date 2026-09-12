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
- [ ] Manual smoke test: capture runs 10 minutes with stable memory (no leak)

## Phase 2 — Hardware encode pipeline
- [ ] Enumerate available hardware MFTs (NVENC/AMF/QuickSync) at startup
- [ ] Encode captured frames via Media Foundation Sink Writer using hw MFT
- [ ] Graceful, clearly-surfaced error if no hardware encoder is present
- [ ] Verify GPU "Video Encode" engine is the one doing the work, not CPU/3D engine

## Phase 3 — Ring buffer + instant clip
- [ ] Rolling in-memory/disk ring buffer holding configurable trailing duration
- [ ] Hotkey → finalize trailing N seconds to an MP4 on disk
- [ ] Multiple duration presets bound to different hotkeys simultaneously

## Phase 4 — Hotkeys, settings, IPC
- [ ] Low-level keyboard hook works while a fullscreen-exclusive game has focus
- [ ] Settings schema + persistence (JSON in %AppData%)
- [ ] Named-pipe IPC server in Engine; basic client in Shell; round-trip tested

## Phase 5 — Full-session recording + autoclip heuristics
- [ ] Independent full-session recording toggle, no double-encode with ring buffer
- [ ] Audio loudness-spike bookmark detection (opt-in, off by default)
- [ ] Manual "bookmark" hotkey tags a timestamp without a full export

## Phase 6 — Audio
- [ ] WASAPI loopback capture muxed into clips
- [ ] Optional mic capture as a second track, user-toggleable

## Phase 7 — Shell UI
- [ ] WinUI 3 shell: dashboard (engine status, quick record/clip buttons)
- [ ] Clip gallery: thumbnails, rename, delete, reveal, quick trim
- [ ] Settings UI covering everything in Phase 4's schema
- [ ] Mica/animations verified to be inactive while a game window has focus

## Phase 8 — System integration
- [ ] Tray icon with quick actions (start/stop, open gallery)
- [ ] Autostart with Windows (optional, user-toggled)
- [ ] Toast feedback on clip save (static, pre-rendered, <1.5s)

## Phase 9 — Performance pass
- [ ] PresentMon before/after comparison documented in README
- [ ] Idle and active CPU/RAM/GPU numbers documented against the budget above
- [ ] Any budget miss investigated and fixed or explicitly justified

## Phase 10 — Packaging
- [ ] MSIX package builds and installs cleanly
- [ ] Uninstall leaves no orphaned scheduled tasks / registry autostart entries
