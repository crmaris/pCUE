<#
.SYNOPSIS
  Local CI for pCUE. Build, UI layout check, packaging. Stops at the first failure.

.DESCRIPTION
  Same shape as Faganas Light's scripts\Invoke-LocalCI.ps1: a short sequential pipeline that
  exits non-zero the moment a stage fails, so it works equally well by hand or from a hook.

  Stages:
    1. Debug build   - must produce no NEW warnings beyond the known baseline
                       (scripts/WarningBaseline.txt; enforced by warning scan).
    2. Remote API    - runs a loopback server/client integration test, including authentication,
                       SSE snapshots, typed commands and protocol-version rejection.
    3. UI layout     - renders every window off-screen and fails on overlapping controls.
                       This exists because overlaps kept shipping: BATT LOW sat on top of the
                       tachometer status, "RPM:" on the fan selector, the hold status on
                       "Auto connect", and the Commander "Status:" label on its own value. Each
                       was found by eye, and the battery one was invisible until the moment it
                       mattered. All are the same one-line mistake in absolute margins.
    4. Sync controls - exercises the real WPF checkbox, numbers and sliders without hardware.
    5. Release pack  - proves the installer still builds. Skipped with -NoPack, because it bumps
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
    $buildLog = Join-Path ([System.IO.Path]::GetTempPath()) ('pcue-build-' + [Guid]::NewGuid().ToString('N') + '.log')
    & $msbuild (Join-Path $root 'pCUE\pCUE.csproj') /t:Rebuild /p:Configuration=Debug /m:1 /nr:false /v:normal /nologo /flp:"logfile=$buildLog;verbosity=normal" 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Debug build failed (exit $LASTEXITCODE). Log: $buildLog" }
    # Enforce the warning baseline: the four HidSharp HidDeviceLoader CS0612 warnings are known.
    # Lines are normalized to "warning CSxxxx: message" (paths/line numbers stripped) and deduped,
    # so rebuilds on different checkouts compare equal.
    $baseline = Join-Path $PSScriptRoot 'WarningBaseline.txt'
    $warnings = @()
    if (Test-Path $buildLog) {
        $warnings = @(Select-String -Path $buildLog -Pattern 'warning (CS\d+): (.+?) \[' |
            ForEach-Object { ('warning ' + $_.Matches[0].Groups[1].Value + ': ' + $_.Matches[0].Groups[2].Value).Trim() } |
            Sort-Object -Unique)
    }
    if (Test-Path $baseline) {
        $allowed = @(Get-Content $baseline | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*#' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
        $extra = @($warnings | Where-Object { $allowed -notcontains $_ })
        $missing = @($allowed | Where-Object { $warnings -notcontains $_ })
        if ($extra.Count -gt 0 -or $missing.Count -gt 0) {
            Write-Host 'Build warning baseline mismatch:' -ForegroundColor Red
            $extra | ForEach-Object { Write-Host "  NEW: $_" -ForegroundColor Red }
            $missing | ForEach-Object { Write-Host "  GONE: $_" -ForegroundColor Yellow }
            throw 'Debug build warnings differ from scripts/WarningBaseline.txt.'
        }
    } elseif ($warnings.Count -gt 0) {
        Write-Host 'Build warnings (no baseline file to compare):' -ForegroundColor Yellow
        $warnings | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    try { if (Test-Path $buildLog) { Remove-Item $buildLog -Force } } catch { }
}

Step 'Tachometer and RPM hold regression tests (no hardware)' {
    & $msbuild (Join-Path $root 'tests\RpmHoldTests\RpmHoldTests.csproj') /t:Rebuild /p:Configuration=Debug /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { return }
    & (Join-Path $root 'tests\RpmHoldTests\bin\Debug\pCUE.RpmHoldTests.exe')
}

Step 'Acquisition protection regression tests (fake hardware)' {
    & $msbuild (Join-Path $root 'tests\AcquisitionTests\AcquisitionTests.csproj') /t:Rebuild /p:Configuration=Debug /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { return }
    & (Join-Path $root 'tests\AcquisitionTests\bin\Debug\pCUE.AcquisitionTests.exe')
}

Step 'Remote protocol integration' {
    $testProject = Join-Path $root 'tests\RemoteProtocolTests\RemoteProtocolTests.csproj'
    & $msbuild $testProject /t:Rebuild /p:Configuration=Debug /m:1 /nr:false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { return }
    & (Join-Path $root 'tests\RemoteProtocolTests\bin\Debug\pCUE.RemoteProtocolTests.exe')
    if ($LASTEXITCODE -ne 0) { return }

    $cli = Join-Path $root 'tools\pcue-cli.ps1'
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($cli, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }

    # Bench script offline gate: parameter validation + plan without a bench PC.
    & (Join-Path $root 'tools\bench-validate.ps1') -Server 127.0.0.1 -DryRun
    if ($LASTEXITCODE -ne 0) { throw 'bench-validate dry run failed.' }

    # CLI end-to-end: the real CLI against a loopback stub, covering exit codes 0-3.
    # The script throws on failure and exits 0 on success (its last child pwsh exits 2 by
    # design, which would otherwise leak session-global $LASTEXITCODE into the gate below).
    & (Join-Path $root 'scripts\Test-CliE2E.ps1')
}

Step 'UI layout' {
    # WPF needs an STA thread, which pwsh 7 does not provide, so shell out to Windows PowerShell.
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-UiLayout.ps1')
}

Step 'Sync controls' {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-SyncControls.ps1')
}

if (-not $NoPack) {
    Step 'Release pack' {
        & pwsh -NoProfile -File (Join-Path $root 'build\pack-release.ps1')
    }
}

Write-Host "`nAll checks passed." -ForegroundColor Green
exit 0
