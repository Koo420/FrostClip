#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs every Windows-only verification Frost's checklist is waiting on, in
    order, and writes one transcript.

.DESCRIPTION
    Phases 9 and 10 and part of Phase 7 cannot be completed on a non-Windows
    host: they ask for measured numbers, an MSIX build, and an on-screen
    observation. This script runs the measurable part unattended and records
    exactly what happened, so the results can be read - or handed back to the
    build loop - without anyone transcribing console output by hand.

    Nothing here is destructive. It builds, it captures a few short recordings
    into a temporary folder, and it reads performance counters. The two things
    it cannot do are the two that need a human: playing a game while it measures
    (step 8) and looking at the screen (step 9).

.PARAMETER Seconds
    Length of each capture test. 30 is enough for a verdict; 60 gives a steadier
    CPU average.

.PARAMETER OutputPath
    Where the transcript goes. Defaults to ./artifacts/windows-verification.md.

.PARAMETER SkipSoak
    Skip the 10-minute memory soak, which is by far the longest step.

.EXAMPLE
    ./verify-windows.ps1
    ./verify-windows.ps1 -Seconds 60 -SkipSoak
#>
[CmdletBinding()]
param(
    [int]$Seconds = 30,
    [string]$OutputPath,
    [switch]$SkipSoak
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'   # a failing step is a result, not a crash

# Resolved defensively rather than from $PSScriptRoot alone. Windows PowerShell
# 5.1 leaves $PSScriptRoot empty inside a param() default, and it is also empty
# when a script is dot-sourced or piped rather than run with -File, so each
# source is tried in turn and the working directory is the last resort.
$scriptPath =
    if ($PSCommandPath) { $PSCommandPath }
    elseif ($PSScriptRoot) { Join-Path $PSScriptRoot 'verify-windows.ps1' }
    elseif ($MyInvocation.MyCommand.Path) { $MyInvocation.MyCommand.Path }
    else { $null }

$repoRoot = if ($scriptPath) { Split-Path -Parent $scriptPath } else { (Get-Location).Path }

# If that landed somewhere without the solution in it, the working directory is
# more likely right than a guess, and saying so beats building paths that fail
# one step later with a less obvious message.
if (-not (Test-Path (Join-Path $repoRoot 'Frost.sln'))) {
    if (Test-Path (Join-Path (Get-Location).Path 'Frost.sln')) {
        $repoRoot = (Get-Location).Path
    } else {
        Write-Error "Could not find Frost.sln. Run this from the repository root."
        exit 2
    }
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'artifacts/windows-verification.md'
}
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "frost-verify-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

$report = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()

function Add-Line([string]$text) { $report.Add($text) }

function Invoke-Step {
    param(
        [string]$Name,
        [string]$Why,
        [scriptblock]$Body,
        [hashtable]$ExitMeanings = @{}
    )

    Write-Host "==> $Name" -ForegroundColor Cyan

    $global:LASTEXITCODE = 0

    # Pre-assigned: when a command cannot be launched at all, the assignment
    # below never happens and StrictMode then throws on the first use of it.
    $output = ''
    $launched = $true

    try {
        $output = & $Body 2>&1 | Out-String

        # Captured immediately, before anything else can overwrite it. This is
        # the load-bearing line of the whole script: a command that does not
        # exist, or cannot be launched, writes an error and leaves
        # $LASTEXITCODE at 0 - so judging by exit code alone reports PASS for a
        # step that never ran. A verification script that reports a false green
        # is worse than no script.
        $launched = $?
    }
    catch {
        $output = ($_ | Out-String)
        $launched = $false
    }

    # Read through Get-Variable: StrictMode turns a bare $LASTEXITCODE into an
    # error when no external command has run yet in this session.
    $code = (Get-Variable -Name LASTEXITCODE -Scope Global -ValueOnly -ErrorAction SilentlyContinue)
    if ($null -eq $code) { $code = 0 }

    if (-not $launched -and $code -eq 0) {
        $code = 127
        $output = "$($output.TrimEnd())`n[the command could not be run - reported as exit 127]"
    }

    $verdict = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    $meaning = if ($ExitMeanings.ContainsKey($code)) { $ExitMeanings[$code] } else { $null }

    # The Shell step is informational: it is expected to fail until Phase 7's
    # XAML exists, so it is reported but never counted as a failure.
    $isExpectedFailure = $Name -like '*expected to fail*'
    if ($isExpectedFailure -and $code -ne 0) { $verdict = 'EXPECTED' }

    $results.Add([pscustomobject]@{ Name = $Name; Code = $code; Verdict = $verdict })

    # Capped, because the point of this report is that it can be pasted back:
    # a full test run is tens of thousands of lines and the tail is where the
    # verdict and any failure live.
    $lines = $output.TrimEnd() -split "`r?`n"
    $cap = 60

    Add-Line ""
    Add-Line "## $Name"
    Add-Line ""
    Add-Line "_${Why}_"
    Add-Line ""
    Add-Line "**$verdict** (exit $code)$(if ($meaning) { " - $meaning" })"
    Add-Line ""

    Add-Line '```'
    if ($lines.Count -gt $cap) {
        Add-Line "[$($lines.Count - $cap) earlier lines omitted]"
        Add-Line ($lines[-$cap..-1] -join "`n")
    } else {
        Add-Line ($lines -join "`n")
    }
    Add-Line '```'

    Write-Host "    $verdict (exit $code)" -ForegroundColor $(
        switch ($verdict) { 'PASS' { 'Green' } 'EXPECTED' { 'Yellow' } default { 'Red' } })

    # Printed to the console as well as the report, because the console is what
    # gets copied into a bug report. A verdict with the diagnostics only in a
    # file the reader has to be told to open separately is how four rounds of
    # guesswork happen.
    if ($verdict -eq 'FAIL') {
        $tail = if ($lines.Count -gt 25) { $lines[-25..-1] } else { $lines }
        Write-Host "    --- last $($tail.Count) lines ---" -ForegroundColor DarkGray
        foreach ($line in $tail) { Write-Host "    $line" -ForegroundColor DarkGray }
        Write-Host "    --- end ---" -ForegroundColor DarkGray
    }
}

# --- environment ------------------------------------------------------------

Add-Line "# Frost - Windows verification run"
Add-Line ""
Add-Line "Generated by ``verify-windows.ps1`` on $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')."
Add-Line ""
Add-Line "| | |"
Add-Line "| --- | --- |"
# Each probed separately and tolerantly: the hardware table is context for
# reading the numbers, never a reason to lose a run that already happened.
function Probe([scriptblock]$block) {
    try { $value = & $block; if ($value) { "$value" } else { 'unknown' } }
    catch { 'unknown' }
}

Add-Line "| Machine | $env:COMPUTERNAME |"
Add-Line "| OS | $(Probe { $os = Get-CimInstance Win32_OperatingSystem; "$($os.Caption) build $($os.BuildNumber)" }) |"
Add-Line "| CPU | $(Probe { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name }) |"
Add-Line "| RAM | $(Probe { "$([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) GB" }) |"
Add-Line "| GPU | $(Probe { (Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" }) -join '<br>' }) |"
Add-Line "| .NET SDK | $(Probe { dotnet --version }) |"
Add-Line "| Capture length | ${Seconds}s |"

