using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class ScanReportAnalyzerTests
{
    [Fact]
    public void BuildInsights_SummarizesStateSizeProvidersAndDuration()
    {
        var installedDriver = Update("driver", 1, UpdateKind.Driver) with
        {
            IsInstalled = true,
            IsDownloaded = true,
            IsMandatory = true,
            RebootRequired = true,
            MaximumDownloadBytes = 4_096
        };
        var hiddenSoftware = Update("software", 1, UpdateKind.Software) with
        {
            IsHidden = true,
            MaximumDownloadBytes = null
        };
        var report = Report([installedDriver, hiddenSoftware], failedProviders: 1, duration: TimeSpan.FromSeconds(12));

        var insights = ScanReportAnalyzer.BuildInsights(report);

        Assert.Equal(2, insights.TotalUpdates);
        Assert.Equal(1, insights.DriverUpdates);
        Assert.Equal(1, insights.SoftwareUpdates);
        Assert.Equal(1, insights.InstalledUpdates);
        Assert.Equal(1, insights.DownloadedUpdates);
        Assert.Equal(1, insights.HiddenUpdates);
        Assert.Equal(1, insights.MandatoryUpdates);
        Assert.Equal(1, insights.RebootRequiredUpdates);
        Assert.Equal(4_096, insights.KnownMaximumDownloadBytes);
        Assert.Equal(1, insights.UpdatesWithUnknownSize);
        Assert.Equal(1, insights.SuccessfulProviders);
        Assert.Equal(1, insights.FailedProviders);
        Assert.Equal(TimeSpan.FromSeconds(12), insights.Duration);
    }

    [Fact]
    public void Compare_ClassifiesNewRemovedRevisionStateAndUnchangedUpdates()
    {
        var previous = Report(
        [
            Update("revision", 1),
            Update("removed", 1),
            Update("state", 1),
            Update("same", 1)
        ]);
        var current = Report(
        [
            Update("revision", 2),
            Update("state", 1) with { IsHidden = true },
            Update("same", 1),
            Update("new", 1)
        ]);

        var comparison = ScanReportAnalyzer.Compare(previous, current);

        Assert.Equal(1, comparison.NewUpdates);
        Assert.Equal(1, comparison.RemovedUpdates);
        Assert.Equal(1, comparison.RevisionChanges);
        Assert.Equal(1, comparison.StateChanges);
        Assert.Equal(1, comparison.UnchangedUpdates);
        Assert.Contains(comparison.Changes, static change => change.UpdateId == "revision" && change.Summary.Contains("1 to 2", StringComparison.Ordinal));
        Assert.Contains(comparison.Changes, static change => change.UpdateId == "state" && change.Summary.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compare_UsesLatestRevisionWhenReportContainsMultipleRevisions()
    {
        var previous = Report([Update("same-id", 1), Update("same-id", 2)]);
        var current = Report([Update("same-id", 2)]);

        var comparison = ScanReportAnalyzer.Compare(previous, current);

        Assert.False(comparison.HasChanges);
        Assert.Equal(1, comparison.UnchangedUpdates);
    }

    private static ScanReport Report(
        IReadOnlyList<UpdateRecord> updates,
        int failedProviders = 0,
        TimeSpan? duration = null)
    {
        var provider = UpdateProviderDefinition.BuiltIn.Single(static item => item.Id == "default");
        var started = DateTimeOffset.Parse("2026-07-24T12:00:00-05:00");
        var providerResults = new List<ProviderScanResult>
        {
            new(provider, started, started.AddSeconds(2), 2, [], updates)
        };
        for (var index = 0; index < failedProviders; index++)
        {
            var failedProvider = UpdateProviderDefinition.Custom($"00000000-0000-0000-0000-{index + 1:000000000000}", $"Failed {index + 1}");
            providerResults.Add(new ProviderScanResult(failedProvider, started, started.AddSeconds(1), 4, [], [], "0x80240001", "Failed"));
        }

        return new ScanReport(
            "1.0",
            Guid.NewGuid(),
            started,
            started.Add(duration ?? TimeSpan.FromSeconds(3)),
            "IsInstalled=0",
            new DeviceIdentity("TEST", null, null, null, null, null, null, null, null, null),
            providerResults,
            updates);
    }

    private static UpdateRecord Update(string id, int revision, UpdateKind kind = UpdateKind.Software) =>
        new(
            UpdateId: id,
            RevisionNumber: revision,
            Title: $"{id} title",
            Description: null,
            Kind: kind,
            ProviderIds: ["default"],
            ProviderNames: ["Policy default"],
            PrimaryProviderId: "default",
            KbArticleIds: [],
            CveIds: [],
            Categories: [],
            CategoryIds: [],
            MsrcSeverity: null,
            MinimumDownloadBytes: null,
            MaximumDownloadBytes: 1_024,
            IsInstalled: false,
            IsDownloaded: false,
            IsHidden: false,
            IsMandatory: false,
            IsUninstallable: false,
            EulaAccepted: false,
            RebootRequired: false,
            IsPresent: null,
            BrowseOnly: null,
            LastDeploymentChangeTime: null,
            SupportUrl: null,
            ReleaseNotes: null,
            DeploymentAction: null,
            DownloadPriority: null,
            InstallationImpact: null,
            RebootBehavior: null,
            CanRequestUserInput: false,
            Driver: null);
}
