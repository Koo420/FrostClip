# Frost

A high-performance Windows game clipping app. Press a key, get the last N seconds
of gameplay as an MP4. No overlay that renders every frame, no injection into the
game, no software encode.

## Why it is built this way

Frost's whole reason to exist is that it must not cost you frames. The design
follows from that:

| Decision | Reason |
| --- | --- |
| **Two processes** — `Frost.Engine.exe` (capture/encode/hotkeys) and `Frost.Shell.exe` (UI) | The UI can crash, hang or be closed without touching an in-progress recording. The Engine is the only thing running while you play. |
| **Windows.Graphics.Capture** for capture | Survives fullscreen-exclusive and hybrid-GPU laptops without stealing frames from the game, which is exactly where GDI `BitBlt` and DXGI Desktop Duplication fall over. |
| **Hardware encode only** (NVENC / AMD AMF / Intel QuickSync via Media Foundation MFTs) | A software x264 fallback would quietly eat the CPU headroom the game needs. If no hardware encoder exists, Frost says so instead. |
| **No DLL injection, no game memory reads, no D3D present hooks** | Anti-cheat ban risk, and unnecessary. Autoclip heuristics are compositor-level only. |
| **`WH_KEYBOARD_LL` hotkeys, not `RegisterHotKey`** | Bindings keep working when a fullscreen-exclusive game owns input focus. |
| **WinUI 3 Shell, idle while gaming** | Mica, acrylic and animations are only active while the Shell window itself is foregrounded. With the game in focus the only UI cost allowed is a static, pre-rendered "clip saved" toast on screen for under 1.5s. |

## Performance budget

These are requirements, not aspirations:

- Engine idle (armed, not recording): **< 1% CPU, < 50 MB RAM**.
- Engine ring-buffering 1080p60: **< 3% CPU** attributable to Frost, encode work on
  the GPU's Video Encode engine only, **no measurable game frame-time increase**.
- Hotkey press → toast on screen: **< 150 ms**.
- Shell open while recording: game frame time unaffected.

Measured numbers are recorded in [Performance](#performance) once Phase 9 runs on
real hardware.

## Repo layout

```
src/Frost.Engine        capture, encode, ring buffer, hotkeys, IPC server
src/Frost.Shell         WinUI 3 app: dashboard, gallery, settings
src/Frost.Shared        IPC contracts, settings schema, clip metadata
tests/Frost.Engine.Tests
```

## Building

Requires the .NET 8 SDK. On Windows, also the Windows App SDK for the Shell.

```powershell
./build.ps1                      # restore + build + test (Debug)
./build.ps1 -Configuration Release
./build.ps1 -Publish             # also AOT-publish the Engine to ./artifacts
```

`Frost.Engine` multi-targets `net8.0` and `net8.0-windows10.0.19041.0`. Everything
platform-agnostic (ring buffer, frame pacing, encoder selection, loudness
detection, retention, IPC) lives in the `net8.0` flavour so it is unit-testable on
any host; the Windows-native implementations live under `src/Frost.Engine/Windows`
and compile only under the Windows TFM. On a non-Windows host `build.ps1` (or
`build.sh`) builds `Frost.Linux.slnf`, which is everything except the WinUI Shell —
the Windows TFM still compiles, so the interop is genuinely type-checked.

## Performance

Not yet measured on real hardware; see Phase 9 in `PROGRESS.md`.
