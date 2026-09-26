using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class UpgradeLogAnalyzerTests
{
    [Fact]
    public void SplitsAttemptsAndPhasesWithoutInventingDurations()
    {
        var result = UpgradeLogAnalyzer.Analyze("setupact.log", [
            "2026-09-01 12:00:00 Info Phase: Downlevel",
            "2026-09-01 12:03:00 Info working",
            "2026-09-01 12:04:00 Info Phase: SafeOS",
            "2026-09-01 12:05:00 Error failed 0xC1900101",
            "2026-09-02 12:00:00 Info next attempt"]);
        Assert.Equal(3, result.Activity.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), result.Activity[0].ObservedSpan);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Activity[1].ObservedSpan);
        Assert.Equal(TimeSpan.Zero, result.Activity[2].ObservedSpan);
        Assert.Equal(4, Assert.Single(result.Findings).Line);
    }

    [Fact]
    public void MissingAndReversedTimestampsDoNotBecomeNegativeDurations()
    {
        var result = UpgradeLogAnalyzer.Analyze("setup.log", ["no timestamp error", "2026-09-01 12:00:00 Info", "2026-09-01 11:00:00 Info"]);
        Assert.Equal(2, result.Activity.Count);
        Assert.All(result.Activity, a => Assert.Equal(TimeSpan.Zero, a.ObservedSpan));
        Assert.Single(result.Findings);
    }

    [Fact]
    public void ReportsParsingLimit()
    {
        var result = UpgradeLogAnalyzer.Analyze("setup.log", ["one", "two", "three"], 2);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.LinesRead);
    }
}
