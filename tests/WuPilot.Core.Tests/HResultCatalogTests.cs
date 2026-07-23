using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class HResultCatalogTests
{
    [Fact]
    public void Explain_KnownWindowsUpdateError_ReturnsActionableName()
    {
        var result = HResultCatalog.Explain(unchecked((int)0x8024402C));
        Assert.Equal("WU_E_PT_WINHTTP_NAME_NOT_RESOLVED", result.Name);
        Assert.Contains("DNS", result.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_UnknownError_PreservesHexCode()
    {
        var result = HResultCatalog.Explain(unchecked((int)0xDEADBEEF));
        Assert.Equal("0xDEADBEEF", result.Code);
        Assert.Equal("Unknown", result.Name);
    }
}
