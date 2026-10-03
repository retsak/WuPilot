using WuPilot.Core.Services;
using WuPilot.Infrastructure.Windows.Export;

namespace WuPilot.Infrastructure.Tests;

public sealed class UpgradeReportServiceTests
{
    [Fact]
    public async Task RegenerationRefreshesPreviouslyTruncatedSupportingEvidence()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "WuPilot-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(bundle, "logs"));
            await File.WriteAllTextAsync(Path.Combine(bundle, "analysis.json"), "[{\"activity\":[],\"findings\":[],\"linesRead\":500000,\"truncated\":true}]");
            await File.WriteAllTextAsync(Path.Combine(bundle, "operation-metrics.json"), "[]");
            await File.WriteAllTextAsync(Path.Combine(bundle, "manifest.json"), "[{\"source\":\"setupact.log\",\"file\":\"logs/setupact.log\",\"status\":\"Collected\"}]");
            await File.WriteAllTextAsync(Path.Combine(bundle, "logs", "setupact.log"), "2026-09-01 12:00:00 Info Phase: SafeOS\n2026-09-01 12:01:00 Error failed");
            var report = await UpgradeReportService.RegenerateAsync(bundle, Path.Combine(bundle, "refreshed"), "UTC", default);
            Assert.Contains("Error failed", await File.ReadAllTextAsync(report));
        }
        finally
        {
            if (Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        }
    }

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
    [InlineData("19", "Microsoft-Windows-WindowsUpdateClient", "UpdateInstalled")]
    [InlineData("41", "Microsoft-Windows-WindowsUpdateClient", "UpdateInstallStarted")]
    [InlineData("20", "Microsoft-Windows-WindowsUpdateClient", null)]
    [InlineData("19", "UnrelatedProvider", null)]
    public void ReadsUpdateIdentityAndTitleWithoutTreatingFailureAsSuccess(string id, string provider, string? kind)
    {
        const string guid = "{ba6eafb7-6fd3-4fb9-8462-3d86b81835b3}";
        var xml = $"""
            <Event><System><Provider Name='{provider}'/><EventID>{id}</EventID><TimeCreated SystemTime='2026-10-03T00:35:39.5753174Z'/><EventRecordID>10362</EventRecordID></System><EventData><Data Name='updateTitle'>Windows 11, version 26H2</Data><Data Name='updateGuid'>{guid}</Data></EventData></Event>
            """;
        var parsed = UpgradeEventReader.Parse(xml, "System.evtx");
        Assert.Equal(kind, parsed?.Kind);
        if (parsed is not null)
        {
            Assert.Equal("ba6eafb7-6fd3-4fb9-8462-3d86b81835b3", parsed.UpdateId);
            Assert.Equal("Windows 11, version 26H2", parsed.Title);
        }
    }

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
