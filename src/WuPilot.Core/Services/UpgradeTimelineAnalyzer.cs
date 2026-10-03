using System.Globalization;
using System.Text.RegularExpressions;

namespace WuPilot.Core.Services;

public sealed record UpgradeBoundary(DateTimeOffset Time, string Label, string Source);
public sealed record UpgradePhase(string Name, UpgradeBoundary? Start, UpgradeBoundary? End, string Confidence, string Explanation)
{
    public TimeSpan? Duration => Start is not null && End is not null && End.Time >= Start.Time ? End.Time - Start.Time : null;
}
public sealed record UpgradeTimeline(string UpdateId, string Title, DateTimeOffset AttemptedAt, UpgradePhase Download,
    UpgradePhase Install, UpgradePhase Reboot, TimeSpan? WaitingForRestart, IReadOnlyList<UpgradeBoundary> Milestones, string Status);
public sealed record UpgradeSystemEvent(DateTimeOffset Time, string Kind, string? Package, string Source,
    string? UpdateId = null, string? Title = null);

/// <summary>Correlates observed UpdateAgent lifecycle markers with package-specific completion, never kernel boot alone.</summary>
public static partial class UpgradeTimelineAnalyzer
{
    public static IReadOnlyList<UpgradeTimeline> Analyze(string source, IEnumerable<string> lines,
        IReadOnlyList<UpgradeSystemEvent> events, TimeZoneInfo sourceZone, int maxLines = int.MaxValue)
    {
        var attempts = new List<Attempt>();
        var currentById = new Dictionary<string, Attempt>(StringComparer.OrdinalIgnoreCase);
        Attempt? current = null;
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (lineNumber > maxLines) break;
            if (line.Length < 19 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            // Wall-clock ambiguity must not silently become an exact correlation across a DST transition.
            if (sourceZone.IsInvalidTime(local) || sourceZone.IsAmbiguousTime(local)) { current = null; continue; }
            var time = new DateTimeOffset(local, sourceZone.GetUtcOffset(local));
            UpgradeBoundary Boundary(string label) => new(time, label, $"{source}:{lineNumber}");
            if (line.Contains("UpdateAgent logging starts.", StringComparison.Ordinal)) current = null;
            var id = Identity().Match(line);
            if (id.Success)
            {
                var key = id.Groups[1].Value.ToUpperInvariant();
                if (!currentById.TryGetValue(key, out current))
                {
                    current = new(key, time);
                    currentById[key] = current;
                    attempts.Add(current);
                }
            }
            if (current is null) continue;
            var startDownload = line.TrimEnd().EndsWith(" GenerateDownloadRequest: Enter", StringComparison.Ordinal);
            var startInstall = line.TrimEnd().EndsWith(" Install: Enter", StringComparison.Ordinal);
            if ((startDownload || startInstall) && (time < current.AttemptedAt || time - current.AttemptedAt > TimeSpan.FromDays(2) || current.Pending is not null))
            {
                current = new(current.Id, time);
                currentById[current.Id] = current;
                attempts.Add(current);
            }
            if (startDownload) current.DownloadStart ??= Boundary("Download preparation started");
            var kb = Package().Match(line);
            if (kb.Success) current.Package = kb.Groups[1].Value.ToUpperInvariant();
            if (line.Contains("ReportEventDownloadRequestEnd:", StringComparison.Ordinal) && DownloadComplete().IsMatch(line))
                current.DownloadEnd ??= Boundary("Download complete");
            if (startInstall) current.InstallStart ??= Boundary("Installation started");
            // Deserialize RebootRequired is stale persisted state, not a new transition.
            if (current.InstallStart is not null && line.TrimEnd().EndsWith(" Reboot required: [TRUE]", StringComparison.Ordinal))
                current.Pending ??= Boundary("Installation ready — restart required");
        }

        var active = attempts.Where(a => a.DownloadStart is not null || a.InstallStart is not null).OrderBy(a => a.AttemptedAt).ToArray();
        return active.Select(a => Build(a, active, events)).OrderByDescending(a => a.AttemptedAt).ToArray();
    }

