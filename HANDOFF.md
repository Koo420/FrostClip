# Handoff — Frost build loop

Read this plus `PROGRESS.md` before resuming. `PROGRESS.md` is the authoritative
task state and carries a per-task note on what was verified and how.

## Where things stand

**26 of 31 checklist tasks done, and the remaining five are each half-built.**
Phases 0–6 and 8 complete. 750 unit tests, all passing, clean build across both
target frameworks (`./build.sh`, or `./build.ps1` on Windows).

Every task this host can finish is finished. The five unchecked boxes all need a
Windows machine — three to measure, one to compile XAML, one to build an MSIX —
and each has had its portable half written and tested here, with a `PROGRESS.md`
note saying precisely what is left to do on Windows.

| Phase | State |
| --- | --- |
| 0 — Scaffolding | done |
| 1 — Capture core | done |
| 2 — Hardware encode | done |
| 3 — Ring buffer + instant clip | done |
| 4 — Hotkeys, settings, IPC | done |
| 5 — Full-session + autoclip | done |
| 6 — Audio (loopback + mic) | done |
| 7 — Shell UI | 4 tasks: policy done + tested, **XAML needs Windows** |
| 8 — System integration | done (tray, autostart, toast) |
| 9 — Performance pass | 3 tasks, **blocked on real hardware** |
| 10 — Packaging | 2 tasks, **blocked on Windows MSIX tooling + a cert** |

## Resume here

Every task that can be done on a Linux host is done. The five remaining tasks all
need a Windows machine, and `PROGRESS.md` has a blocker preamble for each phase
explaining exactly what is missing and what was built around it.

The Shell's whole policy layer now lives in `src/Frost.Shared/Shell/`, tested on
this host: `DashboardModel`, `GalleryView`, `ClipRename`, `TrimPlanner`,
`SettingsEditor`, `HotkeyRebind`, `VisualEffectsPolicy`. Phase 7 on Windows is
therefore XAML, binding and Win32 plumbing — no remaining decisions.

**On a Windows box, in this order:**

1. **Phase 9** — run the measurements. `Frost.Engine.exe --benchmark [seconds]`
   covers idle CPU, idle memory and recording CPU and exits non-zero if a
   *measured* line is over budget. `--encode-test` checks GPU engine attribution
   from PDH counters. The two lines needing a real game (game frame time with the
   Engine ring-buffering, and with the Shell open) need PresentMon — the procedure
   is in the README, and it says compare 95th/99th percentile frame times, not
   averages, because a capture tool's cost shows up as occasional long frames.
   `Diagnostics/PerformanceBudget.cs` encodes the budget as data so a run is
   checked mechanically; an unmeasured line reports "not measured" and
   `IsFullyMet` is false when anything is missing, so "we did not check" can never
   read as "it passed". Then fill in the README table and check the boxes.
2. **Phase 7** — the WinUI 3 Shell: `App.xaml`, a window with Mica, and three
   pages binding to the models above. `Frost.Shell.csproj` exists and is in
   `Frost.sln` (not in `Frost.Linux.slnf`) and deliberately does not yet
   reference an `app.manifest`; `EngineConnection` in `Frost.Shared/Ipc/` is the
   client side, already self-healing and tested. Specifically still to write:
   the thumbnail grid and its cache, reveal-in-Explorer
   (`SHOpenFolderAndSelectItems`), the Sink Writer re-mux that executes a
   `TrimRange`, the key-capture control that turns a keypress into a
   `HotkeyBinding`, the folder picker, and the compositor-level check that sets
   `isGameInForeground` for `VisualEffectsPolicy.Classify`.
   Task 4's acceptance criterion is an observation, not a unit test: watch that
   the backdrop and transitions are off while a game is focused, and confirm
   with PresentMon that leaving the Shell open changes nothing about the game's
   frame times.
3. **Phase 10** — write `Frost.Package.wapproj`, build the package, and add a
   `--uninstall-check` verb wiring `UninstallCheck` to real registry and
   filesystem enumeration so the uninstall task is verified by one command rather
   than by hand.

## Build host constraints — important

The loop is running on **Linux**. This shapes what "verified" can mean and the
notes in `PROGRESS.md` are careful about it:

- `net8.0-windows10.0.26100.0` **does compile here** (WinRT projections via
  `Microsoft.Windows.SDK.NET.Ref`, plus `Vortice.Windows` for D3D11/DXGI/Media
  Foundation) with `EnableWindowsTargeting=true`. So all the Windows interop is
  genuinely type-checked, not stubbed.
- It cannot *execute* here. `Frost.Engine` multi-targets
  `net8.0;net8.0-windows10.0.26100.0`; everything platform-agnostic builds for
  both and is unit-tested for real, and the Windows-native implementations live
  under `src/Frost.Engine/Windows/` and compile only under the Windows TFM.
