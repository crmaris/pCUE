<#
.SYNOPSIS
    Authenticode-sign pCUE artifacts (the app exe and/or the installer).

.DESCRIPTION
    Releases ship UNSIGNED. Run this once you hold a code-signing certificate to produce
    trusted artifacts. Two certificate sources are supported, and NEITHER requires putting a
    secret on the command line:

      * -Thumbprint : a cert already installed in a Windows cert store (CurrentUser\My or
                      LocalMachine\My). No password needed here - a hardware/EV token prompts
                      for its own PIN.
      * -PfxPath    : a .pfx file. Omit -PfxPassword and signtool prompts for it
                      interactively, which keeps it out of your shell history and out of any
                      log this script writes.

    Everything is SHA-256 signed, RFC-3161 timestamped (so signatures stay valid after the
    certificate expires) and then verified with `signtool verify /pa`.

    ORDER MATTERS. Signing setup.exe does NOT sign the payload inside it. To get both the
    installer and the installed application signed, sign the staged app exe FIRST and build the
    installer from that stage - which is exactly what
    `build\pack-release.ps1 -Thumbprint <x>` does for you. Running this script standalone after
    an unsigned pack is the wrong order: the portable .zip was already built around the
    unsigned exe. Re-pack with a certificate instead.

    Adapted from the same pattern used in Powenetics V3's build\sign.ps1.

.EXAMPLE
    pwsh build\sign.ps1 -Thumbprint ABCD1234...

.EXAMPLE
    pwsh build\sign.ps1 -PfxPath C:\certs\cybenetics.pfx
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Store')]
    [string]$Thumbprint,

    [Parameter(Mandatory, ParameterSetName = 'Pfx')]
    [string]$PfxPath,

    [Parameter(ParameterSetName = 'Pfx')]
    [string]$PfxPassword,

    # Files to sign. Defaults to every signable artifact currently in artifacts\.
    [string[]]$Files,

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [string]$Description = 'pCUE'
)

$ErrorActionPreference = 'Stop'

function Resolve-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $root = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $found = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw "signtool.exe not found. Install the Windows 10/11 SDK (Signing Tools)."
}

$signtool = Resolve-SignTool
Write-Host "signtool: $signtool"

$repo = Split-Path -Parent $PSScriptRoot

if (-not $Files -or $Files.Count -eq 0) {
    # The staged app exe (so the INSTALLED app is signed too) plus the installer itself.
    $Files = @(
        Join-Path $repo 'artifacts\stage\pCUE\pCUE.exe'
    )
    $setups = Get-ChildItem (Join-Path $repo 'artifacts') -Filter 'pCUE_*_setup.exe' `
                            -ErrorAction SilentlyContinue
    foreach ($s in $setups) { $Files += $s.FullName }
}

# Certificate selection. A password, if one is needed at all, is either typed into signtool's
# own prompt or supplied by the caller - this script never stores or echoes it.
$certArgs = @()
if ($PSCmdlet.ParameterSetName -eq 'Store') {
    $certArgs = @('/sha1', $Thumbprint)
} else {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $certArgs = @('/f', $PfxPath)
    if ($PfxPassword) { $certArgs += @('/p', $PfxPassword) }
}

$missing = $Files | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Warning "Skipping files that do not exist:`n  $($missing -join "`n  ")"
    $Files = $Files | Where-Object { Test-Path $_ }
}
if (-not $Files) { throw "No existing files to sign. Build them first with build\pack-release.ps1." }

foreach ($f in $Files) {
    Write-Host "`n=== Signing: $f ===" -ForegroundColor Cyan
    & $signtool sign @certArgs /fd SHA256 /tr $TimestampUrl /td SHA256 /d $Description $f
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE) for $f" }

    Write-Host "--- Verifying: $f ---" -ForegroundColor Cyan
    & $signtool verify /pa /v $f
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed ($LASTEXITCODE) for $f" }
}

Write-Host "`nAll files signed and verified." -ForegroundColor Green
Write-Host "NOTE: any .sha256 written before signing is now stale - signing changes the file." `
           -ForegroundColor Yellow
