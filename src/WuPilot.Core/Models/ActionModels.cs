namespace WuPilot.Core.Models;

public enum UpdateAction
{
    Download,
    Install,
    Hide,
    Show
}

public sealed record UpdateActionRequest(
    UpdateRecord Update,
    UpdateProviderDefinition Provider,
    UpdateAction Action,
    bool AcceptEula = false);

public sealed record UpdateActionResult(
    UpdateAction Action,
    string UpdateId,
    int ResultCode,
    int HResult,
    bool RebootRequired,
    string Message,
    DateTimeOffset CompletedAt,
    TimeSpan RevalidationDuration = default,
    TimeSpan DownloadDuration = default,
    TimeSpan InstallDuration = default,
    TimeSpan TotalDuration = default,
    long? DownloadBytes = null)
{
    public bool Succeeded => ResultCode is 2 or 3;
}

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record DiagnosticFinding(
    string Id,
    string Title,
    DiagnosticSeverity Severity,
    string Summary,
    string? CurrentValue = null,
    string? ExpectedValue = null,
    string? Recommendation = null,
    IReadOnlyDictionary<string, string?>? Evidence = null);

public sealed record DiagnosticSnapshot(
    string SchemaVersion,
    Guid SnapshotId,
    DateTimeOffset CollectedAt,
    DeviceIdentity Device,
    string? WuaVersion,
    bool IsAdministrator,
    bool RebootPending,
    IReadOnlyDictionary<string, string?> Services,
    IReadOnlyDictionary<string, string?> Policies,
    IReadOnlyDictionary<string, string?> Connectivity,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyDictionary<string, string?> RawEvidence,
    IReadOnlyList<UpdateHistoryRecord>? UpdateHistory = null);

public enum RepairAction
{
    StartRequiredServices,
    ResetWindowsUpdateCache,
    ScanComponentStore,
    RestoreComponentStore,
    GenerateWindowsUpdateLog
}

public sealed record RepairResult(
    RepairAction Action,
    bool Succeeded,
    int ExitCode,
    string Summary,
    string Output,
    string Error,
    DateTimeOffset CompletedAt,
    string? RecoveryPath = null);
