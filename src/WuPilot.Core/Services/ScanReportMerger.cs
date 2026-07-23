using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class ScanReportMerger
{
    public static IReadOnlyList<UpdateRecord> Merge(IEnumerable<ProviderScanResult> providerResults)
    {
        var merged = new Dictionary<string, UpdateRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var update in providerResults.SelectMany(static result => result.Updates))
        {
            if (!merged.TryGetValue(update.IdentityKey, out var existing))
            {
                merged.Add(update.IdentityKey, update);
                continue;
            }

            var providerIds = existing.ProviderIds.Concat(update.ProviderIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var providerNames = existing.ProviderNames.Concat(update.ProviderNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            merged[update.IdentityKey] = existing with
            {
                ProviderIds = providerIds,
                ProviderNames = providerNames,
                Description = Prefer(existing.Description, update.Description),
                SupportUrl = Prefer(existing.SupportUrl, update.SupportUrl),
                ReleaseNotes = Prefer(existing.ReleaseNotes, update.ReleaseNotes),
                CveIds = existing.CveIds.Concat(update.CveIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                KbArticleIds = existing.KbArticleIds.Concat(update.KbArticleIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Categories = existing.Categories.Concat(update.Categories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                CategoryIds = existing.CategoryIds.Concat(update.CategoryIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Driver = MergeDriver(existing.Driver, update.Driver)
            };
        }

        return merged.Values
            .OrderByDescending(static update => update.IsDriver)
            .ThenBy(static update => update.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? Prefer(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static DriverMetadata? MergeDriver(DriverMetadata? first, DriverMetadata? second)
    {
        if (first is null) return second;
        if (second is null) return first;

        return first with
        {
            Manufacturer = Prefer(first.Manufacturer, second.Manufacturer),
            Provider = Prefer(first.Provider, second.Provider),
            Model = Prefer(first.Model, second.Model),
            DriverClass = Prefer(first.DriverClass, second.DriverClass),
            HardwareId = Prefer(first.HardwareId, second.HardwareId),
            VersionDate = first.VersionDate ?? second.VersionDate,
            Entries = first.Entries.Concat(second.Entries).Distinct().ToArray(),
            InstalledMatch = first.InstalledMatch ?? second.InstalledMatch
        };
    }
}
