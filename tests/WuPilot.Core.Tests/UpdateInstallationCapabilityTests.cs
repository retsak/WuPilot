using WuPilot.Core.Models;

namespace WuPilot.Core.Tests;

public sealed class UpdateInstallationCapabilityTests
{
    [Fact]
    public void InteractiveUpdate_IsExcludedFromSilentInstallation()
    {
        var update = Create(canRequestUserInput: true);

        Assert.True(update.RequiresUserInput);
        Assert.False(update.CanInstallSilently);
        Assert.True(update.CanInstallInteractively);
        Assert.Equal("Windows Update or OEM support tool", update.InteractiveInstallMethod);
    }

    [Fact]
    public void NonInteractiveMissingUpdate_CanInstallSilently()
    {
        var update = Create(canRequestUserInput: false);

        Assert.False(update.RequiresUserInput);
        Assert.True(update.CanInstallSilently);
    }

    private static UpdateRecord Create(bool canRequestUserInput) => new(
        "update-id", 1, "Test driver", null, UpdateKind.Driver, ["windows-update"], ["Windows Update"],
        "windows-update", [], [], ["Drivers"], [], null, null, null, false, false, false, false, false,
        true, null, null, false, null, null, null, null, null, null, null, canRequestUserInput, null);
}
