using WuPilot.Core.Models;

namespace WuPilot.Core.Tests;

public sealed class SavedScanProfileTests
{
    [Fact]
    public void Create_NormalizesNameProvidersAndOptionalFields()
    {
        var profile = SavedScanProfile.Create(
            "  Driver review  ",
            ["default", "DEFAULT", " windows-update "],
            ScanPreset.MissingDrivers,
            "  IsInstalled=0  ",
            true,
            "  service-id  ",
            "  C:\\scan.cab  ");

        Assert.Equal("Driver review", profile.Name);
        Assert.Equal(["default", "windows-update"], profile.ProviderIds);
        Assert.Equal("IsInstalled=0", profile.CustomCriteria);
        Assert.Equal("service-id", profile.CustomServiceId);
        Assert.Equal("C:\\scan.cab", profile.OfflineCatalogPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsEmptyName(string name)
    {
        Assert.Throws<ArgumentException>(() => SavedScanProfile.Create(
            name,
            [],
            ScanPreset.MissingUpdates,
            null,
            false,
            null,
            null));
    }
}