# --- prerequisites ----------------------------------------------------------

# Checked up front and reported as one clear line. Without this, a missing SDK
# surfaces as three build steps failing with 0x8013... exit codes, which says
# nothing about what to install.
$sdkVersion = Probe { dotnet --version }
$sdkOk = $sdkVersion -ne 'unknown' -and $sdkVersion -match '^(\d+)\.' -and [int]$Matches[1] -ge 8

if (-not $sdkOk) {
    $detail = if ($sdkVersion -eq 'unknown') {
        'No .NET SDK was found on PATH.'
    } else {
        "Found .NET SDK $sdkVersion, but Frost targets net8.0 and needs 8.0 or later."
    }

    Add-Line ""
    Add-Line "## Cannot run: no usable .NET SDK"
    Add-Line ""
    Add-Line $detail
    Add-Line ""
    Add-Line "Install it, reopen the terminal so PATH is picked up, and run this again:"
    Add-Line ""
    Add-Line '```'
    Add-Line 'winget install Microsoft.DotNet.SDK.8'
    Add-Line '```'
    Add-Line ""
    Add-Line "Or download the SDK (not the Runtime) from"
    Add-Line "<https://dotnet.microsoft.com/download/dotnet/8.0>."
    Add-Line ""
    Add-Line "The WinUI 3 Shell additionally needs the Windows App SDK workload, and"
    Add-Line "Phase 10's MSIX package needs MSBuild with the Windows Application Packaging"
    Add-Line "tooling - both of which come with Visual Studio."

    $report -join "`n" | Set-Content -Path $OutputPath -Encoding utf8

    Write-Host ""
    Write-Host $detail -ForegroundColor Red
    Write-Host "  Install: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Yellow
    Write-Host "  Then reopen the terminal and run this script again." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Wrote $OutputPath"
    exit 3
}

