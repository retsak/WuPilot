namespace WuPilot.Core.Models;

public sealed record SavedScanProfile(
    Guid Id,
    string Name,
    IReadOnlyList<string> ProviderIds,
    ScanPreset Preset,
    string? CustomCriteria,
    bool IncludePotentiallySuperseded,
    string? CustomServiceId,
    string? OfflineCatalogPath,
    DateTimeOffset UpdatedAt)
{
    public static SavedScanProfile Create(
        string name,
        IEnumerable<string> providerIds,
        ScanPreset preset,
        string? customCriteria,
        bool includePotentiallySuperseded,
        string? customServiceId,
        string? offlineCatalogPath,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name is required.", nameof(name));
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > 80)
        {
            throw new ArgumentException("Profile name must be 80 characters or fewer.", nameof(name));
        }

        return new SavedScanProfile(
            id ?? Guid.NewGuid(),
            normalizedName,
            providerIds
                .Where(static providerId => !string.IsNullOrWhiteSpace(providerId))
                .Select(static providerId => providerId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            preset,
            string.IsNullOrWhiteSpace(customCriteria) ? null : customCriteria.Trim(),
            includePotentiallySuperseded,
            string.IsNullOrWhiteSpace(customServiceId) ? null : customServiceId.Trim(),
            string.IsNullOrWhiteSpace(offlineCatalogPath) ? null : offlineCatalogPath.Trim(),
            DateTimeOffset.Now);
    }
}