    private static UpgradeTimeline Build(Attempt a, Attempt[] attempts, IReadOnlyList<UpgradeSystemEvent> events)
    {
        var identityEvent = events.Where(e => string.Equals(e.UpdateId, a.Id, StringComparison.OrdinalIgnoreCase)
            && e.Time >= a.AttemptedAt && e.Time < a.AttemptedAt.AddDays(30) && !string.IsNullOrWhiteSpace(e.Title))
            .OrderBy(e => e.Time).FirstOrDefault();
        var download = new UpgradePhase("Download", a.DownloadStart, a.DownloadEnd, "Log estimate",
            "First download request to DownloadComplete. Includes preparation, retry and transfer time; not network-only throughput.");
        var install = new UpgradePhase("Install → pending reboot", a.InstallStart, a.Pending, "Log boundaries",
            "Installation starts here and ends when UpdateAgent first reports that a restart is required.");
        var milestones = new List<UpgradeBoundary>();
        foreach (var boundary in new[] { a.DownloadStart, a.DownloadEnd, a.InstallStart, a.Pending }) if (boundary is not null) milestones.Add(boundary);
        UpgradeBoundary? rebootStart = null, complete = null;
        var rebootNote = "Restart and matching update completion boundaries were not found. Kernel boot alone is not update completion.";
        if (a.Pending is { } pending)
        {
            var nextAttempt = attempts.Where(other => other.AttemptedAt > pending.Time).Select(other => other.AttemptedAt).DefaultIfEmpty(pending.Time.AddDays(30)).Min();
            var candidates = events.Where(e => e.Time >= pending.Time && e.Time < nextAttempt && e.Time < pending.Time.AddDays(30)).OrderBy(e => e.Time).ToArray();
            // A normal power-off must not be interpreted as an update restart lasting days.
            var firstPowerTransition = candidates.FirstOrDefault(e => e.Kind is "RestartRequested" or "PowerOffRequested" or "Shutdown");
            if (firstPowerTransition?.Kind is "RestartRequested" or "Shutdown")
            {
                var shutdown = candidates.FirstOrDefault(e => e.Kind == "Shutdown" && e.Time >= firstPowerTransition.Time && e.Time - firstPowerTransition.Time < TimeSpan.FromMinutes(10));
                var boot = shutdown is null ? null : candidates.FirstOrDefault(e => e.Kind == "Boot" && e.Time > shutdown.Time && e.Time - shutdown.Time < TimeSpan.FromHours(24));
                if (shutdown is not null)
                {
                    rebootStart = new(firstPowerTransition.Time, firstPowerTransition.Kind == "RestartRequested" ? "Restart initiated" : "Shutdown started (restart request missing)", firstPowerTransition.Source);
                    milestones.Add(rebootStart);
                    if (boot is not null)
                    {
                        var powerOff = candidates.FirstOrDefault(e => e.Kind == "PowerOffRequested" && e.Time > shutdown.Time)?.Time ?? shutdown.Time.AddHours(24);
                        // Prefer update-identity success over a constituent package's completion.
                        var success = candidates.FirstOrDefault(e => e.Kind == "UpdateInstalled"
                            && string.Equals(e.UpdateId, a.Id, StringComparison.OrdinalIgnoreCase)
                            && e.Time >= boot.Time && e.Time < powerOff && e.Time - rebootStart.Time < TimeSpan.FromHours(24));
                        // A known feature-update identity must not finish on a cumulative package event.
                        if (success is null && identityEvent is null && a.Package is not null)
                            success = candidates.FirstOrDefault(e => e.Kind == "PackageInstalled" && string.Equals(e.Package, a.Package, StringComparison.OrdinalIgnoreCase) && e.Time >= boot.Time && e.Time < powerOff && e.Time - rebootStart.Time < TimeSpan.FromHours(24));
                        if (success is not null)
                        {
                            complete = new(success.Time, $"{success.Title ?? a.Package ?? "Windows update"} installation finished", success.Source);
                            foreach (var step in candidates.Where(e => e.Time > rebootStart.Time && e.Time < success.Time && e.Kind is "Boot" or "RestartRequested"))
                                milestones.Add(new(step.Time, step.Kind == "Boot" ? "Windows boot (intermediate milestone)" : "Additional update restart", step.Source));
                            milestones.Add(complete);
                            rebootNote = success.Kind == "UpdateInstalled"
                                ? "Restart initiation to Windows Update's successful installation event for the same update ID, including intermediate boots. This confirms reported update completion, not desktop readiness. Restart association is chronological."
                                : "Restart initiation to the matching package's Installed event, including intermediate boots. This confirms package servicing completion, not desktop readiness. Restart association is chronological.";
                        }
                    }
                }
            }
        }
        var reboot = new UpgradePhase("Reboot → update finished", rebootStart, complete, "Correlated estimate", rebootNote);
        return new(a.Id, identityEvent?.Title ?? (a.Package is null ? "Windows OS update" : $"Windows cumulative update · {a.Package}"), a.DownloadStart?.Time ?? a.InstallStart!.Time,
            download, install, reboot, rebootStart is not null && a.Pending is not null ? rebootStart.Time - a.Pending.Time : null,
            milestones.OrderBy(m => m.Time).ToArray(), complete is not null ? "Update completed" : a.Pending is not null ? "Restart required; completion unconfirmed" : "Partial evidence");
    }

    private sealed class Attempt(string id, DateTimeOffset time)
    {
        public string Id { get; } = id;
        public DateTimeOffset AttemptedAt { get; } = time;
        public string? Package { get; set; }
        public UpgradeBoundary? DownloadStart { get; set; }
        public UpgradeBoundary? DownloadEnd { get; set; }
        public UpgradeBoundary? InstallStart { get; set; }
        public UpgradeBoundary? Pending { get; set; }
    }
    [GeneratedRegex(@"Initializing UpdateId = \[\{?([0-9a-fA-F-]{36})")]
    private static partial Regex Identity();
    [GeneratedRegex(@"Installing feature:.*Feature: CumulativeUpdate_(KB\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Package();
    [GeneratedRegex(@"DownloadComplete\s*=\s*\[TRUE\]")]
    private static partial Regex DownloadComplete();
}
