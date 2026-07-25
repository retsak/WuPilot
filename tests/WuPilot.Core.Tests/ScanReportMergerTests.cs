using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class ScanReportMergerTests
{
    [Fact]
    public void Merge_DeduplicatesIdentityAndPreservesSourcesAndEvidence()
    {
        var firstProvider = UpdateProviderDefinition.BuiltIn.Single(static provider => provider.Id == "default");
        var secondProvider = UpdateProviderDefinition.BuiltIn.Single(static provider => provider.Id == "microsoft-update");
        var first = MakeUpdate(firstProvider, description: null, cves: ["CVE-2026-0001"]);
        var second = MakeUpdate(secondProvider, description: "Driver description", cves: ["CVE-2026-0002"]);
        var now = DateTimeOffset.Now;

        var merged = ScanReportMerger.Merge([
            new ProviderScanResult(firstProvider, now, now, 2, [], [first]),
            new ProviderScanResult(secondProvider, now, now, 2, [], [second])
        ]);

        var update = Assert.Single(merged);
        Assert.Equal(["default", "microsoft-update"], update.ProviderIds);
        Assert.Equal("Driver description", update.Description);
        Assert.Equal(["CVE-2026-0001", "CVE-2026-0002"], update.CveIds);
    }

    [Fact]
    public void Merge_KeepsDifferentRevisionsSeparate()
    {
        var provider = UpdateProviderDefinition.BuiltIn[0];
        var now = DateTimeOffset.Now;
        var revisionOne = MakeUpdate(provider);
        var revisionTwo = revisionOne with { RevisionNumber = 2 };
        var merged = ScanReportMerger.Merge([new ProviderScanResult(provider, now, now, 2, [], [revisionOne, revisionTwo])]);
        Assert.Equal(2, merged.Count);
    }

    private static UpdateRecord MakeUpdate(UpdateProviderDefinition provider, string? description = "Description", IReadOnlyList<string>? cves = null) =>
        new(
            "12345678-1234-1234-1234-1234567890ab",
            1,
            "Contoso - Firmware - 1.2.3",
            description,
            UpdateKind.Driver,
            [provider.Id],
            [provider.DisplayName],
            provider.Id,
            [],
            cves ?? [],
            ["Drivers"],
            ["category"],
            null,
            1,
            2,
            false,
            false,
            false,
            false,
            false,
            true,
            false,
            false,
            false,
            null,
            null,
            null,
            1,
            1,
            0,
            1,
            false,
            new DriverMetadata("Contoso", "Contoso", "Model", "Firmware", "ACPI\\CONTOSO", DateTimeOffset.Parse("2026-01-01"), 0, 0, false, false, []));
}
