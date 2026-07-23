using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class DriverVersionParserTests
{
    [Theory]
    [InlineData("Intel - SoftwareComponent - 1.63.1155.1", "1.63.1155.1")]
    [InlineData("Contoso Firmware 2025.10.1 - 3.2.9", "3.2.9")]
    [InlineData("Driver without version", null)]
    public void InferFromTitle_ReturnsLastDottedVersion(string title, string? expected) =>
        Assert.Equal(expected, DriverVersionParser.InferFromTitle(title));
}
