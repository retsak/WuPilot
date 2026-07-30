using WuPilot.Core.Models;

namespace WuPilot.Core.Tests;

public sealed class UpdateInstallationCapabilityTests
{
    [Fact]
    public void PromptCapableUpdate_IsStillInstallable()
    {
        var update = Create(canRequestUserInput: true);

        Assert.True(update.MayRequestUserInput);
        Assert.True(update.CanAttemptInstall);
    }

    [Fact]
    public void NonInteractiveMissingUpdate_CanBeAttempted()
    {
        var update = Create(canRequestUserInput: false);

        Assert.False(update.MayRequestUserInput);
        Assert.True(update.CanAttemptInstall);
    }

    private static UpdateRecord Create(bool canRequestUserInput) => new(
        "update-id", 1, "Test driver", null, UpdateKind.Driver, ["windows-update"], ["Windows Update"],
        "windows-update", [], [], ["Drivers"], [], null, null, null, false, false, false, false, false,
        true, null, null, false, null, null, null, null, null, null, null, canRequestUserInput, null);
}
