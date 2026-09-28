using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class UpdateTimingFilterTests
{
    [Theory]
    [InlineData("KB12345", "Update (KB12345)", true)]
    [InlineData("12345", "Update (kb12345)", true)]
    [InlineData("KB12345", "Update (KB123456)", false)]
    [InlineData("KB12345", "Update (KB99999)", false)]
    [InlineData("", null, true)]
    [InlineData("invalid", "invalid", false)]
    public void MatchesWholeKb(string filter, string? title, bool expected) =>
        Assert.Equal(expected, UpdateTimingFilter.Matches(filter, null, title));

    [Fact]
    public void MatchesGuidWithoutDependingOnTitle() =>
        Assert.True(UpdateTimingFilter.Matches("{916031e1-9d13-48e9-a262-c8a0db93fbac}", "916031E1-9D13-48E9-A262-C8A0DB93FBAC", null));
}
