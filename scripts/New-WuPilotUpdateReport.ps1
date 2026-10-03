#requires -Version 5.1
<#
.SYNOPSIS
Collects Windows update evidence and generates performance reports without WuPilot.
.DESCRIPTION
Reads an extracted WuPilot bundle or a directory of UpdateAgent logs and EVTX
snapshots, or collects this device's logs when InputPath is omitted. Run elevated
for protected live logs. No updates are installed and no restart is initiated.
Writes report.html, phase-summary.json and update-timings.csv. Source timestamps
use the supplied time zone, the saved bundle zone, or the local zone.
.EXAMPLE
.\New-WuPilotUpdateReport.ps1 -InputPath C:\Support\Bundle -Destination C:\Support\Report -Update KB5124010
.EXAMPLE
.\New-WuPilotUpdateReport.ps1 -Destination C:\Support -OpenReport
#>
[CmdletBinding()]
param(
    [string]$InputPath,
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$TimeZoneId,
    [string]$Update,
    [switch]$OpenReport,
    [switch]$Collect,
    [ValidateRange(1,10000)][int]$MaxFiles = 1000,
    [ValidateRange(1,1048576)][long]$MaxFileMB = 512,
    [string]$StagingDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WuPilot\LogBundles')
)
$ErrorActionPreference = 'Stop'
$collecting = $Collect -or [string]::IsNullOrWhiteSpace($InputPath)
$deliveryFolder = [IO.Path]::GetFullPath($Destination)
if ($TimeZoneId) { [void][TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId) }
function Write-CollectionJson($Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), (New-Object Text.UTF8Encoding($true)))
}
if ($collecting) {
    $live = [string]::IsNullOrWhiteSpace($InputPath)
    $bundleName = '{0}-{1}-{2}' -f $env:COMPUTERNAME, [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N')
    $work = Join-Path ([IO.Path]::GetFullPath($StagingDirectory)) $bundleName
    $raw = Join-Path $work 'logs'
    $collection = New-Object 'System.Collections.Generic.List[object]'
    function Add-CollectionEntry($Source, $File, $Status, $Bytes, $Seconds, $Hash, $Detail) {
        $collection.Add([pscustomobject]@{source=$Source;file=$File;status=$Status;bytes=$Bytes;seconds=$Seconds;sha256=$Hash;detail=$Detail})
    }
    $sources = New-Object 'System.Collections.Generic.List[string]'
    if ($live) {
        $windows = [Environment]::GetFolderPath('Windows')
        $drive = [IO.Path]::GetPathRoot($windows)
        foreach ($parent in @($drive,(Join-Path $drive 'Windows.old'))) {
            foreach ($relative in @('$WINDOWS.~BT\Sources\Panther','$WINDOWS.~BT\Sources\Rollback')) { $sources.Add((Join-Path $parent $relative)) }
        }
        foreach ($system in @($windows,(Join-Path $drive 'Windows.old\Windows'))) {
            foreach ($relative in @('Panther','Logs\MoSetup','Logs\SetupDiag','Logs\CBS','Logs\DISM','Logs\WindowsUpdate','setupact.log','setuperr.log','INF\setupapi.dev.log','INF\setupapi.app.log')) { $sources.Add((Join-Path $system $relative)) }
        }
        foreach ($channel in @('System','Setup','Microsoft-Windows-WindowsUpdateClient%4Operational')) { $sources.Add((Join-Path $drive ('Windows.old\Windows\System32\winevt\Logs\' + $channel + '.evtx'))) }
        $sources.Add((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WuPilot\operation-metrics.json'))
    } else {
        $inputRoot = (Resolve-Path -LiteralPath $InputPath).ProviderPath
        if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) { throw 'InputPath must be a directory.' }
        if ($work.StartsWith($inputRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'StagingDirectory must be outside InputPath when collecting existing logs.' }
        $sources.Add($inputRoot)
    }
    [void][IO.Directory]::CreateDirectory($raw)
    Write-Host "Collecting evidence into $work"
    $inputs = New-Object 'System.Collections.Generic.List[string]'
    $visited = @{}
    foreach ($source in $sources) {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($source)
        while ($pending.Count) {
            $path = $pending.Pop()
            try {
                $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Add-CollectionEntry $path $null 'Limit' 0 0 $null 'Directory/file link skipped.'; continue }
                if ($item.PSIsContainer) {
                    if ($inputs.Count -ge $MaxFiles) { Add-CollectionEntry $path $null 'Limit' 0 0 $null 'File count limit reached; collection is partial.'; continue }
                    foreach ($child in Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) { $pending.Push($child.FullName) }
                } elseif ($item.Extension -in '.log','.xml','.json','.etl','.evtx','.txt','.cab','.dmp','.bak' -or $item.Name -match '^(?:[0-9]+-)*UpdateAgent(?:[._-][a-z0-9_-]+)*\.(?:log|bak)(?:\.[0-9]+)?$') {
                    if ($visited.ContainsKey($item.FullName)) { continue }
                    $visited[$item.FullName]=$true
                    if ($inputs.Count -ge $MaxFiles) { Add-CollectionEntry $path $null 'Limit' $item.Length 0 $null 'File count limit reached; collection is partial.' }
                    else { $inputs.Add($item.FullName) }
                }
            } catch {
                $status = if ($_.CategoryInfo.Category -eq 'ObjectNotFound') {'Missing'} else {'Unavailable'}
                Add-CollectionEntry $path $null $status 0 0 $null $_.Exception.Message
            }
        }
    }
    $index=0
    foreach ($path in $inputs) {
        $name = '{0:D4}-{1}' -f $index,[IO.Path]::GetFileName($path); $index++
        $target = Join-Path $raw $name
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            Write-Progress -Activity 'Collecting update logs' -Status ([IO.Path]::GetFileName($path)) -PercentComplete (100*$index/[Math]::Max(1,$inputs.Count))
            $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $length = $stream.Length
                if ($length -gt $MaxFileMB*1MB) { Add-CollectionEntry $path $null 'Limit' $length $timer.Elapsed.TotalSeconds $null 'File exceeds size limit.'; continue }
                $copy = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try {
                    $buffer = New-Object byte[] 65536; $remaining=$length
                    while ($remaining -gt 0) {
                        $read = $stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining))
                        if ($read -eq 0) { throw 'Source was truncated during collection.' }
                        $copy.Write($buffer,0,$read); $remaining-=$read
                    }
                } finally { $copy.Dispose() }
            } finally { $stream.Dispose() }
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            Add-CollectionEntry $path ('logs/'+$name) 'Collected' $length $timer.Elapsed.TotalSeconds $hash 'Snapshot bounded to initial file length; not transactional.'
        } catch {
            if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
            Add-CollectionEntry $path $null 'Unavailable' 0 $timer.Elapsed.TotalSeconds $null $_.Exception.Message
        }
    }
    Write-Progress -Activity 'Collecting update logs' -Completed
    if ($live) {
        foreach ($channel in @('System','Setup','Microsoft-Windows-WindowsUpdateClient/Operational')) {
            $name=$channel.Replace('/','-')+'.evtx'; $target=Join-Path $raw $name
            $timer=[Diagnostics.Stopwatch]::StartNew(); $process=$null; $processStarted=$false
            try {
                Write-Host "Exporting $channel"
                $start=New-Object Diagnostics.ProcessStartInfo
                $start.FileName=Join-Path $env:SystemRoot 'System32\wevtutil.exe'
                $start.Arguments='epl "'+$channel+'" "'+$target+'"'
                $start.UseShellExecute=$false; $start.CreateNoWindow=$true
                $start.RedirectStandardError=$true; $start.RedirectStandardOutput=$true
                $process=New-Object Diagnostics.Process; $process.StartInfo=$start
                [void]$process.Start()
                $processStarted=$true
                $stderr=$process.StandardError.ReadToEndAsync(); $stdout=$process.StandardOutput.ReadToEndAsync()
                if (-not $process.WaitForExit(45000)) { $process.Kill(); $process.WaitForExit(); throw 'Event export timed out after 45 seconds.' }
                if ($process.ExitCode -ne 0) { throw $stderr.Result }
                Add-CollectionEntry $channel ('logs/'+$name) 'Collected' ([IO.FileInfo]$target).Length $timer.Elapsed.TotalSeconds (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash 'Full retained event history.'
            } catch {
                if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
                Add-CollectionEntry $channel $null 'Unavailable' 0 $timer.Elapsed.TotalSeconds $null $_.Exception.Message
            } finally {
                if ($process) {
                    if ($processStarted -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
        }
    }
    Write-CollectionJson (Join-Path $work 'manifest.json') @($collection.ToArray())
    # Reuse only saved metadata collected from the same source device.
    foreach ($metadata in @('analysis.json','operation-metrics.json','phase-summary.json')) {
        $entry = $collection | Where-Object { $_.status -eq 'Collected' -and [IO.Path]::GetFileName($_.source) -eq $metadata } | Select-Object -First 1
        if ($entry) { [IO.File]::Copy((Join-Path $work $entry.file),(Join-Path $work $metadata)) }
    }
    Write-Host 'Analyzing supporting log snapshots'
    $supporting = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $collection | Where-Object { $_.status -eq 'Collected' -and $_.file -match '\.log$' }) {
        $findings = New-Object 'System.Collections.Generic.List[object]'
        $activity = New-Object 'System.Collections.Generic.List[object]'
        $count=0; $first=$null; $last=$null; $entries=0; $phase='Unclassified setup activity'
        foreach ($rawLine in [IO.File]::ReadLines((Join-Path $work $entry.file))) {
            $count++
            $line = if ($rawLine.Length -gt 8192) { $rawLine.Substring(0,8192) } else { $rawLine }
            if ($findings.Count -lt 1000 -and $line -match '\b(?:error|failed|failure|rollback)\b|\b0x[89a-f][0-9a-f]{7}\b') {
                $findings.Add([pscustomobject]@{source=$entry.source;line=$count;severity='Review';text=$line})
            }
            $time=[datetime]::MinValue
            if ($line.Length -lt 19 -or -not [datetime]::TryParseExact($line.Substring(0,19).Replace('T',' '),'yyyy-MM-dd HH:mm:ss',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$time)) { continue }
            $nextPhase=$phase
            $marker=$line -match '\b(?:phase\s*[:=]\s*|entering\s+(?:the\s+)?)(Downlevel|SafeOS|Safe OS|FirstBoot|First Boot|SecondBoot|Second Boot|OOBE)\b'
            if ($marker) { $nextPhase=$Matches[1].ToUpperInvariant() }
            $newWindow=$last -and ($time -lt $last -or ($time-$last).TotalHours -gt 6)
            if ($newWindow -and -not $marker) { $nextPhase='Unclassified setup activity' }
            if ($last -and ($newWindow -or $nextPhase -ne $phase)) {
                $activity.Add([pscustomobject]@{source=$entry.source;phase=$phase;first=$first.ToString('s');last=$last.ToString('s');entries=$entries;observedSpan=($last-$first).ToString()})
                $first=$null; $last=$null; $entries=0
            }
            $phase=$nextPhase
            if (-not $first) { $first=$time }; $last=$time; $entries++
        }
        if ($last) { $activity.Add([pscustomobject]@{source=$entry.source;phase=$phase;first=$first.ToString('s');last=$last.ToString('s');entries=$entries;observedSpan=($last-$first).ToString()}) }
        $supporting.Add([pscustomobject]@{activity=@($activity.ToArray());findings=@($findings.ToArray());linesRead=$count;truncated=$false})
    }
    if ($supporting.Count) { Write-CollectionJson (Join-Path $work 'analysis.json') @($supporting.ToArray()) }
    $InputPath=$work
    $Destination=$work
}
$root = (Resolve-Path -LiteralPath $InputPath).ProviderPath
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'InputPath must be an extracted bundle or log directory.' }
$output = [IO.Path]::GetFullPath($Destination)
if (-not $collecting -and $output.TrimEnd('\','/') -eq $root.TrimEnd('\','/')) { throw 'Destination must differ from InputPath to preserve saved reports.' }
$warnings = New-Object 'System.Collections.Generic.List[string]'
function Read-Json([string]$Name) {
    $path = Join-Path $root $Name
    if (Test-Path -LiteralPath $path) {
        $parsed = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($item in $parsed) { Write-Output $item }
    }
}
function Resolve-Source([string]$Relative) {
    $prefix = $root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest source escapes the input folder.' }
    $path
}
function Matches-Update($Id, $Title) {
    if ([string]::IsNullOrWhiteSpace($Update)) { return $true }
    $guid = [guid]::Empty
    if ([guid]::TryParse($Update.Trim(), [ref]$guid)) { return ([string]$Id).Trim('{}') -eq $guid.ToString() }
    $kb = $Update.Trim() -replace '^(?i)KB',''
    if ($kb -match '^\d+$') { return [string]$Title -match ('\bKB' + $kb + '\b') }
    return $false
}
function Boundary($Time, $Label, $Source) { [pscustomobject]@{time=$Time.ToString('o'); label=$Label; source=$Source} }
function Duration($Start, $End) {
    if ($null -ne $Start -and $null -ne $End) {
        $span = [datetimeoffset]::Parse($End.time) - [datetimeoffset]::Parse($Start.time)
        if ($span.Ticks -ge 0) { return $span.ToString() }
    }
    return $null
}
function Phase($Name, $Start, $End, $Confidence, $Explanation) {
    [pscustomobject]@{name=$Name; start=$Start; end=$End; confidence=$Confidence; explanation=$Explanation; duration=(Duration $Start $End)}
}
$saved = Read-Json 'phase-summary.json'
if (-not $TimeZoneId -and $saved) { $TimeZoneId = $saved.timeZone }
$zone = if ($TimeZoneId) { [TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId) } else { [TimeZoneInfo]::Local }
$manifest = @(Read-Json 'manifest.json')
foreach ($entry in $manifest | Where-Object { $_.status -ne 'Collected' }) {
    $warnings.Add("Collection $($entry.status): $($entry.source) - $($entry.detail)")
}
$analysis = @(Read-Json 'analysis.json')
$metrics = @(Read-Json 'operation-metrics.json')
if ($manifest.Count -gt 0) {
    $files = @($manifest | Where-Object { $_.status -eq 'Collected' -and $_.file } | ForEach-Object {
        [pscustomobject]@{path=(Resolve-Source $_.file); source=$_.file; original=$_.source}
    })
} else {
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | ForEach-Object {
        [pscustomobject]@{path=$_.FullName; source=$_.FullName.Substring($root.Length).TrimStart('\','/'); original=$_.Name}
    })
}
$events = New-Object 'System.Collections.Generic.List[object]'
foreach ($file in $files | Where-Object { $_.path -match '\.evtx$' }) {
    try {
        $query = New-Object System.Diagnostics.Eventing.Reader.EventLogQuery($file.path, [System.Diagnostics.Eventing.Reader.PathType]::FilePath, '*[System[(EventID=1074 or EventID=12 or EventID=13 or EventID=2 or EventID=19 or EventID=41)]]')
        $reader = New-Object System.Diagnostics.Eventing.Reader.EventLogReader($query)
        try {
            while ($null -ne ($record = $reader.ReadEvent())) {
                try {
                    [xml]$xml = $record.ToXml()
                    $data = @{}
                    foreach ($node in $xml.SelectNodes('//*[local-name()="EventData"]/* | //*[local-name()="UserData"]//*[not(*)]')) {
                        $key = if ($node.HasAttribute('Name')) { $node.GetAttribute('Name') } else { $node.LocalName }
                        $data[$key] = $node.InnerText
                    }
                    $provider = $xml.Event.System.Provider.Name
                    $id = [int]$xml.Event.System.EventID.InnerText
                    if (-not $id) { $id = [int]([string]$xml.Event.System.EventID) }
                    $kind = $null
                    if ($provider -eq 'Microsoft-Windows-WindowsUpdateClient' -and $id -in 19,41 -and $data['updateGuid']) { $kind = if ($id -eq 19) {'UpdateInstalled'} else {'UpdateInstallStarted'} }
                    if ($provider -eq 'Microsoft-Windows-Kernel-General' -and $id -in 12,13) { $kind = if ($id -eq 12) {'Boot'} else {'Shutdown'} }
                    if ($provider -eq 'User32' -and $id -eq 1074) { $kind = if ($data['param5'] -eq 'restart') {'RestartRequested'} else {'PowerOffRequested'} }
                    if ($provider -eq 'Microsoft-Windows-Servicing' -and $id -eq 2 -and $data['IntendedPackageState'] -eq '5112' -and $data['ErrorCode'] -in '0x0','0x00000000','0') { $kind = 'PackageInstalled' }
                    if ($kind) { $events.Add([pscustomobject]@{time=[datetimeoffset]::Parse($xml.Event.System.TimeCreated.SystemTime); kind=$kind; updateId=([string]$data['updateGuid']).Trim('{}'); title=$data['updateTitle']; package=$data['PackageIdentifier']; source=($file.source + ' record ' + $record.RecordId)}) }
                } finally { $record.Dispose() }
            }
        } finally { $reader.Dispose() }
    } catch { $warnings.Add("Could not read $($file.source): $($_.Exception.Message)") }
}
$timelines = New-Object 'System.Collections.Generic.List[object]'
$agentFiles = @($files | Where-Object { [IO.Path]::GetFileName($_.path) -match '^(?:[0-9]+-)*UpdateAgent(?:[._-][a-z0-9_-]+)*\.(?:log|bak)(?:\.[0-9]+)?$' })
foreach ($file in $agentFiles) {
    if (-not (Test-Path -LiteralPath $file.path)) { $warnings.Add("Missing snapshot: $($file.source)"); continue }
    $attempts = New-Object 'System.Collections.Generic.List[object]'
    $byId = @{}; $current = $null; $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.path)) {
        $lineNumber++
        $local = [datetime]::MinValue
        if ($line.Length -lt 19 -or -not [datetime]::TryParseExact($line.Substring(0,19), 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$local)) { continue }
        if ($zone.IsInvalidTime($local) -or $zone.IsAmbiguousTime($local)) { $current=$null; continue }
        $time = [datetimeoffset]::new($local, $zone.GetUtcOffset($local))
        if ($line.Contains('UpdateAgent logging starts.')) { $current = $null }
        if ($line -match 'Initializing UpdateId = \[\{?([0-9a-fA-F-]{36})') {
            $key = $Matches[1].ToUpperInvariant(); $current = $byId[$key]
            if (-not $current) {
                $current = [pscustomobject]@{id=$key; attempted=$time; package=$null; downloadStart=$null; downloadEnd=$null; installStart=$null; pending=$null}
                $byId[$key]=$current; $attempts.Add($current)
            }
        }
        if (-not $current) { continue }
        $download = $line.TrimEnd().EndsWith(' GenerateDownloadRequest: Enter')
        $install = $line.TrimEnd().EndsWith(' Install: Enter')
        if (($download -or $install) -and ($time -lt $current.attempted -or ($time-$current.attempted).TotalDays -gt 2 -or $current.pending)) {
            $current = [pscustomobject]@{id=$current.id; attempted=$time; package=$null; downloadStart=$null; downloadEnd=$null; installStart=$null; pending=$null}
            $byId[$current.id]=$current; $attempts.Add($current)
        }
        $source = "$($file.source):$lineNumber"
        if ($download -and -not $current.downloadStart) { $current.downloadStart = Boundary $time 'Download preparation started' $source }
        if ($line -match 'Installing feature:.*Feature: CumulativeUpdate_(KB\d+)') { $current.package=$Matches[1].ToUpperInvariant() }
        if ($line.Contains('ReportEventDownloadRequestEnd:') -and $line -match 'DownloadComplete\s*=\s*\[TRUE\]' -and -not $current.downloadEnd) { $current.downloadEnd = Boundary $time 'Download complete' $source }
        if ($install -and -not $current.installStart) { $current.installStart = Boundary $time 'Installation started' $source }
        if ($current.installStart -and $line.TrimEnd().EndsWith(' Reboot required: [TRUE]') -and -not $current.pending) { $current.pending = Boundary $time 'Restart required' $source }
    }
    $active = @($attempts | Where-Object { $_.downloadStart -or $_.installStart } | Sort-Object attempted)
    foreach ($a in $active) {
        $identity = $events | Where-Object { $_.updateId -eq $a.id -and $_.time -ge $a.attempted -and $_.time -lt $a.attempted.AddDays(30) -and $_.title } | Sort-Object time | Select-Object -First 1
        $title = if ($identity) { $identity.title } elseif ($a.package) { 'Windows cumulative update - ' + $a.package } else { 'Windows OS update' }
        $start=$null; $end=$null; $wait=$null
        $milestones = @($a.downloadStart,$a.downloadEnd,$a.installStart,$a.pending | Where-Object { $null -ne $_ })
        if ($a.pending) {
            $pending = [datetimeoffset]::Parse($a.pending.time)
            $limit = $pending.AddDays(30)
            $next = $active | Where-Object { $_.attempted -gt $pending } | Select-Object -First 1
            if ($next -and $next.attempted -lt $limit) { $limit=$next.attempted }
            $candidates = @($events | Where-Object { $_.time -ge $pending -and $_.time -lt $limit } | Sort-Object time)
            $power = $candidates | Where-Object { $_.kind -in 'RestartRequested','PowerOffRequested','Shutdown' } | Select-Object -First 1
            if ($power -and $power.kind -ne 'PowerOffRequested') {
                $shutdown = $candidates | Where-Object { $_.kind -eq 'Shutdown' -and $_.time -ge $power.time -and ($_.time-$power.time).TotalMinutes -lt 10 } | Select-Object -First 1
                if ($shutdown) {
                    $restartLabel = if ($power.kind -eq 'RestartRequested') { 'Restart initiated (chronological association)' } else { 'Shutdown started (restart request missing)' }
                    $start = Boundary $power.time $restartLabel $power.source
                    $wait = ($power.time-$pending).ToString(); $milestones += $start
                    $boot = $candidates | Where-Object { $_.kind -eq 'Boot' -and $_.time -gt $shutdown.time -and ($_.time-$shutdown.time).TotalHours -lt 24 } | Select-Object -First 1
                    if ($boot) {
                        $off = $candidates | Where-Object { $_.kind -eq 'PowerOffRequested' -and $_.time -gt $shutdown.time } | Select-Object -First 1
                        $finishLimit = $power.time.AddHours(24)
                        if ($off -and $off.time -lt $finishLimit) { $finishLimit=$off.time }
                        $success = $candidates | Where-Object { $_.kind -eq 'UpdateInstalled' -and $_.updateId -eq $a.id -and $_.time -ge $boot.time -and $_.time -lt $finishLimit } | Select-Object -First 1
                        if (-not $success -and -not $identity -and $a.package) {
                            $success = $candidates | Where-Object { $_.kind -eq 'PackageInstalled' -and $_.package -eq $a.package -and $_.time -ge $boot.time -and $_.time -lt $finishLimit } | Select-Object -First 1
                        }
                        if ($success) {
                            $end = Boundary $success.time 'Matching update/package installation finished' $success.source
                            foreach ($step in $candidates | Where-Object { $_.time -gt $power.time -and $_.time -lt $success.time -and $_.kind -in 'Boot','RestartRequested' }) { $milestones += Boundary $step.time $step.kind $step.source }
                            $milestones += $end
                        }
                    }
                }
            }
        }
        $attempted = if ($a.downloadStart) {$a.downloadStart.time} else {$a.installStart.time}
        $status = if ($end) {'Update completed'} elseif ($a.pending) {'Restart required; completion unconfirmed'} else {'Partial evidence'}
        $timelines.Add([pscustomobject]@{updateId=$a.id; title=$title; attemptedAt=$attempted;
            download=(Phase 'Download' $a.downloadStart $a.downloadEnd 'Log estimate' 'Includes preparation, retries and transfer time.');
            install=(Phase 'Install to pending reboot' $a.installStart $a.pending 'Log boundaries' 'Ends at the first live restart-required transition.');
            reboot=(Phase 'Reboot to update finished' $start $end 'Correlated estimate' 'Requires shutdown, boot and matching update success or package completion. Does not measure desktop readiness.');
            waitingForRestart=$wait; milestones=@($milestones | Sort-Object time); status=$status})
    }
}
if ($agentFiles.Count -eq 0 -and $saved) {
    $warnings.Add('No raw UpdateAgent snapshots found; report uses saved phase evidence without reanalysis.')
    foreach ($t in $saved.updates) { $timelines.Add($t) }
    foreach ($w in $saved.warnings) { $warnings.Add([string]$w) }
}
$seen=@{}
$updates = @($timelines | Sort-Object @{Expression={@($_.milestones).Count};Descending=$true} | Where-Object {
    $key = $_.updateId + '|' + $_.attemptedAt
    if ($seen.ContainsKey($key)) { return $false }; $seen[$key]=$true
    Matches-Update $_.updateId $_.title
} | Sort-Object @{Expression={[datetimeoffset]::Parse($_.attemptedAt)};Descending=$true})
function ConvertTo-ReportHtml($Value) { [Net.WebUtility]::HtmlEncode([string]$Value) }
function Stamp($Value) { if (-not $Value) {'Not recorded'} else { ConvertTo-ReportHtml ([TimeZoneInfo]::ConvertTime([datetimeoffset]::Parse($Value),$zone).ToString('yyyy-MM-dd HH:mm:ss zzz')) } }
function Seconds($Value) { if ($null -ne $Value -and "$Value" -ne '') { [timespan]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture).TotalSeconds } }
function Show-Duration($Value) { if ($null -eq $Value -or "$Value" -eq '') {'Not recorded'} else { ConvertTo-ReportHtml ([timespan]::Parse([string]$Value).ToString()) } }
$html = New-Object Text.StringBuilder
[void]$html.Append('<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>WuPilot update performance</title><style>body{font:15px/1.6 Segoe UI,Arial;background:#f3f6fa;color:#182b40;margin:0}main{max-width:1100px;margin:auto;padding:32px}article,details{background:white;border:1px solid #dce4ec;border-radius:12px;padding:20px;margin:18px 0}.phases{display:flex;gap:16px;flex-wrap:wrap}.phase{flex:1;min-width:200px;border-top:4px solid #397ab5;padding:12px}strong.duration{display:block;font-size:28px}small{color:#526477}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:8px;border-bottom:1px solid #ddd;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere}.notice{background:#fff3d9;padding:12px}summary{cursor:pointer;font-weight:600}</style></head><body><main><h1>Update performance report</h1>')
[void]$html.Append('<p>Source time zone: ' + (ConvertTo-ReportHtml $zone.Id) + '. Selection: ' + (ConvertTo-ReportHtml $(if ($Update) {$Update} else {'All retained updates'})) + '</p>')
foreach ($w in $warnings) { [void]$html.Append('<p class="notice">'+(ConvertTo-ReportHtml $w)+'</p>') }
if ($updates.Count -eq 0) { [void]$html.Append('<p class="notice">No matching recognizable update attempts. Missing evidence does not imply zero duration.</p>') }
foreach ($t in $updates) {
    [void]$html.Append('<article><h2>'+(ConvertTo-ReportHtml $t.title)+'</h2><p>'+(Stamp $t.attemptedAt)+' &middot; '+(ConvertTo-ReportHtml $t.status)+'</p><small>'+(ConvertTo-ReportHtml $t.updateId)+'</small><div class="phases">')
    foreach ($phase in @($t.download,$t.install,$t.reboot)) {
        [void]$html.Append('<div class="phase"><h3>'+(ConvertTo-ReportHtml $phase.name)+'</h3><strong class="duration">'+(Show-Duration $phase.duration)+'</strong><small>'+(ConvertTo-ReportHtml $phase.confidence)+'</small><p>Start: '+(Stamp $phase.start.time)+'<br>End: '+(Stamp $phase.end.time)+'</p><small>'+(ConvertTo-ReportHtml $phase.explanation)+'</small></div>')
    }
    [void]$html.Append('</div><p>Waiting for restart: '+(Show-Duration $t.waitingForRestart)+'</p><details><summary>Evidence milestones</summary><table><tr><th>Time</th><th>Event</th><th>Source</th></tr>')
    foreach ($m in $t.milestones) { [void]$html.Append('<tr><td>'+(Stamp $m.time)+'</td><td>'+(ConvertTo-ReportHtml $m.label)+'</td><td>'+(ConvertTo-ReportHtml $m.source)+'</td></tr>') }
    [void]$html.Append('</table></details></article>')
}
foreach ($section in @(@{name='Retained operation metrics';items=@($metrics | Where-Object { Matches-Update $_.updateId $_.title })},@{name='Saved supporting log analysis';items=$analysis},@{name='Collection manifest';items=$manifest})) {
    [void]$html.Append('<details><summary>'+(ConvertTo-ReportHtml $section.name)+'</summary><pre>'+(ConvertTo-ReportHtml (ConvertTo-Json -InputObject @($section.items) -Depth 30))+'</pre></details>')
}
[void]$html.Append('<footer>Offline report. Missing boundaries remain unavailable. Saved supporting analysis is displayed as retained; raw UpdateAgent logs and event snapshots are reanalyzed.</footer></main></body></html>')
[void][IO.Directory]::CreateDirectory($output)
$utf8 = New-Object Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $output 'report.html'),$html.ToString(),$utf8)
$summary = [pscustomobject]@{timeZone=$zone.Id; selectedUpdate=$Update; warnings=@($warnings.ToArray()); updates=$updates}
[IO.File]::WriteAllText((Join-Path $output 'phase-summary.json'),(ConvertTo-Json -InputObject $summary -Depth 30),$utf8)
$rows = @($updates | ForEach-Object { [pscustomobject][ordered]@{UpdateId=$_.updateId;Title=$_.title;AttemptedAt=$_.attemptedAt;DownloadSeconds=(Seconds $_.download.duration);InstallSeconds=(Seconds $_.install.duration);WaitSeconds=(Seconds $_.waitingForRestart);RebootSeconds=(Seconds $_.reboot.duration);Status=$_.status} })
if ($rows.Count) { $rows | Export-Csv -LiteralPath (Join-Path $output 'update-timings.csv') -NoTypeInformation -Encoding UTF8 } else { [IO.File]::WriteAllText((Join-Path $output 'update-timings.csv'),"UpdateId,Title,AttemptedAt,DownloadSeconds,InstallSeconds,WaitSeconds,RebootSeconds,Status`r`n",$utf8) }
$report = Join-Path $output 'report.html'
if ($collecting) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $localZip = $work + '.zip'
    Write-Host 'Compressing evidence and reports'
    $archive=[IO.Compression.ZipFile]::Open(($localZip+'.partial'),[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in [IO.Directory]::EnumerateFiles($work,'*',[IO.SearchOption]::AllDirectories)) {
            $relative=$file.Substring($work.Length).TrimStart('\','/').Replace('\','/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file,$relative,[IO.Compression.CompressionLevel]::Fastest)
        }
    } finally { $archive.Dispose() }
    [IO.File]::Move(($localZip+'.partial'),$localZip)
    Write-Host "Local bundle: $work"
    Write-Host "Local ZIP: $localZip"
    try {
        [void][IO.Directory]::CreateDirectory($deliveryFolder)
        $delivered = Join-Path $deliveryFolder ([IO.Path]::GetFileName($localZip))
        if (-not $delivered.Equals($localZip,[StringComparison]::OrdinalIgnoreCase)) {
            $partial = $delivered + '.partial'
            [IO.File]::Copy($localZip,$partial,$false)
            if ((Get-FileHash -LiteralPath $localZip -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash) { throw 'Destination ZIP checksum verification failed.' }
            [IO.File]::Move($partial,$delivered)
        }
        Write-Host "Delivered ZIP: $delivered"
    } catch {
        Write-Warning "ZIP delivery failed: $($_.Exception.Message). Local ZIP retained at $localZip. A destination .partial file is incomplete."
    }
    $counts = $collection | Group-Object status | ForEach-Object { "$($_.Count) $($_.Name)" }
    Write-Host ($counts -join '; ')
}
if ($OpenReport) { Invoke-Item -LiteralPath $report }
Write-Output $report