# --- build ------------------------------------------------------------------

Invoke-Step -Name 'Build Engine, Shared and tests' `
    -Why 'The part of the solution that must pass. The Engine''s Windows target framework compiles on the Linux build host too, so this is a re-check rather than a first look - but it is the first time it has been built by a Windows SDK.' `
    -Body { dotnet build (Join-Path $repoRoot 'Frost.Linux.slnf') -c Release --nologo }

Invoke-Step -Name 'Unit tests' `
    -Why '750 tests, all of which already pass on the build host. A failure here means something is genuinely platform-dependent.' `
    -Body { dotnet test (Join-Path $repoRoot 'tests/Frost.Engine.Tests/Frost.Engine.Tests.csproj') -c Release --nologo --no-build }

# Attempted separately and expected to fail today: Frost.Shell has a csproj and
# no source files, because Phase 7's XAML is the work that needs this machine.
# Rolling it into the step above would report a FAIL that means nothing.
Invoke-Step -Name 'Build the WinUI 3 Shell (expected to fail until Phase 7)' `
    -Why 'Frost.Shell has no App.xaml or pages yet, so this should fail with CS5001 (no entry point). What matters is *how* it fails: a missing Windows App SDK workload or a broken csproj is a different message, and worth knowing before the XAML is written.' `
    -Body { dotnet build (Join-Path $repoRoot 'src/Frost.Shell/Frost.Shell.csproj') -c Release --nologo }

$engine = Join-Path $repoRoot 'src/Frost.Engine/bin/Release/net8.0-windows10.0.26100.0/Frost.Engine.exe'

if (-not (Test-Path $engine)) {
    $found = Get-ChildItem -Path (Join-Path $repoRoot 'src/Frost.Engine/bin') -Recurse -Filter 'Frost.Engine.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) { $engine = $found.FullName }
}

if (-not (Test-Path $engine)) {
    Add-Line ""
    Add-Line "## Everything below was skipped"
    Add-Line ""
    Add-Line "``Frost.Engine.exe`` was not produced, so there was nothing to run. Fix the build first."
    $report -join "`n" | Set-Content -Path $OutputPath -Encoding utf8
    Write-Host "`nWrote $OutputPath" -ForegroundColor Yellow
    exit 1
}

Add-Line ""
Add-Line "Engine under test: ``$engine``"

# --- the ordered verification list from HANDOFF.md --------------------------

# --- can the engine run at all? ---------------------------------------------

# Checked once, before the seven steps that all invoke it. Windows Application
# Control (Smart App Control on Windows 11, or a WDAC policy on a managed
# machine) blocks unsigned executables, and a freshly built Frost.Engine.exe is
# unsigned. When it blocks, every step fails identically with "An Application
# Control policy has blocked this file", which reads like seven broken features
# rather than one policy - and none of it is a defect in Frost.
$engineRuns = $false
$engineBlockReason = ''

try {
    $probe = & $engine --help 2>&1 | Out-String
    $engineRuns = $?
    if (-not $engineRuns) { $engineBlockReason = $probe.Trim() }
}
catch {
    $engineBlockReason = $_.Exception.Message
}

