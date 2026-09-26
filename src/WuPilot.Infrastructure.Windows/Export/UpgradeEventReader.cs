using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using WuPilot.Core.Services;

namespace WuPilot.Infrastructure.Windows.Export;

internal static class UpgradeEventReader
{
    public static IReadOnlyList<UpgradeSystemEvent> Read(string path, string source, CancellationToken token)
    {
        var result = new List<UpgradeSystemEvent>();
        using var reader = new EventLogReader(new EventLogQuery(path, PathType.FilePath,
            "*[System[(EventID=1074 or EventID=12 or EventID=13 or EventID=2)]]"));
        for (var count = 0; count < 100_000; count++)
        {
            token.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null) return result;
            var parsed = Parse(record.ToXml(), source);
            if (parsed is not null) result.Add(parsed);
        }
        throw new InvalidDataException("Event parsing limit reached (100,000 events).");
    }

    internal static UpgradeSystemEvent? Parse(string xml, string source)
    {
        var document = XDocument.Parse(xml);
        var system = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
        string? SystemValue(string name) => system?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        var provider = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider")?.Attribute("Name")?.Value;
        var stamp = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) return null;
        var id = SystemValue("EventID");
        var reference = $"{source} · record {SystemValue("EventRecordID")}";
        string? Data(string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name || (e.Name.LocalName == "Data" && e.Attribute("Name")?.Value == name))?.Value;
        if (provider == "Microsoft-Windows-Kernel-General" && id is "12" or "13")
            return new(time, id == "12" ? "Boot" : "Shutdown", null, reference);
        if (provider == "User32" && id == "1074")
            return new(time, string.Equals(Data("param5"), "restart", StringComparison.OrdinalIgnoreCase) ? "RestartRequested" : "PowerOffRequested", null, reference);
        if (provider == "Microsoft-Windows-Servicing" && id == "2" && Data("IntendedPackageState") == "5112" && Data("ErrorCode") is "0x0" or "0x00000000" or "0")
            return new(time, "PackageInstalled", Data("PackageIdentifier"), reference);
        return null;
    }
}
