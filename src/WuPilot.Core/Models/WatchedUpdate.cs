namespace WuPilot.Core.Models;

public sealed record WatchedUpdate(
    string UpdateId,
    int RevisionNumber,
    string Title,
    UpdateKind Kind,
    IReadOnlyList<string> ProviderNames,
    bool IsInstalled,
    bool IsDownloaded,
    bool IsHidden,
    string? OfferedDriverVersion,
    DateTimeOffset? OfferedDriverDate,
    DateTimeOffset AddedAt,
    DateTimeOffset LastCheckedAt,
    bool? IsOfferedInLastScan)
{
    public static WatchedUpdate FromUpdate(UpdateRecord update, DateTimeOffset? addedAt = null) =>
        new(
            update.UpdateId,
            update.RevisionNumber,
            update.Title,
            update.Kind,
            update.ProviderNames.ToArray(),
            update.IsInstalled,
            update.IsDownloaded,
            update.IsHidden,
            WuPilot.Core.Services.DriverVersionParser.InferFromTitle(update.Title),
            update.Driver?.VersionDate,
            addedAt ?? DateTimeOffset.Now,
            DateTimeOffset.Now,
            true);
}
