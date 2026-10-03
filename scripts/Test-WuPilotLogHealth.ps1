#requires -Version 5.1
<#
.SYNOPSIS
Audits local Windows event retention and update evidence without changing settings.
.EXAMPLE
.\Test-WuPilotLogHealth.ps1 -TargetDays 30 -OutputDirectory C:\Support\LogHealth
.DESCRIPTION
Enumerates all event channels. Reads only their oldest and newest records.
Sizing estimates use allocated EVTX bytes divided by the observed event span,
with headroom. They are approximate, especially for sparse or newly created logs.
File evidence is inventoried, not decoded; modification times are not coverage.
#>
[CmdletBinding()]
param(
    [ValidateRange(1,3650)][int]$TargetDays = 30,
    [ValidateRange(1.0,10.0)][double]$Headroom = 1.5,
    [string[]]$LogName = @('*'),
    [string]$OutputDirectory = (Join-Path $PWD ('LogHealth-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
$now = [datetime]::UtcNow
$cutoff = $now.AddDays(-$TargetDays)
$required = @('System','Setup','Microsoft-Windows-WindowsUpdateClient/Operational')
$issues = New-Object 'System.Collections.Generic.List[object]'
$rows = New-Object 'System.Collections.Generic.List[object]'
$configs = @(Get-WinEvent -ListLog $LogName -Force -ErrorAction SilentlyContinue -ErrorVariable listErrors)
foreach ($err in $listErrors) { $issues.Add([pscustomobject]@{Source='Event enumeration';Detail=$err.ToString()}) }
foreach ($config in $configs) {
    $row = [ordered]@{
        LogName=$config.LogName;RequiredForUpdateReport=($required -contains $config.LogName)
        Enabled=$config.IsEnabled;Mode=[string]$config.LogMode;RecordCount=$config.RecordCount
        FileBytes=$config.FileSize;MaximumBytes=$config.MaximumSizeInBytes;Full=$config.IsLogFull
        OldestUtc=$null;NewestUtc=$null;ObservedSpanDays=$null;OldestAgeDays=$null
        OldestRecordId=$null;RolloverEvidence=$false;Status='Unknown';Detail=''
        SuggestedMaximumBytes=$null;SuggestedCommand=$null
    }
    try {
        if (-not $config.IsEnabled) { $row.Status='Disabled'; $row.Detail='Enable only if this channel is needed.' }
        elseif ($config.RecordCount -eq 0) { $row.Status='Empty'; $row.Detail='No retained evidence; activity may not have occurred.' }
        else {
            $old = Get-WinEvent -LogName $config.LogName -Oldest -MaxEvents 1 -ErrorAction Stop
            $new = Get-WinEvent -LogName $config.LogName -MaxEvents 1 -ErrorAction Stop
            try {
                if ($null -eq $old.TimeCreated -or $null -eq $new.TimeCreated) { throw 'Record timestamps unavailable.' }
                $oldUtc = $old.TimeCreated.ToUniversalTime()
                $newUtc = $new.TimeCreated.ToUniversalTime()
                $span = ($newUtc - $oldUtc).TotalDays
                $row.OldestUtc=$oldUtc.ToString('o'); $row.NewestUtc=$newUtc.ToString('o')
                $row.ObservedSpanDays=[math]::Round($span,4)
                $row.OldestAgeDays=[math]::Round(($now - $oldUtc).TotalDays,4)
                $row.OldestRecordId=$old.RecordId
                $row.RolloverEvidence=($old.RecordId -gt 1)
                if ($span -lt 0 -or $newUtc -gt $now.AddMinutes(5)) {
                    $row.Status='TimestampAnomaly'; $row.Detail='Check the system clock; no sizing estimate generated.'
                } elseif ($oldUtc -le $cutoff) {
                    $row.Status='RetentionTargetMet'; $row.Detail='Oldest retained event reaches the target; completeness is not proven.'
                } elseif ($row.RolloverEvidence -and $row.Mode -eq 'Circular') {
                    $row.Status='RolloverRisk'; $row.Detail='Earlier record IDs are absent and retained history is shorter than the target. Clearing or reconfiguration can also affect history.'
                } else {
                    $row.Status='ShortHistory'; $row.Detail='History is shorter than the target; sparse activity, a new log, or clearing may explain this.'
                }
                if ($row.Full -and $row.Mode -eq 'Retain') {
                    $row.Status='FullRetentionLog'; $row.Detail='Full retain-mode log can stop accepting events; arrange export and capacity review.'
                }
                if ($row.Status -eq 'RolloverRisk' -and $span -ge (1.0/24) -and $row.FileBytes -gt 0) {
                    $estimate = [math]::Ceiling(($row.FileBytes / $span * $TargetDays * $Headroom) / 65536) * 65536
                    $row.SuggestedMaximumBytes=[long][math]::Max($row.MaximumBytes,$estimate)
                    $escapedName = $config.LogName.Replace("'", "''")
                    $row.SuggestedCommand="wevtutil sl '$escapedName' /ms:$($row.SuggestedMaximumBytes)"
                    $row.Detail += ' Estimate assumes the observed write rate continues; check available disk and policy before applying.'
                }
            } finally { $old.Dispose(); $new.Dispose() }
        }
    } catch { $row.Status='Unreadable'; $row.Detail=$_.Exception.Message }
    $rows.Add([pscustomobject]$row)
    $config.Dispose()
}
foreach ($name in $required) {
    if ($LogName -contains '*' -and $name -notin @($rows.LogName)) {
        $issues.Add([pscustomobject]@{Source=$name;Detail='Required channel was not enumerated. Check availability and permissions.'})
    }
}

$files = New-Object 'System.Collections.Generic.List[object]'
$windows = [Environment]::GetFolderPath('Windows')
$drive = [IO.Path]::GetPathRoot($windows)
$sources = New-Object 'System.Collections.Generic.List[string]'
foreach ($base in @($windows,(Join-Path $drive 'Windows.old\Windows'))) {
    foreach ($relative in @('Panther','Logs\MoSetup','Logs\SetupDiag','Logs\CBS','Logs\DISM','Logs\WindowsUpdate','setupact.log','setuperr.log','INF\setupapi.dev.log','INF\setupapi.app.log')) {
        $sources.Add((Join-Path $base $relative))
    }
}
foreach ($base in @($drive,(Join-Path $drive 'Windows.old'))) {
    foreach ($relative in @('$WINDOWS.~BT\Sources\Panther','$WINDOWS.~BT\Sources\Rollback')) { $sources.Add((Join-Path $base $relative)) }
}
$sources.Add((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WuPilot\operation-metrics.json'))
function Read-EvidencePath([string]$Path) {
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $issues.Add([pscustomobject]@{Source=$Path;Detail='Link skipped.'}); return
        }
        if ($item.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)) { Read-EvidencePath $child.FullName }
        } else {
            $files.Add([pscustomobject]@{Path=$item.FullName;Bytes=$item.Length;LastWriteUtc=$item.LastWriteTimeUtc.ToString('o');Status=$(if ($item.Length -eq 0) {'Empty'} else {'Present'});Coverage='Unknown: file contents and archives not decoded'})
        }
    } catch { $issues.Add([pscustomobject]@{Source=$Path;Detail=$_.Exception.Message}) }
}
foreach ($source in $sources) { Read-EvidencePath $source }
$disks = @()
try { $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,Size,FreeSpace) }
catch { $issues.Add([pscustomobject]@{Source='Disk capacity';Detail=$_.Exception.Message}) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$report = [pscustomobject]@{
    Computer=$env:COMPUTERNAME;CheckedUtc=$now.ToString('o');TargetDays=$TargetDays;Headroom=$Headroom
    Limitations='Read-only snapshot. No audit proves all needed events exist. EVTX allocation is not exact payload size. Sizing is approximate; increases cannot recover overwritten events. File dates do not prove retention. Disabled analytic/debug channels are commonly intentional. Missing optional upgrade paths are normal before an upgrade.'
    EventLogs=@($rows.ToArray());EvidenceFiles=@($files.ToArray());Issues=@($issues.ToArray());Disks=$disks
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'log-health.json') -Encoding UTF8
$rows.ToArray() | Export-Csv -LiteralPath (Join-Path $output 'event-log-health.csv') -NoTypeInformation -Encoding UTF8
$files.ToArray() | Export-Csv -LiteralPath (Join-Path $output 'evidence-files.csv') -NoTypeInformation -Encoding UTF8
$issues.ToArray() | Export-Csv -LiteralPath (Join-Path $output 'issues.csv') -NoTypeInformation -Encoding UTF8
$rows | Where-Object { $_.RequiredForUpdateReport -or $_.Status -in @('RolloverRisk','FullRetentionLog','Unreadable','TimestampAnomaly') } |
    Format-Table LogName,Status,OldestAgeDays,MaximumBytes,SuggestedMaximumBytes -AutoSize | Out-Host
Write-Host "Audit saved to $output. Review log-health.json for limitations, disk space, and unreadable sources. No log settings changed."
