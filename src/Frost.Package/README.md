# Frost.Package

The MSIX packaging project. Windows-only: `.wapproj` needs the Windows
Application Packaging tooling that ships with Visual Studio, so this does not
build on a non-Windows host and is excluded from `Frost.Linux.slnf`.

## Building

```powershell
msbuild src\Frost.Package\Frost.Package.wapproj /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=SideloadOnly
```

Producing a package for the Store, or one that installs without a developer
certificate, needs signing — see the note in `PROGRESS.md` under Phase 10, which
records this as the one genuine external dependency in the build.

## What is load-bearing in the manifest

- **`StartupTask/@TaskId` must equal `AutostartPlan.StartupTaskId`.**
  `StartupTask.GetAsync` throws at runtime otherwise and the exception does not
  mention the id. `FrostPackageTests` asserts the two match, so a rename cannot
  drift.
- **`Enabled="false"`** on the startup task. A recorder that runs at every logon
  without being asked is the behaviour people uninstall over; the Settings toggle
  enables it.
- **No scheduled task and no Run key are declared.** An MSIX package cannot remove
  either on uninstall, and an orphan pointing at a deleted executable makes
  Windows report a failure at every logon. `AutostartPlan` encodes the same rule
  in code.
- **Capabilities are minimal.** `graphicsCapture` for WGC, `microphone` only
  because the mic track is opt-in, `runFullTrust` because the Engine needs D3D11,
  Media Foundation and a global keyboard hook. Deliberately no
  `broadFileSystemAccess`: clips go to Videos or a folder the user picks, and the
  picker grants access to both.