if (-not $engineRuns) {
    $isAppControl = $engineBlockReason -match 'Application Control|WDAC|blocked this file'

    # Smart App Control's state lives here: 0 off, 1 enforcing, 2 evaluation.
    $sacState = Probe {
        (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' `
            -Name 'VerifiedAndReputablePolicyState' -ErrorAction Stop).VerifiedAndReputablePolicyState
    }

    $sacLabel = switch ("$sacState") {
        '0' { 'off' }
        '1' { 'ON and enforcing - this is what is blocking the Engine' }
        '2' { 'in evaluation mode' }
        default { "unknown (registry value: $sacState)" }
    }

    Add-Line ""
    Add-Line "## Cannot run: the Engine is blocked from executing"
    Add-Line ""
    Add-Line "``Frost.Engine.exe`` built successfully but Windows refused to run it."
    Add-Line ""
    Add-Line "| | |"
    Add-Line "| --- | --- |"
    Add-Line "| Smart App Control | $sacLabel |"
    Add-Line "| Looks like app control | $isAppControl |"
    Add-Line ""
    Add-Line "What Windows said:"
    Add-Line ""
    Add-Line '```'
    Add-Line $engineBlockReason
    Add-Line '```'
    Add-Line ""
    Add-Line "This is not a defect in Frost. A freshly built executable is unsigned, and"
    Add-Line "Windows Application Control blocks unsigned binaries. It is the same problem"
    Add-Line "Phase 10 records as needing a code-signing certificate - it has simply arrived"
    Add-Line "earlier than expected, at the point of *running* the build rather than"
    Add-Line "installing it."
    Add-Line ""
    Add-Line "Diagnose which policy is responsible:"
    Add-Line ""
    Add-Line '```powershell'
    Add-Line 'Get-WinEvent -LogName "Microsoft-Windows-CodeIntegrity/Operational" -MaxEvents 20 |'
    Add-Line '    Where-Object { $_.Message -like "*Frost*" } | Format-List TimeCreated, Id, Message'
    Add-Line '```'

    $report -join "`n" | Set-Content -Path $OutputPath -Encoding utf8

    Write-Host ""
    Write-Host "Frost.Engine.exe built, but Windows will not run it." -ForegroundColor Red
    Write-Host "  Smart App Control: $sacLabel" -ForegroundColor Yellow
    Write-Host ""
    Write-Host $engineBlockReason -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "The seven capture and encode steps were skipped: they all invoke the Engine," -ForegroundColor Yellow
    Write-Host "so running them would report seven identical failures for one policy." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Wrote $OutputPath"
    exit 4
}

Invoke-Step -Name 'Capture target discovery' `
    -Why 'Windows.Graphics.Capture finds the monitors and windows it can record. Nothing else can work if this does not.' `
    -Body { & $engine --displays; & $engine --windows }

Invoke-Step -Name 'Hardware encoder enumeration' `
    -Why 'Frost is hardware-encode-only by design and never falls back to software x264. This lists the hardware MFTs present and which one it would pick.' `
    -Body { & $engine --encoders }

Invoke-Step -Name 'End-to-end encode, with GPU engine attribution' `
    -Why 'Records the primary display and writes an MP4, then self-checks from PDH counters that the work landed on the video encode engine rather than the CPU or the 3D engine.' `
    -ExitMeanings @{
        5 = 'no keyframes were produced - the encoder ran but the output is unusable'
        6 = 'encoding was NOT on the GPU video encode engine, which breaks the hardware-only rule'
        127 = 'the command could not be run at all'
    } `
    -Body { & $engine --encode-test $Seconds (Join-Path $scratch 'encode-test.mp4') }

Invoke-Step -Name 'Ring buffer to clip, with audio' `
    -Why 'Fills the ring buffer and saves a clip with a WASAPI loopback track. This is the first real exercise of AudioTimeline silence insertion against actual WASAPI packet timing, which unit tests can only approximate.' `
    -Body { & $engine --clip-test $Seconds $scratch }

Invoke-Step -Name 'Ring buffer to clip, with microphone as a second track' `
    -Why 'Same, plus the optional mic track. Needs a microphone present and microphone permission granted.' `
    -Body { & $engine --clip-test $Seconds $scratch --mic }

Invoke-Step -Name 'Performance budget' `
    -Why 'Measures the three budget lines that do not need a game running: Engine idle CPU, idle memory, and CPU while ring-buffering. Exits non-zero if a measured line is over budget.' `
    -Body { & $engine --benchmark $Seconds }

if (-not $SkipSoak) {
    Invoke-Step -Name 'Ten-minute memory soak' `
        -Why 'Long capture with a memory verdict, to catch a slow leak the short tests cannot see.' `
        -ExitMeanings @{ 2 = 'working set drifted over 8MB or steady-state allocation over 4KB/s' } `
        -Body { & $engine --soak 10 }
} else {
    Add-Line ""
    Add-Line "## Ten-minute memory soak"
    Add-Line ""
    Add-Line "Skipped (``-SkipSoak``). Run ``Frost.Engine.exe --soak 10`` before trusting the memory numbers."
}

