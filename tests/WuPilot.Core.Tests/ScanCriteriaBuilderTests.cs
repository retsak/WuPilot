using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class ScanCriteriaBuilderTests
{
    [Theory]
    [InlineData(ScanPreset.MissingUpdates, "IsInstalled=0 and IsHidden=0")]
    [InlineData(ScanPreset.MissingSoftware, "IsInstalled=0 and IsHidden=0 and Type='Software'")]
    [InlineData(ScanPreset.MissingDrivers, "IsInstalled=0 and IsHidden=0 and Type='Driver'")]
    [InlineData(ScanPreset.InstalledUpdates, "IsInstalled=1")]
    [InlineData(ScanPreset.HiddenUpdates, "IsHidden=1")]
    public void Build_ReturnsExpectedPreset(ScanPreset preset, string expected) =>
        Assert.Equal(expected, ScanCriteriaBuilder.Build(preset));

    [Fact]
    public void CustomCriteria_TrimsValidExpression() =>
        Assert.Equal("IsInstalled=0 and Type='Driver'", ScanCriteriaBuilder.Build(ScanPreset.Custom, "  IsInstalled=0 and Type='Driver'  "));

    [Theory]
    [InlineData("")]
    [InlineData("IsInstalled=0; Remove-Item C:\\")]
    [InlineData("IsInstalled=0\nType='Driver'")]
    public void CustomCriteria_RejectsUnsafeInput(string criteria) =>
        Assert.Throws<ArgumentException>(() => ScanCriteriaBuilder.Build(ScanPreset.Custom, criteria));

    [Fact]
    public void IdentityCriteria_RequiresGuidAndIncludesRevision()
    {
        const string id = "12345678-1234-1234-1234-1234567890ab";
        Assert.Equal($"UpdateID='{id}' and RevisionNumber=42", ScanCriteriaBuilder.ForIdentity(id, 42));
        Assert.Throws<ArgumentException>(() => ScanCriteriaBuilder.ForIdentity("not-a-guid", 1));
    }
}
