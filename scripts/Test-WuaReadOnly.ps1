[CmdletBinding()]
param(
    [ValidateSet('Default', 'WSUS', 'WindowsUpdate', 'MicrosoftUpdate', 'Store')]
    [string] $Provider = 'Default',

    [string] $Criteria = "IsInstalled=0 and IsHidden=0 and Type='Driver'",

    [ValidateRange(1, 500)]
    [int] $MaximumResults = 50,

    [string] $OfflineCab
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows Update Agent is available only on Windows.' }

$session = $null
$searcher = $null
$serviceManager = $null
$scanService = $null
$scanServiceId = $null

try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'WuPilot read-only smoke test'
    $searcher = $session.CreateUpdateSearcher()
    $searcher.ClientApplicationID = 'WuPilot read-only smoke test'
    $searcher.Online = [string]::IsNullOrWhiteSpace($OfflineCab)

    if (-not [string]::IsNullOrWhiteSpace($OfflineCab)) {
        if (-not (Test-Path -LiteralPath $OfflineCab -PathType Leaf)) { throw "Offline catalog not found: $OfflineCab" }
        if ([IO.Path]::GetExtension($OfflineCab) -ne '.cab') { throw 'Offline catalog must be a Microsoft-signed .cab file.' }
        $serviceManager = New-Object -ComObject Microsoft.Update.ServiceManager
        $scanService = $serviceManager.AddScanPackageService('WuPilot Read-Only Offline Scan', $OfflineCab, 0)
        $scanServiceId = [string] $scanService.ServiceID
        $searcher.ServerSelection = 3
        $searcher.ServiceID = $scanServiceId
    }
    else {
        $serviceId = $null
        switch ($Provider) {
            'Default' { $searcher.ServerSelection = 0 }
            'WSUS' { $searcher.ServerSelection = 1 }
            'WindowsUpdate' { $searcher.ServerSelection = 2 }
            'MicrosoftUpdate' { $searcher.ServerSelection = 3; $serviceId = '7971f918-a847-4430-9279-4a52d1efe18d' }
            'Store' { $searcher.ServerSelection = 3; $serviceId = '855e8a7c-ecb4-4ca3-b045-1dfa50104289' }
        }

        if ($serviceId) {
            $searcher.ServiceID = $serviceId
            $serviceManager = New-Object -ComObject Microsoft.Update.ServiceManager
            $services = $serviceManager.Services
            $registered = $false
            for ($index = 0; $index -lt $services.Count; $index++) {
                $service = $services.Item($index)
                try {
                    if ([string] $service.ServiceID -eq $serviceId) {
                        $registered = $true
                        break
                    }
                }
                finally {
                    if ($null -ne $service -and [Runtime.InteropServices.Marshal]::IsComObject($service)) {
                        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service)
                    }
                }
            }
            if (-not $registered) {
                throw "$Provider is not registered with Windows Update Agent. This read-only test will not change service registration."
            }
        }
    }

    $started = Get-Date
    $result = $searcher.Search($Criteria)
    $count = [Math]::Min([int] $result.Updates.Count, $MaximumResults)
    $updates = @(for ($index = 0; $index -lt $count; $index++) {
        $update = $result.Updates.Item($index)
        $driver = $null
        if ([int] $update.Type -eq 2) {
            $driver = [ordered]@{
                Manufacturer = $update.DriverManufacturer
                Provider = $update.DriverProvider
                Model = $update.DriverModel
                Class = $update.DriverClass
                HardwareId = $update.DriverHardwareID
                VersionDate = $update.DriverVerDate
            }
        }
        [ordered]@{
            Title = $update.Title
            UpdateId = [string] $update.Identity.UpdateID
            Revision = [int] $update.Identity.RevisionNumber
            Type = [int] $update.Type
            Downloaded = [bool] $update.IsDownloaded
            Hidden = [bool] $update.IsHidden
            MaximumDownloadBytes = [long] $update.MaxDownloadSize
            Driver = $driver
        }
    })

    [ordered]@{
        ComputerName = $env:COMPUTERNAME
        Provider = if ($OfflineCab) { 'OfflineScanPackage' } else { $Provider }
        Criteria = $Criteria
        StartedAt = $started
        CompletedAt = Get-Date
        ResultCode = [int] $result.ResultCode
        TotalUpdates = [int] $result.Updates.Count
        ReturnedUpdates = $updates.Count
        Updates = $updates
    } | ConvertTo-Json -Depth 6
}
finally {
    if ($serviceManager -and $scanServiceId) {
        try { $serviceManager.RemoveService($scanServiceId) } catch { Write-Warning $_.Exception.Message }
    }
    foreach ($item in @($scanService, $services, $searcher, $session, $serviceManager)) {
        if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
            [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
        }
    }
}
