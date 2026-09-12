#!/usr/bin/env pwsh
<#
.SYNOPSIS
    CI-less local build for Frost: restore, build, test, optionally publish.

.DESCRIPTION
    Run this from the repo root on a Windows machine with the .NET 8 SDK and the
    Windows App SDK workload installed. It builds every project in Frost.sln
    (Engine, Shell, Shared, Tests) and runs the Engine test suite.

    On a non-Windows host the WinUI 3 Shell cannot be built at all (its XAML
    compiler is Windows-only), so the script transparently falls back to
    Frost.Linux.slnf, which contains Shared + Engine + Tests. The Engine's
    Windows TFM still compiles there, so all WGC / Media Foundation / WASAPI
    interop is genuinely type-checked; only the Shell is skipped.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER SkipTests
    Build only.

.PARAMETER Publish
    Additionally publish the Engine (AOT, win-x64) and the Shell to ./artifacts.
    Windows only.

.EXAMPLE
    ./build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$Publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
Push-Location $repoRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        Pop-Location
        exit $LASTEXITCODE
    }
}

try {
    $onWindows = $IsWindows -or ($PSVersionTable.PSVersion.Major -le 5)
    if ($onWindows) {
        $target = 'Frost.sln'
    }
    else {
        $target = 'Frost.Linux.slnf'
        Write-Host 'Non-Windows host: building Frost.Linux.slnf (WinUI Shell skipped).' -ForegroundColor Yellow
    }

    Invoke-Step "restore ($target)" { dotnet restore $target }
    Invoke-Step "build ($target, $Configuration)" {
        dotnet build $target --configuration $Configuration --no-restore
    }

    if (-not $SkipTests) {
        Invoke-Step 'test (Frost.Engine.Tests)' {
            dotnet test tests/Frost.Engine.Tests/Frost.Engine.Tests.csproj `
                --configuration $Configuration --no-build --verbosity normal
        }
    }

    if ($Publish) {
        if (-not $onWindows) { throw 'Publishing requires Windows.' }
        Invoke-Step 'publish Frost.Engine (AOT, win-x64)' {
            dotnet publish src/Frost.Engine/Frost.Engine.csproj `
                --configuration Release --runtime win-x64 --self-contained `
                --framework net8.0-windows10.0.19041.0 `
                --output artifacts/Frost.Engine
        }
        Invoke-Step 'publish Frost.Shell (win-x64)' {
            dotnet publish src/Frost.Shell/Frost.Shell.csproj `
                --configuration Release --runtime win-x64 --self-contained `
                --output artifacts/Frost.Shell
        }
    }

    Write-Host ''
    Write-Host 'Build OK.' -ForegroundColor Green
}
finally {
    Pop-Location
}
