param([Parameter(Mandatory=$true)][string]$ScratchPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\build\ReleaseArtifactGuard.ps1')
New-Item -ItemType Directory -Force -Path $ScratchPath | Out-Null
$assembly = Join-Path $ScratchPath 'AssemblyInfo.cs'
foreach ($case in @(@('1.7.0','1.7.1'), @('1.7.9','1.8.0'), @('1.9.9','2.0.0'))) {
    [System.IO.File]::WriteAllText($assembly, '[assembly: AssemblyFileVersion("' + $case[0] + '")]')
    if ((Get-PlannedReleaseVersion $assembly 'Release') -ne $case[1]) { throw 'Release carry differs from the project task.' }
    if ((Get-PlannedReleaseVersion $assembly 'Debug') -ne $case[0]) { throw 'Debug prediction changed the version.' }
}
Assert-ReleaseArtifactsAvailable $ScratchPath '1.7.1'
foreach ($name in @('pCUE_1.7.1_portable.zip', 'pCUE_1.7.1_setup.exe', 'pCUE_1.7.1_portable.zip.sha256', 'pCUE_1.7.1_setup.exe.sha256')) {
    $path = Join-Path $ScratchPath $name
    [System.IO.File]::WriteAllText($path, 'retained owner bytes')
    $rejected = $false
    try { Assert-ReleaseArtifactsAvailable $ScratchPath '1.7.1' } catch { $rejected = $_.Exception.Message -like 'Refusing to overwrite*' }
    if (-not $rejected -or [System.IO.File]::ReadAllText($path) -ne 'retained owner bytes') { throw "Collision guard failed for $name" }
    [System.IO.File]::Delete($path)
}
Write-Host 'PASS: version carry and all four artifact/sidecar collision guards preserve existing bytes.'
