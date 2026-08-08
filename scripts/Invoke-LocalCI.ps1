<#
.SYNOPSIS
  Local CI for pCUE. Build, UI layout check, packaging. Stops at the first failure.

.DESCRIPTION
  Same shape as Faganas Light's scripts\Invoke-LocalCI.ps1: a short sequential pipeline that
  exits non-zero the moment a stage fails, so it works equally well by hand or from a hook.

  Stages:
    1. Debug build   - must produce no NEW warnings beyond the known baseline.
    2. UI layout     - renders every window off-screen and fails on overlapping controls.
                       This exists because overlaps kept shipping: BATT LOW sat on top of the
                       tachometer status, "RPM:" on the fan selector, the hold status on
                       "Auto connect", and the Commander "Status:" label on its own value. Each
                       was found by eye, and the battery one was invisible until the moment it
                       mattered. All are the same one-line mistake in absolute margins.
    3. Release pack  - proves the installer still builds. Skipped with -NoPack, because it bumps
                       the version.

.EXAMPLE
  pwsh scripts\Invoke-LocalCI.ps1
  pwsh scripts\Invoke-LocalCI.ps1 -NoPack
#>
[CmdletBinding()]
param([switch]$NoPack)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = $false

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "`n=== $Name ===" -ForegroundColor Yellow
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Get-MSBuild {
    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($p) { return $p }
    }
    foreach ($ed in 'Enterprise','Professional','Community','BuildTools') {
        $p = "${env:ProgramFiles}\Microsoft Visual Studio\2022\$ed\MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $p) { return $p }
    }
    throw 'MSBuild.exe not found.'
}
$msbuild = Get-MSBuild

Step 'Build (Debug)' {
    & $msbuild (Join-Path $root 'pCUE\pCUE.csproj') /t:Rebuild /p:Configuration=Debug /v:minimal /nologo
}

Step 'UI layout' {
    # WPF needs an STA thread, which pwsh 7 does not provide, so shell out to Windows PowerShell.
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-UiLayout.ps1')
}

if (-not $NoPack) {
    Step 'Release pack' {
        & pwsh -NoProfile -File (Join-Path $root 'build\pack-release.ps1')
    }
}

Write-Host "`nAll checks passed." -ForegroundColor Green
exit 0
