using WuPilot.Core.Models;

namespace WuPilot.Core.Abstractions;

public interface IUpdateScanService
{
    Task<ScanReport> ScanAsync(ScanRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IUpdateActionService
{
    Task<UpdateActionResult> ExecuteAsync(UpdateActionRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IDiagnosticService
{
    Task<DiagnosticSnapshot> CollectAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    Task<RepairResult> RepairAsync(RepairAction action, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IDeviceIdentityProvider
{
    Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken);
}

public interface IInstalledDriverProvider
{
    Task<IReadOnlyList<InstalledDriverInfo>> GetInstalledDriversAsync(CancellationToken cancellationToken);
}

public interface IUpdateHistoryProvider
{
    Task<IReadOnlyList<UpdateHistoryRecord>> GetRecentHistoryAsync(int maximumCount, CancellationToken cancellationToken);
}

public interface IEvidenceExportService
{
    Task<string> ExportAsync(ScanReport report, DiagnosticSnapshot? diagnostics, IEnumerable<UpdateRecord>? selection, CancellationToken cancellationToken);
}

public interface IScanProfileStore
{
    Task<IReadOnlyList<SavedScanProfile>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(SavedScanProfile profile, CancellationToken cancellationToken);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}

public interface IUpdateSourceDiscoveryService
{
    Task<IReadOnlyList<UpdateSourceRegistration>> GetRegisteredSourcesAsync(CancellationToken cancellationToken);
}

public interface IWatchlistStore
{
    Task<IReadOnlyList<WatchedUpdate>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(WatchedUpdate update, CancellationToken cancellationToken);
    Task SaveAllAsync(IEnumerable<WatchedUpdate> updates, CancellationToken cancellationToken);
    Task DeleteAsync(string updateId, CancellationToken cancellationToken);
}

public interface IWindowsUpdateSettingsService
{
    Task<SettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<SettingChangeResult> ApplyAsync(IEnumerable<SettingChange> changes, CancellationToken cancellationToken);
    Task<SettingChangeResult> RestoreAsync(Guid auditEntryId, bool allowConflict, CancellationToken cancellationToken);
    Task<IReadOnlyList<SettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken);
    Task<string> ExportAuditAsync(CancellationToken cancellationToken);
}

public interface IDeliveryOptimizationService
{
    Task<DeliveryOptimizationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IOperationMetricStore
{
    Task<IReadOnlyList<OperationMetric>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(OperationMetric metric, CancellationToken cancellationToken);
}

public interface IAppUpdateService
{
    Task<AppReleaseInfo?> CheckAsync(Version currentVersion, bool force, CancellationToken cancellationToken);
    Task<DownloadedAppUpdate> DownloadAsync(AppReleaseInfo release, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
    void LaunchInstaller(DownloadedAppUpdate update);
}
