# Packaging must never replace another session's prepared or published version.
function Get-PlannedReleaseVersion {
    param([string]$AssemblyInfoPath, [string]$Configuration)
    $text = Get-Content -LiteralPath $AssemblyInfoPath -Raw
    $match = [regex]::Match($text, 'AssemblyFileVersion\("(\d+)\.(\d+)\.(\d+)"\)')
    if (-not $match.Success) { throw 'Expected the project three-part AssemblyFileVersion before packaging.' }
    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    $revision = [int]$match.Groups[3].Value
    if ($Configuration -eq 'Release') {
        $revision++
        if ($revision -gt 9) { $revision = 0; $minor++ }
        if ($minor -gt 9) { $minor = 0; $major++ }
    }
    return "$major.$minor.$revision"
}

function Assert-ReleaseArtifactsAvailable {
    param([string]$ArtifactsPath, [string]$Version)
    foreach ($name in @("pCUE_${Version}_portable.zip", "pCUE_${Version}_setup.exe")) {
        foreach ($suffix in @('', '.sha256')) {
            $path = Join-Path $ArtifactsPath ($name + $suffix)
            if (Test-Path -LiteralPath $path) {
                throw "Refusing to overwrite an existing release artifact: $path. Preserve it and use the next project version."
            }
        }
    }
}
