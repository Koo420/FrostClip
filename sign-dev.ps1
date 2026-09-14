#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Signs Frost.Engine.exe with a local development certificate.

.DESCRIPTION
    Windows code-integrity (WDAC) policies refuse to run unsigned executables.
    A freshly built Frost.Engine.exe is unsigned, so on a machine with such a
    policy every capture and encode verb fails before it starts.

    This creates a self-signed code-signing certificate, trusts it on this
    machine, and signs the Engine with it. That is the standard development
    workaround and it is enough for WDAC policies that trust locally installed
    publishers.

    WHAT IT WILL NOT FIX: Smart App Control, which requires a signature from a
    certificate authority Microsoft already trusts plus file reputation. A
    self-signed certificate cannot satisfy it - there is no local workaround, and
    the only real options there are a purchased certificate or turning Smart App
    Control off (which cannot be undone without reinstalling Windows).

    WHAT IT COSTS: the certificate is added to this machine's Trusted Root and
    Trusted Publishers stores, which means anything signed with it is trusted
    until the certificate is removed. The private key stays on this machine and
    is not exportable, so the practical exposure is limited to someone who
    already has administrator access here. Run with -Remove to undo it
    completely.

    Requires an elevated prompt, because the machine trust stores do.

.PARAMETER Remove
    Delete the certificate from every store it was added to and stop trusting
    it. Does not unsign already-signed binaries; a rebuild replaces them.

.EXAMPLE
    ./sign-dev.ps1
    ./sign-dev.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$subject = 'CN=Frost Development Signing'
$friendlyName = 'Frost development code signing (safe to delete)'

$scriptPath =
    if ($PSCommandPath) { $PSCommandPath }
    elseif ($PSScriptRoot) { Join-Path $PSScriptRoot 'sign-dev.ps1' }
    else { $null }

$repoRoot = if ($scriptPath) { Split-Path -Parent $scriptPath } else { (Get-Location).Path }

if (-not (Test-Path (Join-Path $repoRoot 'Frost.sln'))) {
    $repoRoot = (Get-Location).Path
}

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    Write-Error @"
This needs an elevated prompt: it writes to the machine certificate stores.

Right-click Windows Terminal or PowerShell, "Run as administrator", then:
  cd $repoRoot
  ./sign-dev.ps1
"@
    exit 1
}

# --- removal ----------------------------------------------------------------

if ($Remove) {
    $removed = 0

    foreach ($store in @(
        'Cert:\CurrentUser\My',
        'Cert:\LocalMachine\Root',
        'Cert:\LocalMachine\TrustedPublisher')) {

        Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $subject } |
            ForEach-Object {
                Remove-Item $_.PSPath -Force
                Write-Host "Removed from $store (thumbprint $($_.Thumbprint))." -ForegroundColor Green
                $removed++
            }
    }

    if ($removed -eq 0) {
        Write-Host "Nothing to remove - no certificate with subject '$subject' was found." -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "Done. Already-signed binaries keep their signature but it is no longer" -ForegroundColor Green
        Write-Host "trusted; a rebuild replaces them with unsigned ones." -ForegroundColor Green
    }

    exit 0
}

# --- certificate ------------------------------------------------------------

$existing = Get-ChildItem 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if ($existing) {
    $cert = $existing
    Write-Host "Reusing the existing certificate (thumbprint $($cert.Thumbprint))." -ForegroundColor Cyan
} else {
    Write-Host "Creating a self-signed code-signing certificate..." -ForegroundColor Cyan

    # Code-signing EKU only, so trusting it cannot validate a TLS certificate or
    # anything else - it can only make code signed by this key run here.
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -FriendlyName $friendlyName `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(2)

    Write-Host "  thumbprint $($cert.Thumbprint)" -ForegroundColor DarkGray
}

# Root so the chain validates, TrustedPublisher so policies that check for an
# explicitly trusted publisher accept it.
foreach ($store in @('Root', 'TrustedPublisher')) {
    $path = "Cert:\LocalMachine\$store"
    $already = Get-ChildItem $path -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint }

    if ($already) {
        Write-Host "Already trusted in $store." -ForegroundColor DarkGray
        continue
    }

    $store_ = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $store, 'LocalMachine')
    $store_.Open('ReadWrite')
    $store_.Add($cert)
    $store_.Close()

    Write-Host "Trusted in LocalMachine\$store." -ForegroundColor Green
}

# --- signing ----------------------------------------------------------------

$targets = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter 'Frost.*.exe' `
    -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\bin\*' } |
    Select-Object -ExpandProperty FullName

if (-not $targets) {
    Write-Host ""
    Write-Host "No built executables found under src\. Build first:" -ForegroundColor Yellow
    Write-Host "  dotnet build Frost.Linux.slnf -c Release" -ForegroundColor Yellow
    exit 2
}

Write-Host ""

foreach ($target in $targets) {
    $result = Set-AuthenticodeSignature -FilePath $target -Certificate $cert `
        -HashAlgorithm SHA256 -ErrorAction Continue

    $colour = if ($result.Status -eq 'Valid') { 'Green' } else { 'Red' }
    Write-Host ("{0,-10} {1}" -f $result.Status, (Split-Path -Leaf $target)) -ForegroundColor $colour
    Write-Host "           $target" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Now try the verification again:" -ForegroundColor Cyan
Write-Host "  ./verify-windows.ps1 -SkipSoak" -ForegroundColor Cyan
Write-Host ""
Write-Host "Rebuilding replaces these binaries with unsigned ones, so re-run this" -ForegroundColor Yellow
Write-Host "script after every build. To undo everything: ./sign-dev.ps1 -Remove" -ForegroundColor Yellow
