using System.Globalization;
using System.Text.RegularExpressions;

namespace WuPilot.Core.Services;

public sealed record LogFinding(string Source, int Line, string Severity, string Text);
public sealed record LogActivity(string Source, string Phase, DateTime First, DateTime Last, int Entries)
{
    public TimeSpan ObservedSpan => Last - First;
}
public sealed record UpgradeLogAnalysis(IReadOnlyList<LogActivity> Activity, IReadOnlyList<LogFinding> Findings, int LinesRead, bool Truncated);

/// <summary>Observed log windows are evidence coverage, never inferred operation durations.</summary>
public static partial class UpgradeLogAnalyzer
{
    public static UpgradeLogAnalysis Analyze(string source, IEnumerable<string> lines, int maxLines = 500_000)
    {
        var activity = new List<LogActivity>();
        var findings = new List<LogFinding>();
        var count = 0;
        var truncated = false;
        string phase = "Unclassified setup activity";
        DateTime? first = null, last = null;
        var entries = 0;
        void Flush()
        {
            if (first.HasValue && last.HasValue) activity.Add(new(source, phase, first.Value, last.Value, entries));
            first = last = null;
            entries = 0;
        }
        foreach (var raw in lines)
        {
            if (count >= maxLines) { truncated = true; break; }
            count++;
            var line = raw.Length > 8192 ? raw[..8192] : raw;
            if (ErrorPattern().IsMatch(line) && findings.Count < 1000)
                findings.Add(new(source, count, "Review", line));
            var match = Timestamp().Match(line);
            if (!match.Success || !DateTime.TryParseExact(match.Value.Replace('T', ' '), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var marker = PhasePattern().Match(line);
            var nextPhase = marker.Success ? marker.Groups[1].Value.ToUpperInvariant() : phase;
            // Clock regressions, long gaps and phase transitions split windows rather than merge attempts.
            var newWindow = last.HasValue && (time < last.Value || time - last.Value > TimeSpan.FromHours(6));
            if (newWindow && !marker.Success) nextPhase = "Unclassified setup activity";
            if (last.HasValue && (newWindow || nextPhase != phase)) Flush();
            phase = nextPhase;
            first ??= time;
            last = time;
            entries++;
        }
        Flush();
        return new(activity, findings, count, truncated);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}")]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"\b(?:error|failed|failure|rollback)\b|\b0x[89a-f][0-9a-f]{7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorPattern();
    [GeneratedRegex(@"\b(?:phase\s*[:=]\s*|entering\s+(?:the\s+)?)(Downlevel|SafeOS|Safe OS|FirstBoot|First Boot|SecondBoot|Second Boot|OOBE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhasePattern();
}
