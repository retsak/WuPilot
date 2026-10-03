#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $PSScriptRoot '..\artifacts\standalone-report-tests' }
$generator=Join-Path $PSScriptRoot '..\scripts\New-WuPilotUpdateReport.ps1'
$testRoot=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
$inputRoot=Join-Path $testRoot 'input'
[void][IO.Directory]::CreateDirectory($inputRoot)
$id='11111111-1111-1111-1111-111111111111'
$lines=@(
    "2026-10-02 10:00:00 Initializing UpdateId = [{$id}]",
    '2026-10-02 10:00:00 GenerateDownloadRequest: Enter',
    '2026-10-02 10:00:20 ReportEventDownloadRequestEnd: DownloadComplete = [TRUE]',
    '2026-10-02 10:00:25 Install: Enter',
    '2026-10-02 10:00:26 Installing feature: Feature: CumulativeUpdate_KB1234567',
    '2026-10-02 10:00:30 error <script>alert(1)</script>',
    '2026-10-02 10:01:00 Reboot required: [TRUE]'
)
[IO.File]::WriteAllLines((Join-Path $inputRoot 'UpdateAgent.Old.log'),$lines)
$staging=Join-Path $testRoot 'staging'; $delivery=Join-Path $testRoot 'delivery'
$report = & $generator -Collect -InputPath $inputRoot -Destination $delivery -StagingDirectory $staging -TimeZoneId 'Central Standard Time' -Update KB1234567
$bundle=Split-Path $report
$rows=@(Import-Csv -LiteralPath (Join-Path $bundle 'update-timings.csv'))
if ($rows.Count -ne 1 -or $rows[0].DownloadSeconds -ne '20' -or $rows[0].InstallSeconds -ne '35' -or $rows[0].RebootSeconds -ne '') { throw 'Incorrect or invented phase duration.' }
$manifest=@(Get-Content -LiteralPath (Join-Path $bundle 'manifest.json') -Raw | ConvertFrom-Json)
if ($manifest.Count -ne 1 -or $manifest[0].status -ne 'Collected') { throw 'Incorrect collection manifest.' }
$snapshot=Join-Path $bundle $manifest[0].file
if ((Get-FileHash -LiteralPath $snapshot).Hash -ne $manifest[0].sha256 -or (Get-FileHash -LiteralPath $snapshot).Hash -ne (Get-FileHash -LiteralPath (Join-Path $inputRoot 'UpdateAgent.Old.log')).Hash) { throw 'Snapshot/hash mismatch.' }
$html=Get-Content -LiteralPath $report -Raw
if ($html.Contains('<script>') -or (-not $html.Contains('&lt;script&gt;') -and -not $html.Contains('\u003cscript\u003e'))) { throw 'Supporting evidence is not HTML escaped.' }
$localZip=$bundle+'.zip'; $deliveredZip=Join-Path $delivery ([IO.Path]::GetFileName($localZip))
if ((Get-FileHash -LiteralPath $localZip).Hash -ne (Get-FileHash -LiteralPath $deliveredZip).Hash) { throw 'ZIP delivery checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($deliveredZip)
try { if ('report.html' -notin $archive.Entries.FullName -or 'manifest.json' -notin $archive.Entries.FullName -or $manifest[0].file -notin $archive.Entries.FullName) { throw 'ZIP lacks report or evidence.' } } finally { $archive.Dispose() }
$reReport=& $generator -InputPath $bundle -Destination (Join-Path $testRoot 'regenerated') -Update $id
if (@(Import-Csv -LiteralPath (Join-Path (Split-Path $reReport) 'update-timings.csv')).Count -ne 1) { throw 'Collected bundle cannot be regenerated.' }
$empty=& $generator -InputPath $bundle -Destination (Join-Path $testRoot 'empty') -Update KB9999999
if (@(Import-Csv -LiteralPath (Join-Path (Split-Path $empty) 'update-timings.csv')).Count) { throw 'Unrelated KB matches.' }
# Exercise delivery failure: retain the local complete ZIP when the destination is a file.
$blocked=Join-Path $testRoot 'blocked'; [IO.File]::WriteAllText($blocked,'occupied')
$failed=& $generator -Collect -InputPath $inputRoot -Destination $blocked -StagingDirectory $staging -WarningVariable deliveryWarnings
if (-not (Test-Path -LiteralPath ((Split-Path $failed)+'.zip')) -or -not ($deliveryWarnings -match 'ZIP delivery failed')) { throw 'Delivery failure did not retain local ZIP/report a warning.' }
# Oversized files and file-count limits are recorded explicitly.
[IO.File]::WriteAllBytes((Join-Path $inputRoot 'large.log'),(New-Object byte[] (1MB+1)))
$limited=& $generator -Collect -InputPath $inputRoot -Destination $delivery -StagingDirectory $staging -MaxFileMB 1
$limitedManifest=Get-Content -LiteralPath (Join-Path (Split-Path $limited) 'manifest.json') -Raw | ConvertFrom-Json
if (-not ($limitedManifest | Where-Object { $_.status -eq 'Limit' -and $_.detail -eq 'File exceeds size limit.' })) { throw 'Size limit missing from manifest.' }
$countLimited=& $generator -Collect -InputPath $inputRoot -Destination $delivery -StagingDirectory $staging -MaxFiles 1
$countManifest=Get-Content -LiteralPath (Join-Path (Split-Path $countLimited) 'manifest.json') -Raw | ConvertFrom-Json
if (-not ($countManifest | Where-Object { $_.status -eq 'Limit' -and $_.detail -like 'File count limit*' })) { throw 'Count limit missing from manifest.' }
Write-Output 'Passed standalone collection, report regeneration, hashes, ZIP contents, filtering, HTML escaping, missing completion, delivery failure, and limits.'
