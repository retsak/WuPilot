using System.Text.RegularExpressions;

namespace WuPilot.Core.Services;

public static partial class DriverVersionParser
{
    public static string? InferFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var matches = VersionPattern().Matches(title);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    [GeneratedRegex(@"(?<!\d)\d+(?:\.\d+){1,5}(?!\d)")]
    private static partial Regex VersionPattern();
}
