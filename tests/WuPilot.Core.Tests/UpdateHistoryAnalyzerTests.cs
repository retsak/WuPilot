using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class UpdateHistoryAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static UpdateHistoryRecord Event(int result = 4, int error = 0, int operation = 1, DateTimeOffset? date = null) =>
        new(date, "Test update", "Network unavailable", "update-id", 2, operation, result, error, "client", null, "service", null);

    [Fact]
    public void TimeFilterIncludesBoundaryButExcludesMissingAndOlderDates()
    {
        Assert.True(UpdateHistoryAnalyzer.Matches(Event(date: Now.AddDays(-7)), null, false, 0, 7, Now));
        Assert.False(UpdateHistoryAnalyzer.Matches(Event(date: Now.AddDays(-7).AddTicks(-1)), null, false, 0, 7, Now));
        Assert.False(UpdateHistoryAnalyzer.Matches(Event(), null, false, 0, 7, Now));
        Assert.True(UpdateHistoryAnalyzer.Matches(Event(), null, false, 0, 0, Now));
    }

    [Fact]
    public void FiltersCombineAndSearchDescriptionAndUnsignedErrorCode()
    {
        var record = Event(error: unchecked((int)0x8024402C), date: Now);
        Assert.True(UpdateHistoryAnalyzer.Matches(record, "  0x8024402c  ", true, 1, 30, Now));
        Assert.True(UpdateHistoryAnalyzer.Matches(record, "NETWORK", false, 0, 0, Now));
        Assert.False(UpdateHistoryAnalyzer.Matches(record, "network", true, 2, 0, Now));
        Assert.False(UpdateHistoryAnalyzer.Matches(Event(result: 2, date: Now), null, true, 0, 0, Now));
    }

    [Fact]
    public void GroupsPartialFailedAndAbortedEventsSeparatelyByOperation()
    {
        var records = new[] { Event(result: 3), Event(result: 4, date: Now), Event(result: 5, operation: 2), Event(result: 2) };
        var groups = UpdateHistoryAnalyzer.GroupFailures(records);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(1, groups[0].Operation);
        Assert.Equal(Now, groups[0].LatestAt);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void GuidanceDoesNotTreatZeroAsUnknownError()
    {
        Assert.DoesNotContain("Unknown", UpdateHistoryAnalyzer.Guidance(Event()));
        Assert.Contains("partial result", UpdateHistoryAnalyzer.Guidance(Event()));
        Assert.DoesNotContain("failure", UpdateHistoryAnalyzer.Guidance(Event(result: 2)));
        Assert.Contains("DNS", UpdateHistoryAnalyzer.Guidance(Event(error: unchecked((int)0x8024402C))));
        Assert.Contains("0xDEADBEEF", UpdateHistoryAnalyzer.Guidance(Event(error: unchecked((int)0xDEADBEEF))));
    }

    [Fact]
    public void SummaryDescribesLimitedScopeAndHandlesEmptyView()
    {
        var summary = UpdateHistoryAnalyzer.BuildFailureSummary([Event(), Event(result: 2)]);
        Assert.Contains("2 currently filtered events", summary);
        Assert.Contains("1 failed, aborted, or partially successful", summary);
        Assert.Contains("not unique updates", summary);
        Assert.DoesNotContain("Unknown", summary);
        Assert.Contains("No failures", UpdateHistoryAnalyzer.BuildFailureSummary([]));
    }
}
