using WuPilot.Core.Services;
using WuPilot.Infrastructure.Windows.Export;

namespace WuPilot.Infrastructure.Tests;

public sealed class UpgradeReportServiceTests
{
    [Theory]
    [InlineData("UpdateAgent.log", true)]
    [InlineData("0021-UpdateAgent.Old.log", true)]
    [InlineData("UpdateAgent.20260918.log", true)]
    [InlineData("UpdateAgent.log.1", true)]
    [InlineData("UpdateAgent.log.bak", true)]
    [InlineData("NotUpdateAgent.log", false)]
    [InlineData("UpdateAgent.dll", false)]
    public void RecognizesOnlyCurrentAndRotatedTextLogs(string name, bool expected) =>
        Assert.Equal(expected, UpgradeReportService.IsUpdateAgentLog(name));

    [Theory]
    [InlineData("5112", "0x0", "PackageInstalled")]
    [InlineData("112", "0x0", null)]
    [InlineData("5112", "0x80070002", null)]
    public void RequiresSuccessfulInstalledState(string state, string error, string? expected)
    {
        var xml = $"""
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Servicing'/><EventID>2</EventID><TimeCreated SystemTime='2026-09-18T11:41:26Z'/><EventRecordID>1265</EventRecordID></System><UserData><CbsPackageChangeState xmlns='http://manifests.microsoft.com/win/2004/08/windows/setup_provider'><PackageIdentifier>KB5129195</PackageIdentifier><IntendedPackageState>{state}</IntendedPackageState><ErrorCode>{error}</ErrorCode></CbsPackageChangeState></UserData></Event>
            """;
        var parsed = UpgradeEventReader.Parse(xml, "Setup.evtx");
        Assert.Equal(expected, parsed?.Kind);
        if (parsed is not null) Assert.Equal("KB5129195", parsed.Package);
    }

    [Fact]
    public void ReportKeepsRawEvidenceCollapsedAndEscapesSourceText()
    {
        var html = UpgradeReportService.BuildHtml([], [new([], [new("<script>", 1, "Review", "<img src=x onerror=alert(1)>")], 1, false)], [], [], TimeZoneInfo.Utc, []);
        Assert.Contains("No complete OS update timeline", html);
        Assert.Contains("<details><summary>Log findings", html);
        Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("viewport", html);
    }

    [Fact]
    public void ManifestCannotReadOutsideBundle()
    {
        Assert.Throws<InvalidDataException>(() => UpgradeReportService.ResolveSource(Path.GetTempPath(), "../outside.evtx"));
    }

    [Fact]
    public void DisplaysFriendlyRoundedDurations()
    {
        Assert.Equal("1m 35s", UpgradeReportService.Duration(TimeSpan.FromSeconds(94.633)));
        Assert.Equal("15m 0s", UpgradeReportService.Duration(TimeSpan.FromSeconds(899.779)));
        Assert.Equal("Not recorded", UpgradeReportService.Duration(null));
    }
}
