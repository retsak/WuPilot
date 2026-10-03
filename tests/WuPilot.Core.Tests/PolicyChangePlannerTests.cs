using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class PolicyChangePlannerTests
{
    private static PolicyState State(string? requested, string? effective = null, bool editable = true) =>
        new(PolicyCatalog.All.First(definition => definition.Id == "update.metered"), requested,
            effective ?? requested, PolicyOwnership.Local, true, editable, "Local setting");

    [Theory]
    [InlineData("0", "0", "1")]
    [InlineData("1", "1", "0")]
    [InlineData(null, "0", "1")]
    [InlineData(null, "1", "0")]
    public void ToggleBack_DiscardsPendingChange(string? requested, string effective, string desired)
    {
        var state = State(requested, effective);
        var staged = PolicyChangePlanner.Stage(state, null, desired, false);
        Assert.NotNull(staged);
        Assert.Equal(desired, staged.AfterValue);
        Assert.Null(PolicyChangePlanner.Stage(state, staged, effective, false));
    }

    [Fact]
    public void EditingAfterEffectiveDrift_UsesOriginalSwitchBaseline()
    {
        var state = State(null, "1");
        var staged = PolicyChangePlanner.Stage(state, null, "0", false);
        var refreshed = State(null, "0");
        Assert.Null(PolicyChangePlanner.Stage(refreshed, staged, "1", false));
        Assert.NotNull(PolicyChangePlanner.Stage(refreshed, staged, "0", false));
    }

    [Fact]
    public void EditingAfterRefresh_PreservesOriginalDriftExpectation()
    {
        var initial = State("0");
        var staged = PolicyChangePlanner.Stage(initial, null, "1", false);
        var refreshed = State("1");
        var edited = PolicyChangePlanner.Stage(refreshed, staged, null, true);
        Assert.NotNull(edited);
        Assert.Equal("0", edited.BeforeValue);
        Assert.True(edited.Remove);
    }

    [Fact]
    public void RemovingAnUnconfiguredValue_DiscardsPendingOverride()
    {
        var state = State(null, "0");
        var staged = PolicyChangePlanner.Stage(state, null, "1", false);
        Assert.Null(PolicyChangePlanner.Stage(state, staged, null, true));
    }

    [Fact]
    public void NonEditableControl_CannotBeStaged() =>
        Assert.Throws<InvalidOperationException>(() => PolicyChangePlanner.Stage(State("0", editable: false), null, "1", false));

    [Fact]
    public void InvalidBoolean_CannotBeStaged() =>
        Assert.Throws<ArgumentException>(() => PolicyChangePlanner.Stage(State("0"), null, "true", false));

    [Fact]
    public void ManagedControl_PreservesOwnershipWarning()
    {
        var change = PolicyChangePlanner.Stage(State("0") with { Ownership = PolicyOwnership.Mdm }, null, "1", false);
        Assert.NotNull(change);
        Assert.Equal(PolicyOwnership.Mdm, change.Ownership);
        Assert.Contains("management refresh", change.Status);
    }
}