Invoke-Step -Name 'IPC server' `
    -Why 'The Engine/Shell named pipe against a real Windows named pipe - it is tested on the build host, but there over Unix domain sockets. Starts the server, connects to it as the Shell would, and stops it.' `
    -Body {
        $stdout = Join-Path $scratch 'ipc-out.txt'
        $stderr = Join-Path $scratch 'ipc-err.txt'

        $proc = Start-Process -FilePath $engine -ArgumentList '--ipc-server' `
            -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        # Connecting as a client is the check that matters: enumerating
        # \\.\pipe\ only proves a name exists, and the failure mode worth
        # catching is a pipe that cannot be opened by the Shell. CurrentUserOnly
        # is passed here too, so this also exercises the owner verification the
        # real client relies on.
        $connected = $false
        $lastError = ''

        for ($attempt = 1; $attempt -le 10 -and -not $connected; $attempt++) {
            Start-Sleep -Milliseconds 500

            if ($proc.HasExited) {
                $lastError = "the Engine exited with code $($proc.ExitCode) before accepting a connection"
                break
            }

            try {
                $client = New-Object System.IO.Pipes.NamedPipeClientStream(
                    '.', 'Frost.Engine.v1', [System.IO.Pipes.PipeDirection]::InOut,
                    ([System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly))
                $client.Connect(1000)
                $connected = $client.IsConnected
                $client.Dispose()
            }
            catch {
                $lastError = $_.Exception.Message
            }
        }

        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

        # The Engine's own log is the useful part when this fails.
        foreach ($file in @($stdout, $stderr)) {
            if ((Test-Path $file) -and (Get-Item $file).Length -gt 0) {
                Write-Output "--- $(Split-Path -Leaf $file) ---"
                Get-Content $file | ForEach-Object { Write-Output $_ }
            }
        }

        if ($connected) {
            Write-Output 'Connected to the pipe as the Shell would, then disconnected cleanly.'
            $global:LASTEXITCODE = 0
        } else {
            Write-Output "Could not connect to 'Frost.Engine.v1': $lastError"
            $global:LASTEXITCODE = 1
        }
    }

# --- what the script cannot do ----------------------------------------------

Add-Line ""
Add-Line "## Still needs a person"
Add-Line ""
Add-Line "Three things cannot be automated, and they are the acceptance criteria for the"
Add-Line "boxes that remain unchecked."
Add-Line ""
Add-Line "1. **The low-level keyboard hook under a fullscreen-exclusive game.** This is the"
Add-Line "   one claim in the whole build with no test behind it. Start a game in"
Add-Line "   fullscreen-exclusive mode (not borderless) and confirm the clip hotkey fires."
Add-Line "   ``WH_KEYBOARD_LL`` was chosen over ``RegisterHotKey`` precisely for this case."
Add-Line ""
Add-Line "2. **PresentMon, before and after.** Capture a game's frame times with the Engine"
Add-Line "   idle, then ring-buffering, then with the Shell also open. Compare the 95th and"
Add-Line "   99th percentiles, not the averages: a capture tool's cost shows up as"
Add-Line "   occasional long frames, which an average hides. README has the procedure."
Add-Line ""
Add-Line "3. **Mica and animations off while a game has focus.** Leave the Shell open behind"
Add-Line "   a focused game and confirm the backdrop and transitions are not drawing."
Add-Line ""
Add-Line "And Phase 10's MSIX package needs ``msbuild`` with the Windows Application"
Add-Line "Packaging tooling, plus a code-signing certificate to install without developer"
Add-Line "mode - the one genuine purchase in the build."

# --- summary ----------------------------------------------------------------

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("")
$summary.Add("## Summary")
$summary.Add("")
$summary.Add("| Step | Result |")
$summary.Add("| --- | --- |")
foreach ($r in $results) { $summary.Add("| $($r.Name) | **$($r.Verdict)** (exit $($r.Code)) |") }

$failed = @($results | Where-Object { $_.Verdict -eq 'FAIL' })
$summary.Add("")
$summary.Add($(if ($failed.Count -eq 0) {
    "Every automated step passed."
} else {
    "$($failed.Count) step(s) failed: $(($failed | ForEach-Object { $_.Name }) -join ', ')."
}))

# Summary goes near the top, under the environment table.
$headerEnd = ($report | Select-String -SimpleMatch '| Capture length' | Select-Object -First 1).LineNumber
$final = @()
$final += $report[0..($headerEnd - 1)]
$final += $summary
$final += $report[$headerEnd..($report.Count - 1)]

$final -join "`n" | Set-Content -Path $OutputPath -Encoding utf8

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Wrote $OutputPath" -ForegroundColor Green
if ($failed.Count -gt 0) {
    Write-Host "Failing steps printed their last 25 lines above; the full output is in the report." -ForegroundColor Yellow
}
Write-Host ""
foreach ($r in $results) {
    Write-Host ("  {0,-55} {1}" -f $r.Name, $r.Verdict) -ForegroundColor $(if ($r.Verdict -eq 'PASS') { 'Green' } else { 'Red' })
}

exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
