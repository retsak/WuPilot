[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.2.0'
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'The WinUI XAML compiler is Windows-only. Run this script on Windows.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$output = Join-Path $repoRoot "artifacts/WuPilot-$runtime"

Push-Location $repoRoot
try {
    dotnet restore WuPilot.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet test tests/WuPilot.Core.Tests/WuPilot.Core.Tests.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

    dotnet test tests/WuPilot.Infrastructure.Tests/WuPilot.Infrastructure.Tests.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure tests failed.' }

    dotnet build tests/WuPilot.App.CodeChecks/WuPilot.App.CodeChecks.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'WinUI code-behind compilation gate failed.' }

    dotnet publish src/WuPilot.App/WuPilot.App.csproj --configuration $Configuration --no-restore -p:Platform=$Platform -p:Version=$Version -r $runtime --self-contained true --output $output
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }

    $requiredPublishFiles = @(
        'WuPilot.exe'
        'WuPilot.pri'
        'App.xbf'
        'MainWindow.xbf'
    )
    $missingPublishFiles = @(
        $requiredPublishFiles | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $output $_) -PathType Leaf)
        }
    )
    if ($missingPublishFiles.Count -gt 0) {
        throw "WinUI publish output is incomplete. Missing: $($missingPublishFiles -join ', ')."
    }

    Write-Host "WuPilot published to $output" -ForegroundColor Green
}
finally {
    Pop-Location
}
