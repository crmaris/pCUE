<#
.SYNOPSIS
  Build the pCUE release artifacts into <repo>\artifacts\ (git-ignored).

.DESCRIPTION
  Produces, for the version stamped into the freshly built pCUE.exe:
    * a clean staged Release build   (artifacts\stage\pCUE)
    * a portable .zip                (+ .sha256)
    * a single-file Inno Setup installer .exe (+ .sha256)   [unless -SkipInstaller]

  The stage is produced by a REBUILD into an empty directory, deliberately: the project's
  bin\Release accumulates files from older builds (Core Temp, OpenHardwareMonitorLib, vshost,
  ...) that must never ship. Never package bin\Release directly.

  pCUE targets .NET Framework 4.8, which ships with Windows 10 1903+ and Windows 11, so no
  runtime is bundled.

  Builds are UNSIGNED unless you pass a certificate (see build\sign.ps1).

.PARAMETER SkipInstaller  Build only the portable .zip (skip Inno Setup).

.EXAMPLE
  pwsh build\pack-release.ps1
  pwsh build\pack-release.ps1 -SkipInstaller
  pwsh build\pack-release.ps1 -Thumbprint <cert-thumbprint>
#>
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [switch]$SkipInstaller,

  # Code signing (optional). Supply ONE of these to produce signed artifacts; omit both and the
  # build stays unsigned. A hardware/EV token prompts for its own PIN; a .pfx without
  # -PfxPassword makes signtool prompt, so no secret need appear on the command line.
  [string]$Thumbprint,
  [string]$PfxPath,
  [string]$PfxPassword
)
$ErrorActionPreference = 'Stop'

$signCert = @{}
if ($Thumbprint) { $signCert['Thumbprint'] = $Thumbprint }
elseif ($PfxPath) {
  $signCert['PfxPath'] = $PfxPath
  if ($PfxPassword) { $signCert['PfxPassword'] = $PfxPassword }
}
$doSign = $signCert.Count -gt 0

function Invoke-Sign {
  param([string[]]$Paths, [string]$Why)
  if (-not $doSign) { return }
  Write-Host "  signing ($Why)..."
  & (Join-Path $PSScriptRoot 'sign.ps1') @signCert -Files $Paths -Description 'pCUE'
  if ($LASTEXITCODE -ne 0) { throw "signing failed ($Why)" }
}

function Get-MSBuild {
  $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  if (Test-Path $vswhere) {
    $p = & $vswhere -latest -requires Microsoft.Component.MSBuild `
                    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($p -and (Test-Path $p)) { return $p }
  }
  foreach ($ed in 'Enterprise', 'Professional', 'Community', 'BuildTools') {
    $p = "${env:ProgramFiles}\Microsoft Visual Studio\2022\$ed\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $p) { return $p }
  }
  throw "MSBuild.exe not found. Install Visual Studio 2022 or the Build Tools."
}

function Get-InnoCompiler {
  $paths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  )
  foreach ($p in $paths) { if (Test-Path $p) { return $p } }
  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  return $null
}

$root      = Split-Path $PSScriptRoot -Parent
$proj      = Join-Path $root 'pCUE\pCUE.csproj'
$artifacts = Join-Path $root 'artifacts'
$stage     = Join-Path $artifacts 'stage\pCUE'

Write-Host "== pCUE release pack ==  config=$Configuration"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 1) clean staged build ------------------------------------------------------
# Rebuild into an EMPTY OutputPath so only the files this project currently needs are staged.
$msbuild = Get-MSBuild
Write-Host "  msbuild: $msbuild"
& $msbuild "$proj" /t:Rebuild /p:Configuration=$Configuration "/p:OutputPath=$stage" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

$exe = Join-Path $stage 'pCUE.exe'
if (-not (Test-Path $exe)) { throw "staged build produced no pCUE.exe" }

# Drop development-only files; they are not needed to run and only bloat the package.
Get-ChildItem $stage -Recurse -Include *.pdb, *.xml -File | Remove-Item -Force

# The Release build bumps AssemblyFileVersion, so read the version AFTER building: the freshly
# built exe is the single source of truth for what we are shipping.
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
Write-Host "  version:  $version"
Write-Host ("  staged:   {0} files, {1:N1} MB" -f `
  (Get-ChildItem $stage -Recurse -File).Count,
  ((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# Sign the STAGED app exe before packaging. Signing the finished setup.exe does not sign the
# payload inside it, so this is what makes the INSTALLED app - and the portable copy - signed.
Invoke-Sign -Paths @($exe) -Why 'staged app exe'

# 2) portable zip (+ sha256) -------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $artifacts "pCUE_${version}_portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
(Get-FileHash $zip -Algorithm SHA256).Hash | Out-File "$zip.sha256" -Encoding ascii -NoNewline
Write-Host ("  portable  -> {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

# 3) Inno Setup installer (+ sha256) ----------------------------------------
if (-not $SkipInstaller) {
  $iscc = Get-InnoCompiler
  if (-not $iscc) {
    throw "ISCC.exe (Inno Setup) not found. Install: winget install --id JRSoftware.InnoSetup"
  }
  $iss = Join-Path $root 'build\installer\pCUE.iss'
  $isccLog = & $iscc "/DMyAppVersion=$version" "/DPublishDir=$stage" "$iss" 2>&1
  if ($LASTEXITCODE -ne 0) {
    $hint = ''
    # `x64compatible` needs Inno Setup 6.3+ (2024); older 6.x rejects the directive outright.
    if ("$isccLog" -match 'x64compatible') {
      $hint = "`nThis build of Inno Setup does not understand 'x64compatible' (needs 6.3+). " +
              "Upgrade: winget upgrade --id JRSoftware.InnoSetup"
    }
    throw ("ISCC failed (exit $LASTEXITCODE).`n" + ($isccLog | Select-Object -Last 15 | Out-String) + $hint)
  }
  $setup = Join-Path $artifacts "pCUE_${version}_setup.exe"
  if (Test-Path $setup) {
    # Sign BEFORE hashing: signing rewrites the file, so a hash taken first would not match.
    Invoke-Sign -Paths @($setup) -Why 'installer'
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    $hash | Out-File "$setup.sha256" -Encoding ascii -NoNewline
    Write-Host ("  installer -> {0}  ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
    Write-Host ""
    Write-Host "Publish checklist (in-app updater reads this):" -ForegroundColor Cyan
    Write-Host "  1. Attach the setup .exe to a GitHub Release on crmaris/pCUE."
    Write-Host "  2. In crmaris/powenetics-updates -> components.json, set apps.pcue to:"
    Write-Host "       version : $version"
    Write-Host "       sha256  : $hash"
    Write-Host "       url     : the release asset download URL"
  }
}

if ($doSign) {
  Write-Host "Done. Artifacts are SIGNED and land in $artifacts (git-ignored)."
} else {
  Write-Host "Done. Artifacts are UNSIGNED and land in $artifacts (git-ignored)."
  Write-Host "  To sign, re-run with -Thumbprint <cert> (or -PfxPath <file>)." -ForegroundColor Yellow
}
