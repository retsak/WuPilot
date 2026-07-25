using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class WatchlistTracker
{
    public static IReadOnlyList<WatchedUpdate> Refresh(IEnumerable<WatchedUpdate> watchlist, ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(report);

        var currentById = report.Updates
            .GroupBy(static update => update.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static update => update.RevisionNumber).First(),
                StringComparer.OrdinalIgnoreCase);

        return watchlist
            .Select(watched =>
            {
                if (!currentById.TryGetValue(watched.UpdateId, out var current))
                {
                    return watched with
                    {
                        LastCheckedAt = report.CompletedAt,
                        IsOfferedInLastScan = false
                    };
                }

                return WatchedUpdate.FromUpdate(current, watched.AddedAt) with
                {
                    LastCheckedAt = report.CompletedAt,
                    IsOfferedInLastScan = true
                };
            })
            .OrderByDescending(static watched => watched.IsOfferedInLastScan)
            .ThenBy(static watched => watched.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
