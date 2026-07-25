using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Diagnostics;

public sealed class WindowsDiagnosticService(
    IDeviceIdentityProvider identityProvider,
    IUpdateHistoryProvider? historyProvider = null) : IDiagnosticService
{
    private static readonly string[] ServiceNames = ["wuauserv", "bits", "cryptsvc", "usosvc", "WaaSMedicSvc"];

    public async Task<DiagnosticSnapshot> CollectAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress("Diagnostics", "Reading device and Windows Update Agent state…", 5));
        var deviceTask = identityProvider.GetAsync(cancellationToken);
        var servicesTask = ReadServicesAsync(cancellationToken);
        var connectivityTask = ReadConnectivityAsync(cancellationToken);
        var wuaVersionTask = ReadWuaVersionAsync(cancellationToken);
        var historyTask = ReadHistorySafelyAsync(historyProvider, cancellationToken);
        var eventsTask = ReadRecentWindowsUpdateEventsAsync(cancellationToken);
        var supplementalEvidenceTask = ReadSupplementalEvidenceAsync(cancellationToken);

        var policies = ReadPolicies();
        var (rebootPending, rebootEvidence) = ReadPendingReboot();
        var device = await deviceTask.ConfigureAwait(false);
        var services = await servicesTask.ConfigureAwait(false);
        var connectivity = await connectivityTask.ConfigureAwait(false);
        var wuaVersion = await wuaVersionTask.ConfigureAwait(false);
        var history = await historyTask.ConfigureAwait(false);
        var recentEvents = await eventsTask.ConfigureAwait(false);
        var supplementalEvidence = await supplementalEvidenceTask.ConfigureAwait(false);
        var isAdministrator = IsAdministrator();
        var findings = BuildFindings(services, policies, connectivity, rebootPending, isAdministrator, history, supplementalEvidence);

        var rawEvidence = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PendingReboot"] = rebootEvidence,
            ["WindowsDirectory"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["SystemDirectory"] = Environment.SystemDirectory,
            ["ProcessArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["WindowsUpdateClientOperationalEvents"] = recentEvents
        };
        foreach (var evidence in supplementalEvidence) rawEvidence[evidence.Key] = evidence.Value;

        progress?.Report(new OperationProgress("Diagnostics", $"Collected {findings.Count} findings.", 100));
        return new DiagnosticSnapshot(
            "1.0",
            Guid.NewGuid(),
            DateTimeOffset.Now,
            device,
            wuaVersion,
            isAdministrator,
            rebootPending,
            services,
            policies,
            connectivity,
            findings,
            rawEvidence,
            history);
    }

    public async Task<RepairResult> RepairAsync(RepairAction action, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return new RepairResult(action, false, 5, "Administrator rights are required.", string.Empty, "Restart WuPilot as administrator.", DateTimeOffset.Now);
        }

        progress?.Report(new OperationProgress("Repair", $"Running {action}…", 10));
        var (file, arguments, timeout, recoveryPath) = BuildRepairCommand(action);
        var result = await ProcessRunner.RunAsync(file, arguments, timeout, cancellationToken).ConfigureAwait(false);
        var succeeded = result.ExitCode == 0;
        progress?.Report(new OperationProgress("Repair", succeeded ? "Repair action completed." : "Repair action failed.", 100));
        return new RepairResult(
            action,
            succeeded,
            result.ExitCode,
            succeeded ? "The action completed successfully. Re-run diagnostics and scan to verify the outcome." : "The action did not complete successfully.",
            result.Output.Trim(),
            result.Error.Trim(),
            DateTimeOffset.Now,
            recoveryPath);
    }

    private static IReadOnlyDictionary<string, string?> ReadPolicies()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        ReadRegistryValues(values, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
            "WUServer", "WUStatusServer", "DoNotConnectToWindowsUpdateInternetLocations", "DisableWindowsUpdateAccess",
            "ExcludeWUDriversInQualityUpdate", "SetPolicyDrivenUpdateSourceForDriverUpdates", "SetPolicyDrivenUpdateSourceForQualityUpdates",
            "TargetReleaseVersion", "TargetReleaseVersionInfo", "ProductVersion", "DisableDualScan");
        ReadRegistryValues(values, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
            "UseWUServer", "NoAutoUpdate", "AUOptions", "DetectionFrequencyEnabled", "DetectionFrequency");
        ReadRegistryValues(values, @"SOFTWARE\Microsoft\PolicyManager\current\device\Update",
            "ExcludeWUDriversInQualityUpdate", "AllowMUUpdateService", "BranchReadinessLevel", "DeferQualityUpdatesPeriodInDays");
        return values;
    }

    private static void ReadRegistryValues(IDictionary<string, string?> destination, string path, params string[] names)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        foreach (var name in names)
        {
            var value = key?.GetValue(name);
            destination[$"HKLM\\{path}\\{name}"] = value switch
            {
                null => null,
                string[] strings => string.Join("; ", strings),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadServicesAsync(CancellationToken cancellationToken)
    {
        var serviceList = string.Join(',', ServiceNames.Select(static name => $"'{name}'"));
        var script = $"Get-Service -Name {serviceList} -ErrorAction SilentlyContinue | ForEach-Object {{ [pscustomobject]@{{Name=$_.Name;Status=$_.Status.ToString();StartType=$_.StartType.ToString()}} }} | ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var services = ServiceNames.ToDictionary(static name => name, static _ => (string?)"Not found", StringComparer.OrdinalIgnoreCase);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return services;

        using var document = JsonDocument.Parse(result.Output);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        foreach (var element in elements)
        {
            var name = element.TryGetProperty("Name", out var nameValue) ? nameValue.GetString() : null;
            var status = element.TryGetProperty("Status", out var statusValue) ? statusValue.GetString() : null;
            var startType = element.TryGetProperty("StartType", out var typeValue) ? typeValue.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name)) services[name] = $"{status} ({startType})";
        }

        return services;
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadConnectivityAsync(CancellationToken cancellationToken)
    {
        var proxyTask = ProcessRunner.RunAsync("netsh.exe", ["winhttp", "show", "proxy"], TimeSpan.FromSeconds(10), cancellationToken);
        var dnsTask = ProcessRunner.PowerShellAsync("$r=Resolve-DnsName download.windowsupdate.com -ErrorAction SilentlyContinue | Select-Object -First 1; if($r){$r.IPAddress}else{'Resolution failed'}", TimeSpan.FromSeconds(15), cancellationToken);
        await Task.WhenAll(proxyTask, dnsTask).ConfigureAwait(false);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WinHTTP proxy"] = (await proxyTask.ConfigureAwait(false)).Output.Trim(),
            ["download.windowsupdate.com DNS"] = (await dnsTask.ConfigureAwait(false)).Output.Trim()
        };
    }

    private static async Task<string?> ReadWuaVersionAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.SystemDirectory, "wuapi.dll");
        var result = await ProcessRunner.PowerShellAsync($"(Get-Item -LiteralPath '{path.Replace("'", "''", StringComparison.Ordinal)}').VersionInfo.FileVersion", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static (bool Pending, string Evidence) ReadPendingReboot()
    {
        var evidence = new List<string>();
        if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } cbs)
        {
            evidence.Add("Component Based Servicing\\RebootPending");
            cbs.Dispose();
        }
        if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } wu)
        {
            evidence.Add("WindowsUpdate\\RebootRequired");
            wu.Dispose();
        }
        using var sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        if (sessionManager?.GetValue("PendingFileRenameOperations") is not null) evidence.Add("PendingFileRenameOperations");
        return (evidence.Count > 0, string.Join("; ", evidence));
    }

    private static IReadOnlyList<DiagnosticFinding> BuildFindings(
        IReadOnlyDictionary<string, string?> services,
        IReadOnlyDictionary<string, string?> policies,
        IReadOnlyDictionary<string, string?> connectivity,
        bool rebootPending,
        bool isAdministrator,
        IReadOnlyList<UpdateHistoryRecord> history,
        IReadOnlyDictionary<string, string?> supplementalEvidence)
    {
        var findings = new List<DiagnosticFinding>();
        if (!isAdministrator)
        {
            findings.Add(new DiagnosticFinding("security.not-admin", "Not running as administrator", DiagnosticSeverity.Warning, "Scanning may work, but secured WUA operations and repairs can fail.", Recommendation: "Restart WuPilot as administrator."));
        }

        foreach (var service in new[] { "wuauserv", "bits", "cryptsvc" })
        {
            if (!services.TryGetValue(service, out var state) || state?.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase) == true || state == "Not found")
            {
                findings.Add(new DiagnosticFinding($"service.{service}", $"{service} is not running", DiagnosticSeverity.Warning, "A required update component is stopped or unavailable.", state, "Running or trigger-start capable", "Use Start required services, then scan again."));
            }
        }

        var noInternetKey = policies.FirstOrDefault(static pair => pair.Key.EndsWith("DoNotConnectToWindowsUpdateInternetLocations", StringComparison.OrdinalIgnoreCase));
        if (noInternetKey.Value == "1")
        {
            findings.Add(new DiagnosticFinding("policy.no-internet-wu", "Public Microsoft update services are blocked by policy", DiagnosticSeverity.Warning, "Direct Windows Update, Microsoft Update, and Store scans may fail by design.", noInternetKey.Value, "0 or not configured", "Confirm intended policy with the Intune or Group Policy owner before changing it."));
        }

        var useWsusKey = policies.FirstOrDefault(static pair => pair.Key.EndsWith("AU\\UseWUServer", StringComparison.OrdinalIgnoreCase));
        if (useWsusKey.Value == "1")
        {
            findings.Add(new DiagnosticFinding("policy.wsus", "Managed WSUS is enabled", DiagnosticSeverity.Information, "The policy-default scan source is the configured intranet update service.", policies.FirstOrDefault(static pair => pair.Key.EndsWith("WUServer", StringComparison.OrdinalIgnoreCase)).Value));
        }

        if (rebootPending)
        {
            findings.Add(new DiagnosticFinding("system.reboot-pending", "A restart is pending", DiagnosticSeverity.Warning, "Pending servicing can block or distort update installation results.", "True", "False", "Restart the device before deeper repair work when operationally safe."));
        }

        if (connectivity.TryGetValue("download.windowsupdate.com DNS", out var dns) && dns?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            findings.Add(new DiagnosticFinding("network.dns", "Windows Update DNS resolution failed", DiagnosticSeverity.Error, "The device could not resolve a Microsoft update content hostname.", dns, "Resolved address", "Check DNS and proxy configuration."));
        }

        var recentFailure = history.FirstOrDefault(static item => item.ResultCode is 3 or 4);
        if (recentFailure is not null)
        {
            var explanation = Core.Services.HResultCatalog.Explain(recentFailure.HResult);
            findings.Add(new DiagnosticFinding(
                "history.recent-failure",
                "Recent Windows Update history contains a failure",
                DiagnosticSeverity.Warning,
                recentFailure.Title ?? "An update operation failed.",
                explanation.Code,
                "Successful operation",
                explanation.Recommendation,
                new Dictionary<string, string?>
                {
                    ["Date"] = recentFailure.Date?.ToString("O"),
                    ["Result"] = recentFailure.ResultCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Client"] = recentFailure.ClientApplicationId,
                    ["UpdateId"] = recentFailure.UpdateId
                }));
        }

        if (supplementalEvidence.TryGetValue("BITSJobs", out var bits) &&
            (bits?.Contains("\"JobState\":\"Error\"", StringComparison.OrdinalIgnoreCase) == true ||
             bits?.Contains("\"JobState\":\"TransientError\"", StringComparison.OrdinalIgnoreCase) == true))
        {
            findings.Add(new DiagnosticFinding(
                "bits.failed-job",
                "BITS contains a failed transfer job",
                DiagnosticSeverity.Warning,
                "A Background Intelligent Transfer Service job is in Error or TransientError state.",
                Recommendation: "Review bits-jobs.json in the evidence bundle before cancelling jobs owned by another application."));
        }

        if (supplementalEvidence.TryGetValue("SystemDriveSpace", out var diskJson) && TryReadFreeSpace(diskJson, out var freeBytes) && freeBytes < 15L * 1024 * 1024 * 1024)
        {
            findings.Add(new DiagnosticFinding(
                "disk.low-space",
                "System drive free space is low",
                freeBytes < 8L * 1024 * 1024 * 1024 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                "Low disk space can prevent update download, staging, component servicing, or rollback.",
                FormatBytes(freeBytes),
                "At least 15 GB for routine troubleshooting; feature updates may require more",
                "Free space using approved cleanup procedures, then retry the scan or installation."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new DiagnosticFinding("baseline.ok", "Baseline checks passed", DiagnosticSeverity.Information, "No common local configuration blocker was detected. This does not guarantee endpoint reachability or update applicability."));
        }

        return findings;
    }

    private static bool TryReadFreeSpace(string? json, out long freeBytes)
    {
        freeBytes = 0;
        if (string.IsNullOrWhiteSpace(json) || json.StartsWith("Collection failed", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("FreeSpace", out var value) &&
                (value.TryGetInt64(out freeBytes) || long.TryParse(value.ToString(), out freeBytes));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    private static async Task<IReadOnlyList<UpdateHistoryRecord>> ReadHistorySafelyAsync(
        IUpdateHistoryProvider? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null) return [];
        try
        {
            return await provider.GetRecentHistoryAsync(100, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new UpdateHistoryRecord(DateTimeOffset.Now, "History collection failed", exception.Message, null, null, 0, 4, exception.HResult, "WuPilot", null, null, null)];
        }
    }

    private static async Task<string?> ReadRecentWindowsUpdateEventsAsync(CancellationToken cancellationToken)
    {
        const string script = "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-WindowsUpdateClient/Operational';StartTime=(Get-Date).AddDays(-7)} -MaxEvents 50 -ErrorAction Stop | Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message | ConvertTo-Json -Compress -Depth 3";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output.Trim() : $"Collection failed: {result.Error.Trim()}";
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadSupplementalEvidenceAsync(CancellationToken cancellationToken)
    {
        const string wuaServicesScript = "$m=New-Object -ComObject Microsoft.Update.ServiceManager; $items=@(); for($i=0;$i -lt $m.Services.Count;$i++){ $s=$m.Services.Item($i); $items += [pscustomobject]@{Name=$s.Name;ServiceId=$s.ServiceID;IsManaged=$s.IsManaged;IsDefaultAuService=$s.IsDefaultAUService;IsScanPackageService=$s.IsScanPackageService;OffersWindowsUpdates=$s.OffersWindowsUpdates} }; $items|ConvertTo-Json -Compress";
        const string bitsScript = "Get-BitsTransfer -AllUsers -ErrorAction Stop | Select-Object DisplayName,JobState,BytesTotal,BytesTransferred,CreationTime,ErrorDescription | ConvertTo-Json -Compress -Depth 3";
        const string cbsScript = "$p=Join-Path $env:windir 'Logs\\CBS\\CBS.log'; if(Test-Path -LiteralPath $p){Select-String -LiteralPath $p -Pattern 'error' -SimpleMatch | Select-Object -Last 100 | ForEach-Object {$_.Line}}";
        const string setupApiScript = "$p=Join-Path $env:windir 'inf\\setupapi.dev.log'; if(Test-Path -LiteralPath $p){Get-Content -LiteralPath $p -Tail 300}";
        const string setupDiagScript = "$p=Join-Path $env:windir 'Logs\\SetupDiag\\SetupDiagResults.xml'; if(Test-Path -LiteralPath $p){$v=Get-Content -LiteralPath $p -Raw; if($v.Length -gt 200000){$v.Substring(0,200000)}else{$v}}";
        const string diskScript = "Get-CimInstance Win32_LogicalDisk -Filter \"DeviceID='$env:SystemDrive'\" | Select-Object DeviceID,Size,FreeSpace | ConvertTo-Json -Compress";

        var tasks = new Dictionary<string, Task<ProcessResult>>(StringComparer.OrdinalIgnoreCase)
        {
            ["WuaRegisteredServices"] = ProcessRunner.PowerShellAsync(wuaServicesScript, TimeSpan.FromSeconds(20), cancellationToken),
            ["BITSJobs"] = ProcessRunner.PowerShellAsync(bitsScript, TimeSpan.FromSeconds(20), cancellationToken),
            ["CBSLogErrors"] = ProcessRunner.PowerShellAsync(cbsScript, TimeSpan.FromSeconds(20), cancellationToken),
            ["SetupApiDeviceLogTail"] = ProcessRunner.PowerShellAsync(setupApiScript, TimeSpan.FromSeconds(20), cancellationToken),
            ["SetupDiagResults"] = ProcessRunner.PowerShellAsync(setupDiagScript, TimeSpan.FromSeconds(20), cancellationToken),
            ["SystemDriveSpace"] = ProcessRunner.PowerShellAsync(diskScript, TimeSpan.FromSeconds(20), cancellationToken)
        };

        await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        return tasks.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Result.ExitCode == 0 ? (string?)pair.Value.Result.Output.Trim() : $"Collection failed: {pair.Value.Result.Error.Trim()}",
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static (string File, IReadOnlyList<string> Arguments, TimeSpan Timeout, string? RecoveryPath) BuildRepairCommand(RepairAction action)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var softwareDistributionRecovery = Path.Combine(windows, $"SoftwareDistribution.WuPilot.{stamp}");
        var catrootRecovery = Path.Combine(windows, "System32", $"catroot2.WuPilot.{stamp}");
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, $"WindowsUpdate-{stamp}.log");

        return action switch
        {
            RepairAction.StartRequiredServices => PowerShell("'cryptsvc','bits','wuauserv' | ForEach-Object { Start-Service -Name $_ -ErrorAction Continue }; Get-Service cryptsvc,bits,wuauserv | Format-Table Name,Status,StartType -AutoSize", TimeSpan.FromMinutes(2)),
            RepairAction.ResetWindowsUpdateCache => PowerShell(
                "$ErrorActionPreference='Stop'; Stop-Service wuauserv,bits,cryptsvc -Force; " +
                $"if(Test-Path -LiteralPath '{Path.Combine(windows, "SoftwareDistribution").Replace("'", "''", StringComparison.Ordinal)}'){{Rename-Item -LiteralPath '{Path.Combine(windows, "SoftwareDistribution").Replace("'", "''", StringComparison.Ordinal)}' -NewName '{Path.GetFileName(softwareDistributionRecovery)}'}}; " +
                $"if(Test-Path -LiteralPath '{Path.Combine(windows, "System32", "catroot2").Replace("'", "''", StringComparison.Ordinal)}'){{Rename-Item -LiteralPath '{Path.Combine(windows, "System32", "catroot2").Replace("'", "''", StringComparison.Ordinal)}' -NewName '{Path.GetFileName(catrootRecovery)}'}}; " +
                "Start-Service cryptsvc,bits,wuauserv; Get-Service cryptsvc,bits,wuauserv | Format-Table Name,Status -AutoSize",
                TimeSpan.FromMinutes(5),
                $"{softwareDistributionRecovery}; {catrootRecovery}"),
            RepairAction.ScanComponentStore => ("dism.exe", ["/Online", "/Cleanup-Image", "/ScanHealth"], TimeSpan.FromMinutes(30), null),
            RepairAction.RestoreComponentStore => ("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"], TimeSpan.FromMinutes(60), null),
            RepairAction.GenerateWindowsUpdateLog => PowerShell($"Get-WindowsUpdateLog -LogPath '{logPath.Replace("'", "''", StringComparison.Ordinal)}'; Write-Output '{logPath.Replace("'", "''", StringComparison.Ordinal)}'", TimeSpan.FromMinutes(10), logPath),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown repair action.")
        };

        static (string, IReadOnlyList<string>, TimeSpan, string?) PowerShell(string script, TimeSpan timeout, string? recovery = null) =>
            ("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], timeout, recovery);
    }
}
