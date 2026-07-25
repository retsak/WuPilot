using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class WatchlistTrackerTests
{
    [Fact]
    public void Refresh_UpdatesCurrentRecordAndMarksMissingRecordNotOffered()
    {
        var addedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        var current = Update("current", 3) with { IsDownloaded = true };
        var watchedCurrent = WatchedUpdate.FromUpdate(Update("current", 1), addedAt);
        var watchedMissing = WatchedUpdate.FromUpdate(Update("missing", 1), addedAt);
        var report = Report([current]);

        var refreshed = WatchlistTracker.Refresh([watchedCurrent, watchedMissing], report);

        var currentResult = Assert.Single(refreshed, static update => update.UpdateId == "current");
        Assert.Equal(3, currentResult.RevisionNumber);
        Assert.True(currentResult.IsDownloaded);
        Assert.True(currentResult.IsOfferedInLastScan);
        Assert.Equal(addedAt, currentResult.AddedAt);

        var missingResult = Assert.Single(refreshed, static update => update.UpdateId == "missing");
        Assert.False(missingResult.IsOfferedInLastScan);
        Assert.Equal(report.CompletedAt, missingResult.LastCheckedAt);
    }

    private static ScanReport Report(IReadOnlyList<UpdateRecord> updates)
    {
        var provider = UpdateProviderDefinition.BuiltIn.Single(static item => item.Id == "default");
        var started = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        return new ScanReport(
            "1.0",
            Guid.NewGuid(),
            started,
            started.AddSeconds(2),
            "IsInstalled=0",
            new DeviceIdentity("TEST", null, null, null, null, null, null, null, null, null),
            [new ProviderScanResult(provider, started, started.AddSeconds(2), 2, [], updates)],
            updates);
    }

    private static UpdateRecord Update(string id, int revision) =>
        new(
            id, revision, $"{id} title", null, UpdateKind.Software,
            ["default"], ["Policy default"], "default", [], [], [], [], null,
            null, 1_024, false, false, false, false, false, false, false,
            null, null, null, null, null, null, null, null, null, false, null);
}
