using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class ScanReportAnalyzer
{
    public static ScanInsights BuildInsights(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ScanInsights(
            report.Updates.Count,
            report.DriverCount,
            report.SoftwareCount,
            report.Updates.Count(static update => update.IsInstalled),
            report.Updates.Count(static update => update.IsDownloaded),
            report.Updates.Count(static update => update.IsHidden),
            report.Updates.Count(static update => update.IsMandatory),
            report.Updates.Count(static update => update.RebootRequired == true),
            report.Updates.Where(static update => update.MaximumDownloadBytes is not null).Sum(static update => update.MaximumDownloadBytes!.Value),
            report.Updates.Count(static update => update.MaximumDownloadBytes is null),
            report.ProviderResults.Count(static result => result.Succeeded),
            report.FailedProviderCount,
            report.CompletedAt - report.StartedAt);
    }

    public static ScanComparison Compare(ScanReport previous, ScanReport current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousById = LatestRevisionByUpdateId(previous.Updates);
        var currentById = LatestRevisionByUpdateId(current.Updates);
        var changes = new List<ScanUpdateChange>();
        var unchanged = 0;

        foreach (var updateId in previousById.Keys.Union(currentById.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            previousById.TryGetValue(updateId, out var previousUpdate);
            currentById.TryGetValue(updateId, out var currentUpdate);

            if (previousUpdate is null)
            {
                changes.Add(new ScanUpdateChange(
                    updateId,
                    currentUpdate!.Title,
                    ScanChangeKind.New,
                    null,
                    currentUpdate.RevisionNumber,
                    "Newly offered in the latest scan."));
                continue;
            }

            if (currentUpdate is null)
            {
                changes.Add(new ScanUpdateChange(
                    updateId,
                    previousUpdate.Title,
                    ScanChangeKind.Removed,
                    previousUpdate.RevisionNumber,
                    null,
                    "No longer offered in the latest scan."));
                continue;
            }

            if (previousUpdate.RevisionNumber != currentUpdate.RevisionNumber)
            {
                changes.Add(new ScanUpdateChange(
                    updateId,
                    currentUpdate.Title,
                    ScanChangeKind.RevisionChanged,
                    previousUpdate.RevisionNumber,
                    currentUpdate.RevisionNumber,
                    $"Revision changed from {previousUpdate.RevisionNumber} to {currentUpdate.RevisionNumber}."));
                continue;
            }

            var stateChanges = DescribeStateChanges(previousUpdate, currentUpdate);
            if (stateChanges.Count > 0)
            {
                changes.Add(new ScanUpdateChange(
                    updateId,
                    currentUpdate.Title,
                    ScanChangeKind.StateChanged,
                    previousUpdate.RevisionNumber,
                    currentUpdate.RevisionNumber,
                    string.Join("; ", stateChanges)));
            }
            else
            {
                unchanged++;
            }
        }

        return new ScanComparison(
            previous.ScanId,
            current.ScanId,
            changes
                .OrderBy(static change => change.Kind)
                .ThenBy(static change => change.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            unchanged);
    }

    private static Dictionary<string, UpdateRecord> LatestRevisionByUpdateId(IEnumerable<UpdateRecord> updates) =>
        updates
            .GroupBy(static update => update.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static update => update.RevisionNumber).First(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DescribeStateChanges(UpdateRecord previous, UpdateRecord current)
    {
        var changes = new List<string>();
        AddBooleanChange(changes, "installed", previous.IsInstalled, current.IsInstalled);
        AddBooleanChange(changes, "downloaded", previous.IsDownloaded, current.IsDownloaded);
        AddBooleanChange(changes, "hidden", previous.IsHidden, current.IsHidden);
        AddBooleanChange(changes, "mandatory", previous.IsMandatory, current.IsMandatory);
        AddNullableBooleanChange(changes, "restart required", previous.RebootRequired, current.RebootRequired);

        var previousProviders = previous.ProviderIds.Order(StringComparer.OrdinalIgnoreCase);
        var currentProviders = current.ProviderIds.Order(StringComparer.OrdinalIgnoreCase);
        if (!previousProviders.SequenceEqual(currentProviders, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add("offering sources changed");
        }

        return changes;
    }

    private static void AddBooleanChange(ICollection<string> changes, string name, bool previous, bool current)
    {
        if (previous != current)
        {
            changes.Add($"{name}: {previous} → {current}");
        }
    }

    private static void AddNullableBooleanChange(ICollection<string> changes, string name, bool? previous, bool? current)
    {
        if (previous != current)
        {
            changes.Add($"{name}: {Display(previous)} → {Display(current)}");
        }

        static string Display(bool? value) => value?.ToString() ?? "unknown";
    }
}
