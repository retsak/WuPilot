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
