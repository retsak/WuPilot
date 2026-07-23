using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class DriverEvidenceCorrelator
{
    public static IReadOnlyList<UpdateRecord> Enrich(
        IEnumerable<UpdateRecord> updates,
        IReadOnlyList<InstalledDriverInfo> installedDrivers)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(installedDrivers);

        return updates.Select(update => EnrichOne(update, installedDrivers)).ToArray();
    }

    public static InstalledDriverMatch? FindBestMatch(
        DriverMetadata? offered,
        IReadOnlyList<InstalledDriverInfo> installedDrivers)
    {
        if (offered is null || installedDrivers.Count == 0) return null;

        var offeredIds = new[] { offered.HardwareId }
            .Concat(offered.Entries.Select(static entry => entry.HardwareId))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (offeredIds.Length == 0) return null;

        InstalledDriverMatch? best = null;
        foreach (var installed in installedDrivers)
        {
            foreach (var offeredId in offeredIds)
            {
                var match = Score(installed, offeredId);
                if (match is null) continue;
                if (best is null || match.Confidence > best.Confidence ||
                    match.Confidence == best.Confidence && installed.DriverDate > best.Driver.DriverDate)
                {
                    best = match;
                }
            }
        }

        return best;
    }

    private static UpdateRecord EnrichOne(UpdateRecord update, IReadOnlyList<InstalledDriverInfo> installedDrivers)
    {
        if (update.Driver is null) return update;
        return update with
        {
            Driver = update.Driver with
            {
                InstalledMatch = FindBestMatch(update.Driver, installedDrivers)
            }
        };
    }

    private static InstalledDriverMatch? Score(InstalledDriverInfo installed, string offeredId)
    {
        var offered = Normalize(offeredId);
        if (offered.Length == 0) return null;

        var hardware = Normalize(installed.HardwareId);
        if (hardware == offered)
        {
            return new InstalledDriverMatch(installed, 100, "Installed hardware ID", offeredId);
        }

        var compatible = Normalize(installed.CompatibleId);
        if (compatible == offered)
        {
            return new InstalledDriverMatch(installed, 98, "Installed compatible ID", offeredId);
        }

        var device = Normalize(installed.DeviceId);
        if (device == offered)
        {
            return new InstalledDriverMatch(installed, 96, "PnP device instance ID", offeredId);
        }

        if (device.StartsWith(offered + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledDriverMatch(installed, 92, "PnP device instance prefix", offeredId);
        }

        if (hardware.Length > 0 && (hardware.StartsWith(offered + "&", StringComparison.OrdinalIgnoreCase) || offered.StartsWith(hardware + "&", StringComparison.OrdinalIgnoreCase)))
        {
            return new InstalledDriverMatch(installed, 88, "Hardware ID family", offeredId);
        }

        if (compatible.Length > 0 && (compatible.StartsWith(offered + "&", StringComparison.OrdinalIgnoreCase) || offered.StartsWith(compatible + "&", StringComparison.OrdinalIgnoreCase)))
        {
            return new InstalledDriverMatch(installed, 86, "Compatible ID family", offeredId);
        }

        return null;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\').ToUpperInvariant();
}
