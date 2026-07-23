using WuPilot.Core.Models;

namespace WuPilot.Core.Tests;

public sealed class UpdateProviderDefinitionTests
{
    [Fact]
    public void BuiltInProviders_HaveUniqueIds() =>
        Assert.Equal(UpdateProviderDefinition.BuiltIn.Count, UpdateProviderDefinition.BuiltIn.Select(static provider => provider.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void Custom_RequiresServiceGuid()
    {
        var provider = UpdateProviderDefinition.Custom("12345678-1234-1234-1234-1234567890ab", "Lab service");
        Assert.Equal(UpdateServerSelection.Others, provider.ServerSelection);
        Assert.Equal("Lab service", provider.DisplayName);
        Assert.Throws<ArgumentException>(() => UpdateProviderDefinition.Custom("not-a-guid"));
    }

    [Fact]
    public void OfflineScanPackage_RequiresCabAndMarksMetadataOnlySource()
    {
        var provider = UpdateProviderDefinition.OfflineScanPackage(@"C:\support\Wsusscn2.cab");
        Assert.Equal("offline-scan", provider.Id);
        Assert.Equal(@"C:\support\Wsusscn2.cab", provider.ScanPackagePath);
        Assert.Null(provider.ServiceId);
        Assert.Throws<ArgumentException>(() => UpdateProviderDefinition.OfflineScanPackage(@"C:\support\catalog.zip"));
    }
}
