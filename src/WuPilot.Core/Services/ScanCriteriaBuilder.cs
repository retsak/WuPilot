using System.Text.RegularExpressions;
using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static partial class ScanCriteriaBuilder
{
    private const int MaximumCustomCriteriaLength = 2_048;

    public static string Build(ScanPreset preset, string? customCriteria = null) => preset switch
    {
        ScanPreset.MissingUpdates => "IsInstalled=0 and IsHidden=0",
        ScanPreset.MissingSoftware => "IsInstalled=0 and IsHidden=0 and Type='Software'",
        ScanPreset.MissingDrivers => "IsInstalled=0 and IsHidden=0 and Type='Driver'",
        ScanPreset.InstalledUpdates => "IsInstalled=1",
        ScanPreset.HiddenUpdates => "IsHidden=1",
        ScanPreset.EverythingApplicable => "IsInstalled=0",
        ScanPreset.Custom => ValidateCustom(customCriteria),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown scan preset.")
    };

    public static string ForIdentity(string updateId, int revisionNumber)
    {
        if (!Guid.TryParse(updateId, out _))
        {
            throw new ArgumentException("The update ID must be a GUID.", nameof(updateId));
        }

        if (revisionNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revisionNumber));
        }

        return $"UpdateID='{updateId}' and RevisionNumber={revisionNumber}";
    }

    public static string ValidateCustom(string? criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria))
        {
            throw new ArgumentException("Custom search criteria cannot be empty.", nameof(criteria));
        }

        var normalized = criteria.Trim();
        if (normalized.Length > MaximumCustomCriteriaLength)
        {
            throw new ArgumentException($"Custom search criteria cannot exceed {MaximumCustomCriteriaLength} characters.", nameof(criteria));
        }

        if (UnsafeCustomCriteriaPattern().IsMatch(normalized))
        {
            throw new ArgumentException("Custom search criteria contains unsupported control characters or statement separators.", nameof(criteria));
        }

        return normalized;
    }

    [GeneratedRegex("[;\\r\\n\\0]")]
    private static partial Regex UnsafeCustomCriteriaPattern();
}
