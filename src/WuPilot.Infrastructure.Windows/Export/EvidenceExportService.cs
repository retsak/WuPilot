using System.Net;
using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Infrastructure.Windows.Export;

public sealed class EvidenceExportService(string? exportRoot = null) : IEvidenceExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _exportRoot = exportRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WuPilot",
        "Exports");

    public async Task<string> ExportAsync(
        ScanReport report,
        DiagnosticSnapshot? diagnostics,
        IEnumerable<UpdateRecord>? selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var updates = (selection ?? report.Updates).DistinctBy(static update => update.IdentityKey).ToArray();
        var stamp = report.CompletedAt.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var safeComputer = string.Concat(report.Device.ComputerName.Select(static character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var directory = Path.Combine(_exportRoot, $"{safeComputer}-{stamp}-{report.ScanId.ToString("N")[..8]}");
        Directory.CreateDirectory(directory);

        await WriteJsonAsync(Path.Combine(directory, "scan-report.json"), report with { Updates = updates }, cancellationToken).ConfigureAwait(false);
        if (diagnostics is not null)
        {
            await WriteJsonAsync(Path.Combine(directory, "diagnostics.json"), diagnostics, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "update-history.csv"), BuildHistoryCsv(diagnostics.UpdateHistory ?? []), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
            if (diagnostics.RawEvidence.TryGetValue("WindowsUpdateClientOperationalEvents", out var events) && !string.IsNullOrWhiteSpace(events))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "windows-update-events.json"), events, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            await WriteRawEvidenceFileAsync(diagnostics, "WuaRegisteredServices", Path.Combine(directory, "wua-registered-services.json"), cancellationToken).ConfigureAwait(false);
            await WriteRawEvidenceFileAsync(diagnostics, "BITSJobs", Path.Combine(directory, "bits-jobs.json"), cancellationToken).ConfigureAwait(false);
            await WriteRawEvidenceFileAsync(diagnostics, "CBSLogErrors", Path.Combine(directory, "cbs-errors.txt"), cancellationToken).ConfigureAwait(false);
            await WriteRawEvidenceFileAsync(diagnostics, "SetupApiDeviceLogTail", Path.Combine(directory, "setupapi-device-tail.log"), cancellationToken).ConfigureAwait(false);
            await WriteRawEvidenceFileAsync(diagnostics, "SetupDiagResults", Path.Combine(directory, "setupdiag-results.xml"), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "driver-review.csv"), BuildCsv(report.Device.ComputerName, updates.Where(static update => update.IsDriver)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot");
        await CopyOptionalAsync(Path.Combine(localData, "settings-audit.json"), Path.Combine(directory, "settings-audit.json"), cancellationToken).ConfigureAwait(false);
        await CopyOptionalAsync(Path.Combine(localData, "operation-metrics.json"), Path.Combine(directory, "operation-metrics.json"), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "intune-review.html"), BuildHtml(report, diagnostics, updates), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.txt"), BuildReadme(report, updates), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return directory;
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, cancellationToken);

    private static Task WriteRawEvidenceFileAsync(DiagnosticSnapshot diagnostics, string key, string path, CancellationToken cancellationToken) =>
        diagnostics.RawEvidence.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? File.WriteAllTextAsync(path, value, Encoding.UTF8, cancellationToken)
            : Task.CompletedTask;

    private static async Task CopyOptionalAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source)) return;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCsv(string deviceName, IEnumerable<UpdateRecord> updates)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Title,OfferedVersion,OfferedDate,Manufacturer,Provider,Model,DriverClass,HardwareId,InstalledDevice,InstalledVersion,InstalledDate,InstalledInf,InstalledSigned,InstalledSigner,MatchConfidence,MatchBasis,UpdateId,Revision,ScanSources,DeviceName,Downloaded,Hidden");
        foreach (var update in updates)
        {
            var driver = update.Driver;
            builder.AppendLine(string.Join(',', new[]
            {
                update.Title,
                DriverVersionParser.InferFromTitle(update.Title),
                driver?.VersionDate?.ToString("O"),
                driver?.Manufacturer,
                driver?.Provider,
                driver?.Model,
                driver?.DriverClass,
                driver?.HardwareId,
                driver?.InstalledMatch?.Driver.DeviceName,
                driver?.InstalledMatch?.Driver.DriverVersion,
                driver?.InstalledMatch?.Driver.DriverDate?.ToString("O"),
                driver?.InstalledMatch?.Driver.InfName,
                driver?.InstalledMatch?.Driver.IsSigned?.ToString(),
                driver?.InstalledMatch?.Driver.Signer,
                driver?.InstalledMatch?.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                driver?.InstalledMatch?.MatchedOn,
                update.UpdateId,
                update.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(" | ", update.ProviderNames),
                deviceName,
                update.IsDownloaded.ToString(),
                update.IsHidden.ToString()
            }.Select(Csv)));
        }
        return builder.ToString();

        static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildHistoryCsv(IEnumerable<UpdateHistoryRecord> history)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Date,Title,Operation,ResultCode,HResult,UpdateId,Revision,ClientApplicationId,ServerSelection,ServiceId");
        foreach (var item in history)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                item.Date?.ToString("O"),
                item.Title,
                item.Operation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ResultCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"0x{unchecked((uint)item.HResult):X8}",
                item.UpdateId,
                item.RevisionNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ClientApplicationId,
                item.ServerSelection?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ServiceId
            }.Select(Csv)));
        }
        return builder.ToString();

        static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildHtml(ScanReport report, DiagnosticSnapshot? diagnostics, IReadOnlyList<UpdateRecord> updates)
    {
        var drivers = updates.Where(static update => update.IsDriver).ToArray();
        var rows = string.Join(Environment.NewLine, drivers.Select(update =>
        {
            var driver = update.Driver;
            var installed = driver?.InstalledMatch;
            return $"<tr><td>{H(update.Title)}</td><td>{H(DriverVersionParser.InferFromTitle(update.Title))}<br>{H(driver?.VersionDate?.ToString("yyyy-MM-dd"))}</td><td>{H(installed?.Driver.DriverVersion)}<br>{H(installed?.Driver.DriverDate?.ToString("yyyy-MM-dd"))}<br><small>{H(installed?.Driver.InfName)}</small></td><td>{H(driver?.Manufacturer)}</td><td>{H(driver?.Provider)}</td><td>{H(driver?.Model)}</td><td>{H(driver?.DriverClass)}</td><td><code>{H(driver?.HardwareId)}</code></td><td>{(installed is null ? "—" : $"{installed.Confidence}%<br><small>{H(installed.MatchedOn)}</small>")}</td><td><code>{H(update.UpdateId)}</code><br>rev {update.RevisionNumber}</td><td>{H(string.Join(", ", update.ProviderNames))}</td></tr>";
        }));
        var findings = diagnostics is null
            ? "<p>Diagnostics were not attached.</p>"
            : $"<ul>{string.Join(string.Empty, diagnostics.Findings.Select(finding => $"<li class=\"{finding.Severity.ToString().ToLowerInvariant()}\"><strong>{H(finding.Title)}</strong> — {H(finding.Summary)}</li>"))}</ul>";
        var historyFailures = diagnostics?.UpdateHistory?.Where(static item => item.ResultCode is 3 or 4).Take(20).ToArray() ?? [];
        var historyRows = string.Join(Environment.NewLine, historyFailures.Select(item => $"<tr><td>{H(item.Date?.ToString("u"))}</td><td>{H(item.Title)}</td><td>{item.ResultCode}</td><td><code>0x{unchecked((uint)item.HResult):X8}</code></td><td>{H(item.ClientApplicationId)}</td></tr>"));

        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>WuPilot Intune driver review</title>
            <style>body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:32px;color:#1b1b1b}h1{margin-bottom:4px}.meta{color:#555}table{border-collapse:collapse;width:100%;margin-top:20px}th,td{border:1px solid #d4d4d4;padding:8px;text-align:left;vertical-align:top}th{background:#f3f3f3}code{font:12px Consolas,monospace}.warning{color:#8a4b00}.error{color:#a80000}.information{color:#005a9e}.note{padding:12px;background:#fff4ce;border-left:4px solid #ffb900}</style></head>
            <body><h1>Intune driver review evidence</h1><p class="meta">Device {{H(report.Device.ComputerName)}} · Scan {{H(report.ScanId.ToString())}} · {{H(report.CompletedAt.ToString("u"))}}</p>
            <div class="note"><strong>Matching guidance:</strong> WUA UpdateID is a local catalog identity and is not the Intune driver inventory ID. In Intune, match on driver name, manufacturer, version/date, and class, then validate applicable-device counts before approval.</div>
            <h2>Technician notes</h2><p>{{H(report.TechnicianNotes ?? "No technician notes were supplied.")}}</p>
            <h2>Device</h2><dl><dt>Manufacturer / model</dt><dd>{{H(report.Device.Manufacturer)}} / {{H(report.Device.Model)}}</dd><dt>OS</dt><dd>{{H(report.Device.OsCaption)}} {{H(report.Device.OsVersion)}} ({{H(report.Device.OsBuild)}})</dd><dt>Entra device ID</dt><dd><code>{{H(report.Device.EntraDeviceId)}}</code></dd></dl>
            <h2>Drivers ({{drivers.Length}})</h2><table><thead><tr><th>Name</th><th>Offered version* / date</th><th>Installed version / date</th><th>Manufacturer</th><th>Provider</th><th>Model</th><th>Class</th><th>Hardware ID</th><th>Match</th><th>WUA identity</th><th>Sources</th></tr></thead><tbody>{{rows}}</tbody></table><p class="meta">* Offered version is inferred from the catalog title because the WUA driver interface does not expose a standalone version string. Installed-driver matches use exact or family PnP identifiers and include a confidence score. Confirm both records in Intune and Device Manager.</p>
            <h2>Diagnostic findings</h2>{{findings}}
            <h2>Recent failed update history ({{historyFailures.Length}})</h2><table><thead><tr><th>Date</th><th>Update</th><th>Result</th><th>HRESULT</th><th>Client</th></tr></thead><tbody>{{historyRows}}</tbody></table>
            <p class="meta">Generated by WuPilot. Review is evidence, not an automatic approval decision.</p></body></html>
            """;

        static string H(string? value) => WebUtility.HtmlEncode(value ?? "—");
    }

    private static string BuildReadme(ScanReport report, IReadOnlyList<UpdateRecord> updates) => $$"""
        WuPilot evidence bundle
        =======================

        Device: {{report.Device.ComputerName}}
        Scan ID: {{report.ScanId}}
        Completed: {{report.CompletedAt:O}}
        Criteria: {{report.Criteria}}
        Included updates: {{updates.Count}}
        Included drivers: {{updates.Count(static update => update.IsDriver)}}
        Technician notes: {{report.TechnicianNotes ?? "None"}}

        Files
        -----
        scan-report.json    Complete machine-readable scan evidence.
        diagnostics.json    Local policy, service, connectivity, and reboot checks (when collected).
        update-history.csv  Up to 100 recent local WUA history events (when diagnostics were collected).
        windows-update-events.json  Recent Windows Update Client operational events (when available).
        wua-registered-services.json  WUA service registrations and source roles.
        bits-jobs.json       Current all-user BITS transfer state.
        cbs-errors.txt       Recent error lines from the live CBS log.
        setupapi-device-tail.log  Tail of the Plug and Play device-install log.
        setupdiag-results.xml  Existing Windows SetupDiag result, when present.
        driver-review.csv   Driver fields for comparison/filtering.
        intune-review.html  Human-readable handoff for an Intune administrator.

        Important
        ---------
        WUA UpdateID/revision values are retained for local traceability. They are not guaranteed to
        equal Microsoft Intune driver inventory IDs. Match the update in Intune by name, manufacturer,
        version or release date, driver class, and applicable devices. Test with a deployment ring before
        broad approval. This bundle contains device identifiers and should be handled as support data.
        """;
}
