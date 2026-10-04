using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public sealed record HistoryFailureGroup(int HResult, int Operation, int Count, DateTimeOffset? LatestAt, string? LatestTitle);

public static class UpdateHistoryAnalyzer
{
    public static bool IsFailure(UpdateHistoryRecord record) => record.ResultCode is 3 or 4 or 5;

    public static bool Matches(UpdateHistoryRecord record, string? query, bool failuresOnly, int operation, int days, DateTimeOffset now)
    {
        if (failuresOnly && !IsFailure(record) || operation != 0 && record.Operation != operation) return false;
        if (days > 0 && (record.Date is null || record.Date < now.AddDays(-days))) return false;
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        return new[] { record.Title, record.Description, record.UpdateId, record.ClientApplicationId, record.ServiceId,
                $"0x{unchecked((uint)record.HResult):X8}" }
            .Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    public static IReadOnlyList<HistoryFailureGroup> GroupFailures(IEnumerable<UpdateHistoryRecord> records) =>
        records.Where(IsFailure).GroupBy(record => (record.HResult, record.Operation))
            .Select(group =>
            {
                var latest = group.OrderByDescending(record => record.Date).First();
                return new HistoryFailureGroup(group.Key.HResult, group.Key.Operation, group.Count(), latest.Date, latest.Title);
            })
            .OrderByDescending(group => group.Count).ThenByDescending(group => group.LatestAt)
            .ThenBy(group => unchecked((uint)group.HResult)).ThenBy(group => group.Operation).ToArray();

    public static string OperationLabel(int operation) => operation switch
    {
        1 => "Installation",
        2 => "Uninstallation",
        3 => "Other",
        _ => $"Operation {operation}"
    };

    public static string Guidance(UpdateHistoryRecord record) => record.HResult == 0
        ? IsFailure(record)
            ? "No error code was recorded. Review the event description and collected logs for the failure or partial result."
            : "No error code was recorded."
        : FormatExplanation(HResultCatalog.Explain(record.HResult));

    private static string FormatExplanation(HResultExplanation explanation) =>
        $"{explanation.Code} · {explanation.Name}{Environment.NewLine}{explanation.Explanation}{Environment.NewLine}Suggested next step: {explanation.Recommendation}";

    public static string BuildFailureSummary(IEnumerable<UpdateHistoryRecord> records)
    {
        var events = records.ToArray();
        var groups = GroupFailures(events);
        var lines = new List<string>
        {
            "WuPilot · Windows Update failure summary",
            $"Scope: {events.Length} currently filtered events from the loaded history (up to 500 records).",
            $"{groups.Sum(group => group.Count)} failed, aborted, or partially successful events · {groups.Count} error/operation groups",
            "Counts describe events, not unique updates; retained history may be incomplete."
        };
        foreach (var group in groups)
        {
            lines.Add(string.Empty);
            lines.Add($"0x{unchecked((uint)group.HResult):X8} · {OperationLabel(group.Operation)} · {group.Count} events");
            lines.Add($"Latest: {group.LatestAt?.ToString("g") ?? "Date unavailable"} · {group.LatestTitle ?? "Title unavailable"}");
            lines.Add(group.HResult == 0 ? "No error code was recorded; inspect event descriptions and collected logs."
                : FormatExplanation(HResultCatalog.Explain(group.HResult)));
        }
        if (groups.Count == 0) lines.Add("No failures or partial results in the current view.");
        return string.Join(Environment.NewLine, lines);
    }
}