- `Frost.Shell` (WinUI 3) cannot build here at all — its XAML compiler is
  Windows-only. Same for `src/Frost.Package` (`.wapproj` needs the Windows
  Application Packaging targets from Visual Studio). Both are in `Frost.sln`; the
  Linux path uses `Frost.Linux.slnf`.
- **The .NET SDK is not preinstalled and `dot.net` is blocked by the egress
  policy.** `apt-get update && apt-get install -y dotnet-sdk-8.0` works
  (nuget.org is reachable). Do this first in a fresh container.
- PowerShell is not installed, so `build.sh` is what the loop actually runs;
  `build.ps1` is the Windows entry point the spec asked for.

## The design decision that matters most

**The Sink Writer is used as a muxer, not as the encoder.** Phase 2's checklist
said "encode via Media Foundation Sink Writer", but Phase 3 needs a ring buffer
of trailing *encoded* data and Phase 5 needs full-session recording alongside it
"without double encoding" — and the Sink Writer gives no access to encoded
frames. So Frost drives the hardware encoder MFT directly
(`Windows/Encode/HardwareVideoEncoder.cs`) and the Sink Writer muxes
already-encoded samples in passthrough mode (`Windows/Encode/Mp4Muxer.cs`). One
encode, any number of destinations via `FanOutSampleSink`. This is recorded as a
deviation in `PROGRESS.md` with the reasoning; **do not "fix" it back**.

Other deviations, all noted in `PROGRESS.md`: the ring buffer is in-memory only
(a disk-backed rolling buffer would write every frame to the SSD for as long as
the Engine is merely armed); `build.sh` exists alongside `build.ps1`; and the AAC
audio encode is software, which does not conflict with the hardware-only rule —
that rule is about video, and AAC on a CPU is negligible.

## Gotchas already paid for — don't rediscover these

1. **`System.Text.Json`'s source generator does not run property initialisers.**
   A property absent from the JSON comes back `null`, not as its default. This
   bit the settings loader (an older settings file would have crashed startup);
   `SettingsStore.Normalise` coalesces every section. Same hazard in any new IPC
   contract.
2. **The default JSON encoder escapes the plus sign** as `+`, which made
   every hotkey in the settings file read `"Alt+F10"`. Use
   `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` anywhere a human reads the JSON.
3. **`JsonSerializerOptions` are frozen once passed to a source-gen context**, so
   build the options and the context once, statically.
4. **Vortice API names drift from the C++ ones** (`pool.CreateCaptureSession(item)`,
   not `item.CreateCaptureSession()`; `EnumAdapters1` takes `uint`; `MFTEnumEx`
   wants `out`, not `ref`; `OutputStreamInfo.Size`, not `CbSize`). There is a
   metadata dumper recipe that saved a lot of guessing: a tiny console app using
   `MetadataLoadContext` over `~/.nuget/packages/vortice.*/3.6.2/lib/net8.0/*.dll`
   (needs `sharpgen.runtime/2.2.0-beta` on the resolver path too).
5. **`IsBorderRequired` needs the 26100 SDK projection**, which is why the TFM
   targets 26100 while `TargetPlatformMinVersion` stays at 19041 (Windows 10
   1903, where WGC shipped). Anything above the floor is `ApiInformation`-guarded.
6. **Don't hardcode WinRT IIDs.** `WinRT.GuidGenerator.GetIID(typeof(T))` gives
   the authoritative one from projection metadata. Only `IClosable`,
   `IDirect3DDxgiInterfaceAccess`, `ID3D11Texture2D`,
   `IGraphicsCaptureItemInterop` and `IGraphicsCaptureItem` are literals, and
   only because they cannot be reached through the projection.
7. **A `record`'s `with` expression copies private fields.** A cached label on a
   hotkey assignment survived a rewrite of the label it was derived from and went
   stale. Effective labels are now precomputed in `HotkeyRouter` and carried on
   `HotkeyFired`. Don't cache derived state on a record you clone.
8. **An interface default implementation hides a signature change.** When
   `ISessionWriter.TryWriteAudio` gained a track index, two test fakes kept the
   old signature and silently stopped receiving audio instead of failing to
   compile. Tests caught it. Watch for this on any `ISessionWriter`/`ISampleSink`
   change.
9. **Exactly-zero allocation assertions are flaky** under tiered-compilation OSR.
   `AllocationAssert.NoPerIterationAllocation` uses a bound of <1 byte per
   iteration (the smallest object is 24 bytes, so anything real is caught).
