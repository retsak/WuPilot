namespace WuPilot.Core.Models;

public sealed record ScanInsights(
    int TotalUpdates,
    int DriverUpdates,
    int SoftwareUpdates,
    int InstalledUpdates,
    int DownloadedUpdates,
    int HiddenUpdates,
    int MandatoryUpdates,
    int RebootRequiredUpdates,
    long KnownMaximumDownloadBytes,
    int UpdatesWithUnknownSize,
    int SuccessfulProviders,
    int FailedProviders,
    TimeSpan Duration);

public enum ScanChangeKind
{
    New,
    Removed,
    RevisionChanged,
    StateChanged
}

public sealed record ScanUpdateChange(
    string UpdateId,
    string Title,
    ScanChangeKind Kind,
    int? PreviousRevision,
    int? CurrentRevision,
    string Summary);

public sealed record ScanComparison(
    Guid PreviousScanId,
    Guid CurrentScanId,
    IReadOnlyList<ScanUpdateChange> Changes,
    int UnchangedUpdates)
{
    public int NewUpdates => Changes.Count(static change => change.Kind == ScanChangeKind.New);
    public int RemovedUpdates => Changes.Count(static change => change.Kind == ScanChangeKind.Removed);
    public int RevisionChanges => Changes.Count(static change => change.Kind == ScanChangeKind.RevisionChanged);
    public int StateChanges => Changes.Count(static change => change.Kind == ScanChangeKind.StateChanged);
    public bool HasChanges => Changes.Count > 0;
}
