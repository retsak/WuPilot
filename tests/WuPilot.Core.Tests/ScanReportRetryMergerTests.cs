using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class ScanReportRetryMergerTests
{
    [Fact]
    public void Combine_ReplacesOnlyRetriedProviderAndRebuildsMergedUpdates()
    {
        var policy = UpdateProviderDefinition.BuiltIn.Single(static item => item.Id == "default");
        var store = UpdateProviderDefinition.BuiltIn.Single(static item => item.Id == "store");
        var originalPolicyUpdate = Update("policy-update", policy);
        var retriedStoreUpdate = Update("store-update", store);
        var started = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var original = Report(
            started,
            [
                new ProviderScanResult(policy, started, started.AddSeconds(1), 2, [], [originalPolicyUpdate]),
                new ProviderScanResult(store, started, started.AddSeconds(1), 4, [], [], "0x80240001", "Failed")
            ]);
        var retry = Report(
            started.AddMinutes(1),
            [new ProviderScanResult(store, started.AddMinutes(1), started.AddMinutes(1).AddSeconds(1), 2, [], [retriedStoreUpdate])]);

        var combined = ScanReportRetryMerger.Combine(original, retry, ["store"]);

        Assert.Equal(2, combined.ProviderResults.Count);
        Assert.All(combined.ProviderResults, static result => Assert.True(result.Succeeded));
        Assert.Equal(["policy-update", "store-update"], combined.Updates.Select(static update => update.UpdateId).Order().ToArray());
        Assert.Equal(original.StartedAt, combined.StartedAt);
        Assert.Equal(retry.CompletedAt, combined.CompletedAt);
    }

    private static ScanReport Report(DateTimeOffset started, IReadOnlyList<ProviderScanResult> results) =>
        new(
            "1.0",
            Guid.NewGuid(),
            started,
            started.AddSeconds(2),
            "IsInstalled=0",
            new DeviceIdentity("TEST", null, null, null, null, null, null, null, null, null),
            results,
            ScanReportMerger.Merge(results));

    private static UpdateRecord Update(string id, UpdateProviderDefinition provider) =>
        new(
            id, 1, $"{id} title", null, UpdateKind.Software,
            [provider.Id], [provider.DisplayName], provider.Id, [], [], [], [], null,
            null, 1_024, false, false, false, false, false, false, false,
            null, null, null, null, null, null, null, null, null, false, null);
}
