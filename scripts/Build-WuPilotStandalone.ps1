[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Platform = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.4.5'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = "win-$Platform"
$publish = Join-Path $repoRoot "artifacts/collector-$runtime"
$release = Join-Path $repoRoot 'artifacts/standalone'
$dotnet = if (Test-Path -LiteralPath (Join-Path $repoRoot '.dotnet/dotnet.exe')) { Join-Path $repoRoot '.dotnet/dotnet.exe' } else { 'dotnet' }
& $dotnet publish (Join-Path $repoRoot 'src/WuPilot.Collector/WuPilot.Collector.csproj') -c Release -r $runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Standalone collector publish failed.' }
New-Item -ItemType Directory -Path $release -Force | Out-Null
$output = Join-Path $release "WuPilot-Collector-$Version-$runtime.exe"
Copy-Item -LiteralPath (Join-Path $publish 'WuPilot-Collector.exe') -Destination $output -Force
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($output))" | Set-Content -LiteralPath "$output.sha256" -Encoding ascii
Write-Host "Standalone collector: $output"
