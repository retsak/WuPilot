[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.3.1',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipAppBuild,

    [string] $InnoCompilerPath,

    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'WuPilot installers must be built on Windows.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $repoRoot 'installer/WuPilot.iss'
$innoArchitecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$publishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Join-Path $repoRoot "artifacts/WuPilot-$runtime"
} else {
    [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($PublishDirectory)) {
        $PublishDirectory
    } else {
        Join-Path $repoRoot $PublishDirectory
    }))
}
$installerDirectory = Join-Path $repoRoot 'artifacts/installer'
$installerPath = Join-Path $installerDirectory "WuPilot-$Version-win-$innoArchitecture-setup.exe"

if (-not $SkipAppBuild) {
    & (Join-Path $PSScriptRoot 'Build-WuPilot.ps1') `
        -Platform $Platform `
        -Configuration $Configuration `
        -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw 'WuPilot application build failed.'
    }
}

$requiredPublishFiles = @('WuPilot.exe', 'WuPilot.pri', 'App.xbf', 'MainWindow.xbf')
$missingPublishFiles = @(
    $requiredPublishFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_) -PathType Leaf)
    }
)
if ($missingPublishFiles.Count -gt 0) {
    throw "Publish output is incomplete. Missing: $($missingPublishFiles -join ', ')."
}

$compilerCandidates = @(
    $InnoCompilerPath
    $env:INNO_SETUP_COMPILER
    (Join-Path $repoRoot '.tools/inno/ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$compiler = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if ($null -eq $compiler) {
    throw 'Inno Setup Compiler was not found. Install Inno Setup 7 or pass -InnoCompilerPath.'
}

New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
& $compiler "/DAppVersion=$Version" "/DAppArch=$innoArchitecture" "/DAppSourceDir=$publishDirectory" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup compilation failed.'
}

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer was not created at $installerPath."
}

$hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
$checksumPath = "$installerPath.sha256"
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($installerPath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "WuPilot installer: $installerPath" -ForegroundColor Green
Write-Host "SHA-256: $($hash.Hash)" -ForegroundColor Green
