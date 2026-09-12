# Handoff — Frost build loop

Written at the end of the first session. Read this plus `PROGRESS.md` before
resuming; `PROGRESS.md` is the authoritative task state and carries a per-task
note on what was verified and how.

## Where things stand

**15 of 31 checklist tasks done.** Phases 0–3 complete, Phase 4 two-thirds done.

| Phase | State |
| --- | --- |
| 0 — Scaffolding | done |
| 1 — Capture core | done |
| 2 — Hardware encode | done |
| 3 — Ring buffer + instant clip | done |
| 4 — Hotkeys, settings, IPC | keyboard hook done, settings done, **IPC not started** |
| 5–10 | not started |

316 unit tests, all passing. `./build.sh` (or `./build.ps1` on Windows) builds
and runs them.

## Resume here

The next unchecked task is **Phase 4, task 3: named-pipe IPC server in Engine,
basic client in Shell, round-trip tested.**

Good news for that one: `NamedPipeServerStream` works on Linux (Unix domain
sockets), so unlike most of the Windows surface the IPC round trip can be
genuinely executed and tested on this build host. Plan:

- `Frost.Shared/Ipc/` — message contracts as records plus a source-generated
  `JsonSerializerContext` (the Engine is AOT; see the gotcha below), a pipe-name
  constant, and length-prefixed framing.
- `Frost.Engine/Ipc/IpcServer.cs` — accept loop on its own thread, one client at
  a time is enough (there is only ever one Shell), must survive the Shell
  crashing or being killed mid-message without affecting a recording.
- `Frost.Shell/Ipc/IpcClient.cs` — connect, reconnect with backoff, and
  degrade to "Engine not running" rather than hanging the UI.
- Messages the later phases need: status ping (engine armed / recording / buffer
  duration held), clip-saved notification, hotkey-fired notification, request
  clip, toggle full-session, get/set settings, list displays and windows.

Then Phase 5 (full-session recording + autoclip), which is where the
`FanOutSampleSink` and `Bookmark` types already built get used.

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
  Windows-only. It is in `Frost.sln`; the Linux path uses `Frost.Linux.slnf`.
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
frames. So Frost drives the hardware encoder MFT directly and the Sink Writer
muxes already-encoded samples in passthrough mode. One encode, any number of
destinations via `FanOutSampleSink`. This is recorded as a deviation in
`PROGRESS.md` with the reasoning; **do not "fix" it back**.

Other deviations, both noted in `PROGRESS.md`: the ring buffer is in-memory only
(a disk-backed rolling buffer would write every frame to the SSD for as long as
the Engine is merely armed), and `build.sh` exists alongside `build.ps1`.

## Gotchas already paid for — don't rediscover these

1. **`System.Text.Json`'s source generator does not run property initialisers.**
   A property absent from the JSON comes back `null`, not as its default. This
   bit the settings loader (an older settings file would have crashed startup)
   and will bite the IPC contracts the same way. Coalesce, or make every field
   non-nullable with a required value.
2. **The default JSON encoder escapes `+` as `+`**, which made every hotkey
   in the settings file read `"Alt+F10"`. `SettingsStore` uses a relaxed
   encoder; do the same anywhere a human reads the JSON.
3. **`JsonSerializerOptions` are frozen once passed to a source-gen context**, so
   build the options and the context once, statically.
4. **Vortice API names drift from the C++ ones** (`pool.CreateCaptureSession(item)`,
   not `item.CreateCaptureSession()`; `EnumAdapters1` takes `uint`;
   `MFTEnumEx` wants `out`, not `ref`; `OutputStreamInfo.Size`, not `CbSize`).
   There is a metadata dumper recipe that saved a lot of guessing: a tiny console
   app using `MetadataLoadContext` over
   `~/.nuget/packages/vortice.*/3.6.2/lib/net8.0/*.dll` (needs
   `sharpgen.runtime/2.2.0-beta` on the resolver path too).
5. **`IsBorderRequired` needs the 26100 SDK projection**, which is why the TFM
   targets 26100 while `TargetPlatformMinVersion` stays at 19041 (Windows 10
   1903, where WGC shipped). Anything above the floor is `ApiInformation`-guarded.
6. **Don't hardcode WinRT IIDs.** `WinRT.GuidGenerator.GetIID(typeof(T))` gives
   the authoritative one from projection metadata. Only `IClosable`,
   `IDirect3DDxgiInterfaceAccess`, `ID3D11Texture2D`,
   `IGraphicsCaptureItemInterop` and `IGraphicsCaptureItem` are literals, and
   only because they cannot be reached through the projection.

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
4. `Frost.Engine.exe --soak 10` — ten-minute capture with a memory verdict
   (exit 2 if working set drifts over 8MB or steady-state allocation over 4KB/s).
5. The keyboard hook under a fullscreen-exclusive title — the one claim with no
   local test at all.

## Conventions in this codebase

- Comments explain *why*, especially where a choice looks odd (why polling WGC
  instead of `FrameArrived`, why `CopyResource` over `CopySubresourceRegion`,
  why the filler-frame slot trick is free). Keep that up; several of those
  comments are the only record of a decision.
- Hot-path code proves it does not allocate with
  `GC.GetAllocatedBytesForCurrentThread()` over a long loop, rather than
  asserting it in a comment. There are such tests for the pacer, texture pool,
  frame queue, frame router, sample arena, ring buffer, fan-out sink and hotkey
  router. Add one for anything new on capture/encode/hook threads.
- Platform-agnostic policy is deliberately extracted so it can be tested on any
  host (`FrameRouter`, `EncoderSelector`, `EncodeAttribution`, `HookWatchdog`,
  `ClipNaming`). Prefer that shape over testing through Windows types.
- Test names read as sentences describing the behaviour and the failure they
  guard against.
- One commit per checklist task, with the reasoning in the body.

## Loop mechanics

`/loop` re-reads this file and `PROGRESS.md`, finds the first unchecked task,
implements it completely, verifies, checks the box with a note, commits, and
continues. Work goes on branch `claude/sleepy-feynman-91levv`; it is pushed.
