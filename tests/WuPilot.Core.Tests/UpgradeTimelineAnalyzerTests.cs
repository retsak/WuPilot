using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class UpgradeTimelineAnalyzerTests
{
    private static readonly string[] Lines =
    [
        "2026-09-18 06:17:19, Info CDeploymentSession: Initializing UpdateId = [916031E1-9D13-48E9-A262-C8A0DB93FBAC.1]",
        "2026-09-18 06:17:20, Info GenerateDownloadRequest: Enter",
        "2026-09-18 06:17:36, Info Installing feature: Group: Microsoft, FMID: (null), Feature: CumulativeUpdate_KB5129195",
        "2026-09-18 06:17:45, Info GenerateDownloadRequest: Enter",
        "2026-09-18 06:19:18, Info ReportEventDownloadRequestEnd: DownloadComplete = [FALSE]",
        "2026-09-18 06:21:32, Info ReportEventDownloadRequestEnd: DownloadComplete = [TRUE]",
        "2026-09-18 06:21:40, Info Install: Enter",
        "2026-09-18 06:24:52, Info Reboot required: [TRUE]",
        "2026-09-25 16:21:15, Info CDeploymentSession: Deserialize RebootRequired = [TRUE]",
        "2026-09-25 16:21:15, Info GetPostRebootResult succeeded. Post reboot result: [0x0]"
    ];
    private static UpgradeSystemEvent Event(string clock, string kind, string? package = null) => new(DateTimeOffset.Parse("2026-09-18T" + clock + "-05:00"), kind, package, "test.evtx");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("TestCentral", TimeSpan.FromHours(-5), "Test", "Test");

    [Fact]
    public void FullTimingScanIncludesBoundariesBeyondSupportingLogLimit()
    {
        var lines = Enumerable.Repeat("Supporting detail", 500_001).Concat(Lines);
        var timeline = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.Old.log", lines, [], Zone));
        Assert.Equal(TimeSpan.FromSeconds(252), timeline.Download.Duration);
        Assert.Contains("UpdateAgent.Old.log:500003", timeline.Download.Start!.Source);
    }

    [Fact]
    public void FeatureUpdateCompletesOnlyOnMatchingUpdateIdentity()
    {
        var lines = new[]
        {
            "2026-10-02 19:32:53, Info CDeploymentSession: Initializing UpdateId = [BA6EAFB7-6FD3-4FB9-8462-3D86B81835B3.1]",
            "2026-10-02 19:32:54, Info GenerateDownloadRequest: Enter",
            "2026-10-02 19:33:05, Info Installing feature: Group: Microsoft, FMID: (null), Feature: GDR_KB5121794",
            "2026-10-02 19:33:20, Info ReportEventDownloadRequestEnd: DownloadComplete            = [TRUE]",
            "2026-10-02 19:33:31, Info Install: Enter",
            "2026-10-02 19:34:08, Info Reboot required: [TRUE]"
        };
        UpgradeSystemEvent Evidence(string time, string kind, string? id = null, string? title = null) =>
            new(DateTimeOffset.Parse($"2026-10-02T{time}-05:00"), kind, null, "test.evtx", id, title);
        var events = new[]
        {
            Evidence("19:33:29", "UpdateInstallStarted", "ba6eafb7-6fd3-4fb9-8462-3d86b81835b3", "Windows 11, version 26H2"),
            Evidence("19:34:35", "Shutdown"), Evidence("19:34:51", "Boot"),
            Evidence("19:35:02", "UpdateInstalled", "916031e1-9d13-48e9-a262-c8a0db93fbac", "Unrelated update"),
            Evidence("19:35:39", "UpdateInstalled", "ba6eafb7-6fd3-4fb9-8462-3d86b81835b3", "Windows 11, version 26H2")
        };
        var result = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", lines, events, Zone));
        Assert.Equal("Windows 11, version 26H2", result.Title);
        Assert.Equal(TimeSpan.FromSeconds(26), result.Download.Duration);
        Assert.Equal(TimeSpan.FromSeconds(37), result.Install.Duration);
        Assert.Equal(TimeSpan.FromSeconds(64), result.Reboot.Duration);
        Assert.Equal(TimeSpan.FromSeconds(27), result.WaitingForRestart);
        var incomplete = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", lines, events[..^1], Zone));
        Assert.Null(incomplete.Reboot.End);
    }

    [Fact]
    public void SeparatesWaitingAndIncludesAllBootsThroughPackageCompletion()
    {
        var result = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", Lines,
        [Event("06:39:51", "RestartRequested"), Event("06:40:11", "Shutdown"), Event("06:40:34", "Boot"),
         Event("06:40:52", "RestartRequested"), Event("06:40:59", "Shutdown"), Event("06:41:17", "Boot"),
         Event("06:41:26", "PackageInstalled", "KB5129195")], Zone));
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12), result.Download.Duration);
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(12), result.Install.Duration);
        Assert.Equal(TimeSpan.FromSeconds(95), result.Reboot.Duration);
        Assert.Equal(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59), result.WaitingForRestart);
        Assert.Equal(2, result.Milestones.Count(m => m.Label.StartsWith("Windows boot")));
    }

    [Fact]
    public void BootAndUnrelatedPackageDoNotCompleteUpdate()
    {
        var result = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", Lines,
        [Event("06:39:51", "RestartRequested"), Event("06:40:11", "Shutdown"), Event("06:40:34", "Boot"), Event("06:41:26", "PackageInstalled", "KB9999999")], Zone));
        Assert.Null(result.Reboot.Duration);
        Assert.Null(result.Reboot.End);
    }

    [Fact]
    public void PowerOffIsNotAnUpdateRestart()
    {
        var result = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", Lines,
        [Event("06:39:51", "PowerOffRequested"), Event("06:40:11", "Shutdown"), Event("06:40:34", "Boot"), Event("06:41:26", "PackageInstalled", "KB5129195")], Zone));
        Assert.Null(result.Reboot.Start);
    }

    [Fact]
    public void TruncatedStartAndPersistedRebootFlagRemainUnknown()
    {
        var result = Assert.Single(UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log",
            [Lines[0], Lines[5], Lines[6], Lines[8]], [], Zone));
        Assert.Null(result.Download.Duration);
        Assert.Null(result.Install.Duration);
        Assert.Null(result.WaitingForRestart);
    }

    [Fact]
    public void RepeatedInstallAfterPendingStartsNewAttempt()
    {
        var result = UpgradeTimelineAnalyzer.Analyze("UpdateAgent.log", Lines.Concat([
            "2026-09-26 06:21:40, Info Install: Enter", "2026-09-26 06:24:52, Info Reboot required: [TRUE]"]), [], Zone);
        Assert.Equal(2, result.Count);
        Assert.Null(result[0].Download.Duration);
    }
}
