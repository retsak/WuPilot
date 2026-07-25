namespace WuPilot.Core.Models;

public enum UpdateKind
{
    Unknown,
    Software,
    Driver
}

public enum ScanPreset
{
    MissingUpdates,
    MissingSoftware,
    MissingDrivers,
    InstalledUpdates,
    HiddenUpdates,
    EverythingApplicable,
    Custom
}

public sealed record ScanRequest(
    IReadOnlyList<UpdateProviderDefinition> Providers,
    ScanPreset Preset,
    string? CustomCriteria = null,
    bool IncludePotentiallySuperseded = false,
    bool Online = true);

public sealed record DriverMetadata(
    string? Manufacturer,
    string? Provider,
    string? Model,
    string? DriverClass,
    string? HardwareId,
    DateTimeOffset? VersionDate,
    int? DeviceProblemNumber,
    int? DeviceStatus,
    bool? BrowseOnly,
    bool? PerUser,
    IReadOnlyList<DriverEntry> Entries,
    InstalledDriverMatch? InstalledMatch = null)
{
    public static DriverMetadata Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, []);
}

public sealed record InstalledDriverInfo(
    string? DeviceId,
    string? DeviceName,
    string? HardwareId,
    string? CompatibleId,
    string? DeviceClass,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    string? Manufacturer,
    string? ProviderName,
    string? InfName,
    bool? IsSigned,
    string? Signer);

public sealed record InstalledDriverMatch(
    InstalledDriverInfo Driver,
    int Confidence,
    string MatchedOn,
    string OfferedIdentifier);

public sealed record DriverEntry(
    string? Manufacturer,
    string? Provider,
    string? Model,
    string? DriverClass,
    string? HardwareId,
    DateTimeOffset? VersionDate,
    int? DeviceProblemNumber,
    int? DeviceStatus);

public sealed record UpdateRecord(
    string UpdateId,
    int RevisionNumber,
    string Title,
    string? Description,
    UpdateKind Kind,
    IReadOnlyList<string> ProviderIds,
    IReadOnlyList<string> ProviderNames,
    string PrimaryProviderId,
    IReadOnlyList<string> KbArticleIds,
    IReadOnlyList<string> CveIds,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> CategoryIds,
    string? MsrcSeverity,
    long? MinimumDownloadBytes,
    long? MaximumDownloadBytes,
    bool IsInstalled,
    bool IsDownloaded,
    bool IsHidden,
    bool IsMandatory,
    bool IsUninstallable,
    bool EulaAccepted,
    bool? RebootRequired,
    bool? IsPresent,
    bool? BrowseOnly,
    DateTimeOffset? LastDeploymentChangeTime,
    string? SupportUrl,
    string? ReleaseNotes,
    int? DeploymentAction,
    int? DownloadPriority,
    int? InstallationImpact,
    int? RebootBehavior,
    bool? CanRequestUserInput,
    DriverMetadata? Driver)
{
    public string IdentityKey => $"{UpdateId.ToUpperInvariant()}:{RevisionNumber}";
    public bool IsDriver => Kind == UpdateKind.Driver;
}

public sealed record ProviderScanResult(
    UpdateProviderDefinition Provider,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ResultCode,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<UpdateRecord> Updates,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorCode is null;
}

public sealed record DeviceIdentity(
    string ComputerName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? OsCaption,
    string? OsVersion,
    string? OsBuild,
    string? Architecture,
    string? EntraDeviceId,
    string? TenantId);

public sealed record ScanReport(
    string SchemaVersion,
    Guid ScanId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Criteria,
    DeviceIdentity Device,
    IReadOnlyList<ProviderScanResult> ProviderResults,
    IReadOnlyList<UpdateRecord> Updates,
    string? TechnicianNotes = null)
{
    public int DriverCount => Updates.Count(static update => update.IsDriver);
    public int SoftwareCount => Updates.Count - DriverCount;
    public int FailedProviderCount => ProviderResults.Count(static result => !result.Succeeded);
}

public sealed record OperationProgress(
    string Stage,
    string Message,
    int? Percent = null,
    string? ProviderId = null);

public sealed record UpdateHistoryRecord(
    DateTimeOffset? Date,
    string? Title,
    string? Description,
    string? UpdateId,
    int? RevisionNumber,
    int Operation,
    int ResultCode,
    int HResult,
    string? ClientApplicationId,
    int? ServerSelection,
    string? ServiceId,
    string? SupportUrl);