10. **`Path`'s file-name rules are the *host's*, not Windows'.** On Linux
   `Path.GetInvalidFileNameChars` accepts `:` and `?`, and
   `Path.GetDirectoryName` does not split a backslash path at all. Anything
   validating a name or path for the target file system has to spell the rules
   out — see `ClipRename`. Same for test fixtures: build Windows paths with a
   literal `\`, not `Path.Combine`.
11. **Reflection in `Frost.Shared` is an AOT trim warning.** The Engine is
   published AOT, so reflecting over the settings schema there produced IL2075.
   Checks that only matter while the code is changing belong in the test
   project — `SettingsUiTests` reflects, `SettingsEditor` does not.

## Windows run, 2026-09-14 - what it found

The verification script (`verify-windows.ps1`) was run on a real machine. Nine of
its ten automated steps passed, including hardware encoder selection, GPU engine
attribution, clips with audio and a mic track, the performance budget and a
ten-minute memory soak. Three things came out of it:

1. **A real shipped bug.** The Engine could never open its IPC pipe on Windows:
   `PipeOptions.CurrentUserOnly` plus an explicit `PipeSecurity` throws. Fixed by
   deleting the ACL branch. The reason 750 tests missed it is the lesson - the
   ACL sat behind `#if WINDOWS`, so it compiled here and never ran. **Treat any
   `#if WINDOWS` block as untested code**, and prefer one code path.
2. **A test that could only pass on the machine that wrote it.** The line-ending
   assertion depended on `core.autocrlf`, which rewrites files on checkout.
   Anything asserting on file bytes has to survive being cloned by someone else.
3. **Smart App Control blocks unsigned builds from running**, not just from
   installing. See the Phase 10 note in `PROGRESS.md`; this is now a
   prerequisite rather than a packaging detail.

## What still needs real Windows hardware

These are implemented and compile, but their headline claims rest on the API
choice rather than on a test run, and `PROGRESS.md` says so per task. When a
Windows box is available, run in this order:

1. `Frost.Engine.exe --displays` / `--windows` — target discovery works.
2. `Frost.Engine.exe --encoders` — enumeration finds a hardware MFT and picks
   the right one.
3. `Frost.Engine.exe --encode-test 30` — the whole chain, and it self-checks GPU
   engine attribution from PDH counters (exit 6 if encoding is not on the video
   encode engine, exit 5 if no keyframes).
4. `Frost.Engine.exe --clip-test 30 [dir] [--mic]` — fills the ring buffer, then
   saves a clip with audio; `--mic` adds the microphone as a second track. This
   is the one that exercises `AudioTimeline`'s silence-insertion against real
   WASAPI packet timing.
5. `Frost.Engine.exe --ipc-server` — the Engine/Shell pipe against a real
   Windows named pipe (it is tested here, but over Unix domain sockets).
6. `Frost.Engine.exe --soak 10` — ten-minute capture with a memory verdict
   (exit 2 if working set drifts over 8MB or steady-state allocation over 4KB/s).
7. `Frost.Engine.exe --benchmark 60` — the Phase 9 budget lines.
8. The keyboard hook under a fullscreen-exclusive title — the one claim with no
   local test at all.

## Conventions in this codebase

- Comments explain *why*, especially where a choice looks odd (why polling WGC
  instead of `FrameArrived`, why `CopyResource` over `CopySubresourceRegion`,
  why the filler-frame slot trick is free). Keep that up; several of those
  comments are the only record of a decision.
- Hot-path code proves it does not allocate with
  `GC.GetAllocatedBytesForCurrentThread()` over a long loop, rather than
  asserting it in a comment. There are such tests for the pacer, texture pool,
  frame queue, frame router, sample arena, ring buffer, fan-out sink, audio
  timeline and hotkey router. Add one for anything new on capture/encode/hook
  threads.
- Platform-agnostic policy is deliberately extracted so it can be tested on any
  host: `FrameRouter`, `EncoderSelector`, `EncodeAttribution`, `HookWatchdog`,
  `ClipNaming`, `AudioTimeline`, `TrayMenuModel`, `AutostartPlan`, `ToastPolicy`,
  `PerformanceBudget`, `UninstallCheck`, and all of `Frost.Shared/Shell/`. Prefer
  that shape over testing through Windows types — it is why 750 tests run on a
  host that cannot run the app.
- Tests that assert *that* something happens use generous timeouts; only tests
  asserting *how fast* are tight. The container has run 3× slower on some days.
- Test names read as sentences describing the behaviour and the failure they
  guard against.
- One commit per checklist task, with the reasoning in the body.

## Things the spec forbids — do not let these creep back in

- No DLL injection, no reading game memory, no hooking the game's D3D present.
  All autoclipping is compositor-level.
- **No OCR and no game-specific templates for killfeed/UI parsing.** The spec
  calls this out by name. If asked, push back and offer the bookmark hotkey
  instead.
- No software x264 fallback. If there is no hardware encoder, surface
  `NoHardwareEncoderException` clearly and stop.
- No Task Scheduler for autostart, ever — it is the usual source of the orphaned
  entries Phase 10 forbids. `AutostartPlan` encodes this and `UninstallCheck`
  treats any scheduled task as an orphan by definition.

## Loop mechanics

`/loop` re-reads this file and `PROGRESS.md`, finds the first unchecked task,
implements it completely, verifies, checks the box with a note, commits, and
continues. Work goes on branch `claude/sleepy-feynman-91levv`; it is pushed.

Note that the first unchecked task is now one of the blocked ones, so a fresh
loop on this host will correctly stop and say so rather than finding work.
