using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class PolicyChangePlanner
{
    // Keep the original readback even if the user edits a staged value after refreshing.
    // The transaction must detect drift against the value that was first reviewed.
    public static StagedPolicyChange? Stage(PolicyState state, StagedPolicyChange? existing, string? value, bool remove)
    {
        if (!state.CanEdit) throw new InvalidOperationException(state.Status);
        var normalized = PolicyValueValidator.Normalize(state.Definition, value, remove);
        var before = existing is null ? state.RequestedValue : existing.BeforeValue;
        var initialEffective = existing is null ? state.EffectiveValue : existing.InitialEffectiveValue;
        var baseline = before;
        if (!remove && state.Definition.ValueKind == PolicyValueKind.Boolean && baseline is null)
            baseline = initialEffective ?? "0";
        if (remove ? before is null : string.Equals(baseline, normalized, StringComparison.OrdinalIgnoreCase))
            return null;
        return new(state.Definition.Id, state.Definition.DisplayName, before, normalized, remove,
            state.Ownership, state.Definition.Risk, state.Definition.RequiresRestart,
            state.Ownership is PolicyOwnership.Mdm or PolicyOwnership.GroupPolicy
                ? "A management refresh may ignore or revert this local request." : state.Status, initialEffective);
    }
}
