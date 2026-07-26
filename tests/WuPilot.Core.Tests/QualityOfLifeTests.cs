using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class QualityOfLifeTests
{
    [Fact]
    public void WindowPlacement_IsClampedToVisibleWorkArea()
    {
        var result = WindowPlacementValidator.Clamp(new(-9_000, 4_000, 4_000, 100, true), 0, 0, 1920, 1080);

        Assert.Equal(0, result.X);
        Assert.Equal(440, result.Y);
        Assert.Equal(1920, result.Width);
        Assert.Equal(640, result.Height);
        Assert.True(result.IsMaximized);
    }

    [Theory]
    [InlineData(PolicyValueKind.Boolean, "true")]
    [InlineData(PolicyValueKind.Boolean, "2")]
    [InlineData(PolicyValueKind.Integer, "31")]
    [InlineData(PolicyValueKind.Choice, "9")]
    [InlineData(PolicyValueKind.DateTime, "not-a-date")]
    public void PolicyValueValidator_RejectsInvalidTypedValues(PolicyValueKind kind, string value)
    {
        var definition = Definition(kind);
        Assert.Throws<ArgumentException>(() => PolicyValueValidator.Normalize(definition, value, false));
    }

    [Fact]
    public void PolicyValueValidator_NormalizesDateAndInteger()
    {
        Assert.Equal("14", PolicyValueValidator.Normalize(Definition(PolicyValueKind.Integer), " 14 ", false));
        Assert.Equal("2026-07-25", PolicyValueValidator.Normalize(Definition(PolicyValueKind.DateTime), "2026-07-25", false));
    }

    [Fact]
    public void SettingChange_CanCaptureAnExpectedWindowsDefault()
    {
        var change = new SettingChange("update.example", "1", ExpectedRequestedValue: null, EnforceExpectedRequestedValue: true);
        Assert.True(change.EnforceExpectedRequestedValue);
        Assert.Null(change.ExpectedRequestedValue);
    }

    private static PolicyDefinition Definition(PolicyValueKind kind) => new(
        "test", "Test", "Test policy", "Test", kind, "Path", "Value", 0, 30,
        kind == PolicyValueKind.Choice ? new Dictionary<string, string> { ["0"] = "Off", ["1"] = "On" } : null);
}
