[CmdletBinding()]
param([string]$OutputDirectory, [string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $root '.codex-tmp/linux/publish' }
$match = [regex]::Match((Get-Content (Join-Path $root 'pCUE/Properties/AssemblyInfo.cs') -Raw), 'AssemblyFileVersion\("([^"]+)"\)')
if (-not $match.Success) { throw 'The release version is missing.' }
$version = $match.Groups[1].Value
foreach ($name in @("pCUE_${version}_linux-x64.tar.gz", "pcue-linux_${version}_amd64.deb")) {
    foreach ($suffix in @('', '.sha256')) { if (Test-Path (Join-Path $OutputDirectory ($name + $suffix))) { throw "Existing package is protected: $name$suffix" } }
}
if (Test-Path $PublishDirectory) { throw 'Use an empty, task-owned publish directory.' }
if ($IsWindows) {
    $active = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='MSBuild.exe'" | Where-Object { $_.CommandLine -match ' (build|test|publish) |MSBuild.dll|MSBuild.exe' })
    if ($active.Count) { throw 'Another .NET gate is active. Defer Linux packaging.' }
}
$project = Join-Path $root 'linux/pCUE.Linux.csproj'
if ($IsWindows) {
    & 'C:\Users\ARIS\.codex\scripts\safe-dotnet.ps1' publish $project -c Release -r linux-x64 --self-contained true -p:RestoreLockedMode=true -o $PublishDirectory
} else {
    & dotnet publish $project -c Release -r linux-x64 --self-contained true -p:RestoreLockedMode=true -o $PublishDirectory --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false
}
if ($LASTEXITCODE -ne 0) { throw 'Linux publication failed.' }
$epoch = (& git -C $root log -1 --format=%ct).Trim()
& python (Join-Path $PSScriptRoot 'package-linux.py') --publish $PublishDirectory --output $OutputDirectory --version $version --epoch $epoch
if ($LASTEXITCODE -ne 0) { throw 'Linux package assembly failed.' }
