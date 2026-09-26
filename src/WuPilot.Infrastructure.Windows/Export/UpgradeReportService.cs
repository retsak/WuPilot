using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Infrastructure.Windows.Export;

public static class UpgradeReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<string> RegenerateAsync(string bundle, string destination, string? timeZoneId, CancellationToken token) => Task.Run(async () =>
    {
        var analyses = await ReadAsync<UpgradeLogAnalysis[]>(Path.Combine(bundle, "analysis.json"), token) ?? [];
        var metrics = await ReadAsync<OperationMetric[]>(Path.Combine(bundle, "operation-metrics.json"), token) ?? [];
        var manifest = await ReadAsync<CollectedLog[]>(Path.Combine(bundle, "manifest.json"), token) ?? [];
        if (timeZoneId is null && File.Exists(Path.Combine(bundle, "phase-summary.json")))
        {
            using var context = await ReadAsync<JsonDocument>(Path.Combine(bundle, "phase-summary.json"), token);
            if (context?.RootElement.TryGetProperty("timeZone", out var savedZone) == true) timeZoneId = savedZone.GetString();
        }
        Directory.CreateDirectory(destination);
        return await WriteAsync(bundle, destination, analyses, metrics, manifest, timeZoneId, token).ConfigureAwait(false);
    }, token);

    internal static async Task<string> WriteAsync(string bundle, string destination, IReadOnlyList<UpgradeLogAnalysis> analyses,
        IReadOnlyList<OperationMetric> metrics, IReadOnlyCollection<CollectedLog> manifest, string? timeZoneId, CancellationToken token)
    {
        var zone = timeZoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var warnings = new List<string>();
        var events = new List<UpgradeSystemEvent>();
        foreach (var file in manifest.Where(m => m.Status == "Collected" && m.File?.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase) == true))
        {
            try { events.AddRange(UpgradeEventReader.Read(ResolveSource(bundle, file.File!), file.File!, token)); }
            catch (Exception ex) when (ex is EventLogException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { warnings.Add($"Could not read {file.File}: {ex.Message}"); }
        }
        var timelines = new List<UpgradeTimeline>();
        foreach (var file in manifest.Where(m => m.Status == "Collected" && m.File?.EndsWith("UpdateAgent.log", StringComparison.OrdinalIgnoreCase) == true))
        {
            token.ThrowIfCancellationRequested();
            timelines.AddRange(UpgradeTimelineAnalyzer.Analyze(file.File!, ReadLines(ResolveSource(bundle, file.File!), token), events, zone));
        }
        var ordered = timelines.DistinctBy(t => (t.UpdateId, t.AttemptedAt)).OrderByDescending(t => t.AttemptedAt).ToArray();
        await File.WriteAllTextAsync(Path.Combine(destination, "phase-summary.json"), JsonSerializer.Serialize(new { timeZone = zone.Id, warnings, updates = ordered }, JsonOptions), token).ConfigureAwait(false);
        var report = Path.Combine(destination, "report.html");
        await File.WriteAllTextAsync(report, BuildHtml(ordered, analyses, metrics, manifest, zone, warnings), token).ConfigureAwait(false);
        return report;
    }

    internal static string ResolveSource(string bundle, string relative)
    {
        var root = Path.GetFullPath(bundle).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest source escapes the bundle folder.");
        return path;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token).ConfigureAwait(false);
    }
    private static IEnumerable<string> ReadLines(string path, CancellationToken token)
    {
        foreach (var line in File.ReadLines(path)) { token.ThrowIfCancellationRequested(); yield return line; }
    }
    private static string H(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "Not recorded");
    internal static string Duration(TimeSpan? value)
    {
        if (value is null || value < TimeSpan.Zero) return "Not recorded";
        var seconds = (long)Math.Round(value.Value.TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds < 1) return "< 1s";
        return seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m {seconds % 60}s" : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s";
    }

    internal static string BuildHtml(IReadOnlyList<UpgradeTimeline> timelines, IReadOnlyList<UpgradeLogAnalysis> analyses,
        IReadOnlyList<OperationMetric> metrics, IReadOnlyCollection<CollectedLog> manifest, TimeZoneInfo zone, IReadOnlyList<string> warnings)
    {
        string Stamp(DateTimeOffset? time) => time is null ? "Not recorded" : TimeZoneInfo.ConvertTime(time.Value, zone).ToString("MMM d, yyyy · HH:mm:ss", CultureInfo.InvariantCulture);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>WuPilot · Update timeline</title><style>
            :root{color-scheme:light;--ink:#182b40;--muted:#526477;--line:#dce4ec}*{box-sizing:border-box}
            body{margin:0;background:#f3f6fa;color:var(--ink);font:15px/1.55 "Segoe UI",Arial,sans-serif}main{max-width:1180px;margin:auto;padding:40px 28px 64px}
            .brand{font-size:12px;font-weight:700;letter-spacing:2px;color:#315f91}h1{font-size:34px;line-height:1.18;letter-spacing:-1px;margin:10px 0}h2{font-size:23px;margin:0}h3{font-size:17px;margin:0}
            p{margin:8px 0}.muted,small{color:var(--muted)}.attempt{margin-top:28px;background:white;border:1px solid var(--line);border-radius:16px;padding:26px;box-shadow:0 4px 18px #18304e06}
            .heading{display:flex;gap:16px;justify-content:space-between;align-items:flex-start}.badge{display:inline-block;border-radius:20px;background:#e9f5ef;color:#236145;padding:5px 12px;font-size:12px;font-weight:700;white-space:nowrap}.unknown{background:#fff3d9;color:#745414}
            .phases{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px;margin:24px 0 16px}.phase{border:1px solid var(--line);border-top:4px solid #3d79b4;border-radius:10px;padding:20px;background:#fbfdff}.phase:nth-child(2){border-top-color:#268079}.phase:nth-child(3){border-top-color:#7665ae}
            .phase-label{font-size:13px;font-weight:700;min-height:24px}.duration{font-size:36px;font-weight:650;letter-spacing:-1px;margin:12px 0 6px;line-height:1.15}.confidence{font-size:12px;color:var(--muted)}.bounds{margin-top:18px;font-size:13px}.bounds span{display:block}.definition{font-size:12px;margin-top:12px;color:var(--muted)}
            .wait{padding:14px 18px;background:#f5f7fa;border:1px dashed #c8d3df;border-radius:9px}.elapsed{margin-top:12px;color:var(--muted);font-size:13px}.notice{padding:16px 20px;border-left:4px solid #dda943;background:#fff8e9;margin:20px 0}
            details{border:1px solid var(--line);border-radius:9px;background:#fff;margin-top:14px;padding:0 18px}summary{cursor:pointer;font-weight:600;padding:15px 0}details[open]{padding-bottom:16px}.attempt details{background:#fbfcfe}details details{background:#fafbfd}table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;vertical-align:top;padding:12px 10px;border-bottom:1px solid var(--line);overflow-wrap:anywhere}th{color:var(--muted);font-size:12px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:12px/1.5 Consolas,monospace;background:#f5f7fa;padding:12px;border-radius:6px}.scroll{overflow:auto}footer{font-size:12px;color:var(--muted);margin-top:28px}.technical{margin-top:32px}
            @media(max-width:760px){main{padding:24px 16px}h1{font-size:28px}.attempt{padding:18px}.phases{grid-template-columns:1fr}.heading{display:block}.badge{margin-top:10px}.duration{font-size:32px}}
            @media print{body{background:white}main{max-width:none;padding:0}.attempt{break-inside:avoid;box-shadow:none}.technical{display:none}details{display:none}.phases{grid-template-columns:repeat(3,1fr)}}
            </style></head><body><main><div class="brand">WUPILOT / UPDATE TIMELINE</div><h1>Windows update, in three phases</h1>
            <p class="muted">Download. Install until a restart is required. Restart until the update finishes.</p>
            """);
        if (timelines.Count == 0)
            html.Append("<div class='notice'><strong>No complete OS update timeline was identified.</strong><p>The available logs do not contain recognizable UpdateAgent phase boundaries. Retained WuPilot call timings are available below; they are not a substitute for a full update/restart timeline.</p></div>");
        for (var i = 0; i < timelines.Count; i++)
        {
            var timeline = timelines[i];
            if (i > 0) html.Append("<details><summary>Earlier update attempt · " + H(Stamp(timeline.AttemptedAt)) + "</summary>");
            html.Append($"<section class='attempt'><div class='heading'><div><h2>{H(timeline.Title)}</h2><p class='muted'>{H(Stamp(timeline.AttemptedAt))}{(i == 0 ? " · Latest recognized attempt" : "")}</p></div><span class='badge{(timeline.Reboot.End is null ? " unknown" : "")}'>{H(timeline.Status)}</span></div><div class='phases'>");
            var phases = new[] { timeline.Download, timeline.Install, timeline.Reboot };
            for (var j = 0; j < phases.Length; j++)
            {
                var phase = phases[j];
                html.Append($"<article class='phase'><div class='phase-label'>{j + 1:00} &nbsp; {H(phase.Name)}</div><div class='duration'>{H(Duration(phase.Duration))}</div><div class='confidence'>{H(phase.Duration is null ? "Missing phase boundary" : phase.Confidence)}</div><div class='bounds'><span><strong>Start</strong> &nbsp; {H(Stamp(phase.Start?.Time))}</span><span><strong>Finish</strong> &nbsp; {H(Stamp(phase.End?.Time))}</span></div><p class='definition'>{H(phase.Explanation)}</p></article>");
            }
            html.Append($"</div><div class='wait'><strong>Waiting for restart: {H(Duration(timeline.WaitingForRestart))}</strong><br><small>Pending reboot → restart initiation. This waiting time is excluded from both install and reboot durations.</small></div>");
            if (timeline.Download.Start is { } start && timeline.Reboot.End is { } end)
                html.Append($"<p class='elapsed'>Elapsed from download preparation to update completion: <strong>{H(Duration(end.Time - start.Time))}</strong> · Includes waiting and gaps between phases.</p>");
            html.Append($"<details><summary>View phase boundaries and restart milestones ({timeline.Milestones.Count})</summary><p class='muted'>Update ID: {H(timeline.UpdateId)}</p><div class='scroll'><table><tr><th>Time</th><th>Milestone</th><th>Evidence</th></tr>");
            foreach (var m in timeline.Milestones) html.Append($"<tr><td>{H(Stamp(m.Time))}</td><td>{H(m.Label)}</td><td>{H(m.Source)}</td></tr>");
            html.Append("</table></div></details></section>");
            if (i > 0) html.Append("</details>");
        }
        html.Append($"<p class='elapsed'>Time zone: {H(zone.Id)}. Text-log timestamps are interpreted in this zone; event timestamps are converted from UTC. Durations are rounded to the nearest second. The selected update is not mixed with other dates or Defender operations.</p>");
        foreach (var warning in warnings) html.Append($"<p class='notice'>{H(warning)}</p>");
        html.Append("<section class='technical'><h2>Supporting evidence</h2><p class='muted'>Expand a section when you need the raw detail. Keyword matches alone do not indicate that the update failed.</p>");
        html.Append($"<details><summary>Other recorded operations ({metrics.Count})</summary><p>These are individual WUA call timings, not full OS upgrade durations. A shutdown-to-kernel-boot interval is not update completion.</p><div class='scroll'><table><tr><th>Operation / update</th><th>When</th><th>Download</th><th>Install call</th><th>Evidence</th></tr>");
        foreach (var m in metrics) html.Append($"<tr><td>{H(m.Title)}<br>{H(m.Operation)}</td><td>{H(Stamp(m.StartedAt))}</td><td>{H(Duration(m.DownloadDuration == TimeSpan.Zero ? null : m.DownloadDuration))}</td><td>{H(Duration(m.InstallDuration == TimeSpan.Zero ? null : m.InstallDuration))}</td><td>{H(m.TimingConfidence)} · {H(m.EvidenceSource)}</td></tr>");
        html.Append($"</table></div></details><details><summary>Log findings ({analyses.Sum(a => a.Findings.Count)} keyword matches)</summary><p>Raw matches can include expected or recovered errors. Review source context before drawing conclusions.</p>");
        foreach (var analysis in analyses.Where(a => a.Findings.Count > 0 || a.Truncated))
        {
            html.Append($"<details><summary>{H(analysis.Findings.FirstOrDefault()?.Source ?? analysis.Activity.FirstOrDefault()?.Source)} · {analysis.Findings.Count} matches</summary>");
            if (analysis.Truncated) html.Append("<p>Parsing limit reached; consult the original log.</p>");
            foreach (var finding in analysis.Findings) html.Append($"<small>Line {finding.Line}</small><pre>{H(finding.Text)}</pre>");
            html.Append("</details>");
        }
        html.Append("</details><details><summary>Raw log activity windows</summary><p>First-to-last observed entries, not phase durations. Older setup logs are intentionally kept out of the current update timeline.</p><div class='scroll'><table><tr><th>Source / phase</th><th>First</th><th>Last</th><th>Observed span</th></tr>");
        foreach (var a in analyses.SelectMany(a => a.Activity)) html.Append($"<tr><td>{H(a.Source)}<br>{H(a.Phase)}</td><td>{H(a.First)}</td><td>{H(a.Last)}</td><td>{H(Duration(a.ObservedSpan))}</td></tr>");
        html.Append($"</table></div></details><details><summary>Collection coverage · {manifest.Count(m => m.Status == "Collected")} collected / {manifest.Count(m => m.Status != "Collected")} missing or limited</summary><div class='scroll'><table><tr><th>Source</th><th>Status</th><th>Details</th></tr>");
        foreach (var m in manifest.OrderBy(m => m.Source)) html.Append($"<tr><td>{H(m.Source)}</td><td>{H(m.Status)}</td><td>{H(m.Detail)}</td></tr>");
        return html.Append("</table></div></details></section><footer>Generated by WuPilot from collected evidence. Reboot completion requires matching update evidence after startup; reaching kernel boot alone never completes the phase. For a feature upgrade with multiple packages, a single package completion is not proof the entire upgrade finished. Source logs may contain identifiers and user paths.</footer></main></body></html>").ToString();
    }
}
