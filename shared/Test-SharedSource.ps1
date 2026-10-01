param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$ConformancePath,
    [string]$CanonicalRoot
)
$ErrorActionPreference = 'Stop'
if (!$ConformancePath) {
    $ConformancePath = @('tests\CoreBackendTests.cs', 'tests\CoolingControllers\CoreBackendTests.cs') | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) } | Select-Object -First 1
}
if (!$ConformancePath) { throw 'Pass the app conformance test path.' }
$manifest = Get-Content -LiteralPath (Join-Path $Root 'shared\source-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $path = if ($entry.name -like '*BackendTests.cs') { Join-Path $Root (Join-Path (Split-Path $ConformancePath -Parent) $entry.name) } else { Join-Path $Root ('shared\' + $entry.name) }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($hash -ne $entry.sha256) { throw "Shared source drift: $($entry.name). Update canonical code/tests and synchronize all three apps before releasing." }
    if ($CanonicalRoot) {
        $canonicalPath = if ($entry.name -like '*BackendTests.cs') { Join-Path $CanonicalRoot ('tests\' + $entry.name) } else { Join-Path $CanonicalRoot ('shared\' + $entry.name) }
        if ((Get-FileHash -LiteralPath $canonicalPath).Hash -ne $hash) { throw "Canonical parity gap: $($entry.name)." }
    }
}
Write-Output 'PASS: shared controller source and conformance SHA256 manifest.'
