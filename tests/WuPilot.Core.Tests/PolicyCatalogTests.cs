using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class PolicyCatalogTests
{
    [Fact]
    public void Catalog_HasUniqueStableIdentifiersAndValidRanges()
    {
        Assert.True(PolicyCatalog.All.Count >= 45);
        Assert.Equal(PolicyCatalog.All.Count, PolicyCatalog.All.Select(static item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PolicyCatalog.All, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.Category));
            if (definition.Minimum is not null && definition.Maximum is not null)
                Assert.True(definition.Minimum <= definition.Maximum);
            if (definition.IsMdmOnly)
                Assert.Null(definition.RegistryPath);
        });
    }

    [Fact]
    public void Catalog_SeparatesEditablePoliciesFromMdmEvidence()
    {
        Assert.Contains(PolicyCatalog.All, static item => item.IsMdmOnly);
        Assert.Contains(PolicyCatalog.All, static item => item.IsPrivateUx);
        Assert.Contains(PolicyCatalog.All, static item => item.Category == "Delivery Optimization");
        Assert.Contains(PolicyCatalog.All, static item => item.Risk == PolicyRisk.High);
    }
}
