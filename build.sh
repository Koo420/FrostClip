#!/usr/bin/env bash
# Linux/macOS verification path for Frost, for build hosts without PowerShell.
# Equivalent to: ./build.ps1 on a non-Windows host.
#
# Builds Frost.Linux.slnf (Shared + Engine + Tests). The Engine's
# net8.0-windows10.0.19041.0 TFM compiles here too, so the WGC / D3D11 /
# Media Foundation / WASAPI interop is genuinely type-checked; only the WinUI 3
# Shell is skipped, because its XAML compiler is Windows-only.
set -euo pipefail

CONFIGURATION="${1:-Debug}"
cd "$(dirname "$0")"

step() { printf '\n==> %s\n' "$1"; }

step "restore (Frost.Linux.slnf)"
dotnet restore Frost.Linux.slnf

step "build (Frost.Linux.slnf, $CONFIGURATION)"
dotnet build Frost.Linux.slnf --configuration "$CONFIGURATION" --no-restore

step "test (Frost.Engine.Tests)"
dotnet test tests/Frost.Engine.Tests/Frost.Engine.Tests.csproj \
    --configuration "$CONFIGURATION" --no-build

printf '\nBuild OK.\n'
