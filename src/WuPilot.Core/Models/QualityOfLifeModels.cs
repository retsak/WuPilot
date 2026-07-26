namespace WuPilot.Core.Models;

public sealed record WindowPlacement(
    int X = 100,
    int Y = 100,
    int Width = 1440,
    int Height = 900,
    bool IsMaximized = false);

public sealed record AppPreferences(
    int SchemaVersion = 1,
    WindowPlacement? Window = null,
    string NavigationTag = "scan",
    bool NavigationPaneOpen = false,
    string Theme = "System",
    IReadOnlyList<string>? ScanProviderIds = null,
    string ScanPreset = "MissingDrivers",
    string CustomServiceId = "",
    string OfflineCatalogPath = "",
    string CustomCriteria = "",
    bool IncludeSuperseded = false,
    string ResultFilter = "All",
    string ResultSort = "Default",
    int PerformanceRangeDays = 30,
    string PolicySearch = "",
    string PolicyCategory = "All",
    string PolicyOwnership = "All",
    string PolicyRisk = "All",
    string PolicyStateFilter = "All",
    bool ShowLegacyPolicies = false,
    IReadOnlyList<string>? FavoritePolicyIds = null,
    bool FlashTaskbarOnCompletion = true)
{
    public static AppPreferences Default { get; } = new(
        Window: new WindowPlacement(),
        ScanProviderIds: ["default"],
        FavoritePolicyIds: []);
}

public enum OperationRunState { Idle, Running, CancellationRequested, Succeeded, Failed, Cancelled }

public sealed record OperationStatus(
    Guid Id,
    string Operation,
    string OriginatingPage,
    string Stage,
    string Message,
    int? Percent,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    bool IsCancellable,
    OperationRunState State);

public enum CompletionSeverity { Information, Success, Warning, Error }

public sealed record CompletionNotice(
    Guid Id,
    DateTimeOffset CompletedAt,
    string Title,
    string Message,
    string SourcePage,
    CompletionSeverity Severity,
    bool IsAcknowledged = false);

public sealed record StagedPolicyChange(
    string PolicyId,
    string DisplayName,
    string? BeforeValue,
    string? AfterValue,
    bool Remove,
    PolicyOwnership Ownership,
    PolicyRisk Risk,
    bool RequiresRestart,
    string Status);

public enum ShellProgressState { None, Indeterminate, Normal, Paused, Error }
