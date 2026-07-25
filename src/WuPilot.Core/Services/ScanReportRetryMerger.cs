using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class ScanReportRetryMerger
{
    public static ScanReport Combine(
        ScanReport original,
        ScanReport retry,
        IEnumerable<string> retriedProviderIds)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(retriedProviderIds);

        var retriedIds = retriedProviderIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retryById = retry.ProviderResults.ToDictionary(
            static result => result.Provider.Id,
            StringComparer.OrdinalIgnoreCase);
        var combinedResults = new List<ProviderScanResult>(original.ProviderResults.Count);

        foreach (var originalResult in original.ProviderResults)
        {
            if (retriedIds.Contains(originalResult.Provider.Id) &&
                retryById.Remove(originalResult.Provider.Id, out var retriedResult))
            {
                combinedResults.Add(retriedResult);
            }
            else
            {
                combinedResults.Add(originalResult);
            }
        }

        combinedResults.AddRange(retryById.Values);
        return new ScanReport(
            original.SchemaVersion,
            Guid.NewGuid(),
            original.StartedAt,
            retry.CompletedAt,
            original.Criteria,
            retry.Device,
            combinedResults,
            ScanReportMerger.Merge(combinedResults));
    }
}
