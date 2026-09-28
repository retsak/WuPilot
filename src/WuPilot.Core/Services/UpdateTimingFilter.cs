using System.Text.RegularExpressions;

namespace WuPilot.Core.Services;

public static class UpdateTimingFilter
{
    public static bool Matches(string? selection, string? updateId, string? title)
    {
        if (string.IsNullOrWhiteSpace(selection)) return true;
        var value = selection.Trim();
        if (Guid.TryParse(value, out var id))
            return Guid.TryParse(updateId, out var candidate) && candidate == id;
        if (Regex.IsMatch(value, @"^(KB)?[0-9]+$", RegexOptions.IgnoreCase))
        {
            var kb = value.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? value : "KB" + value;
            return Regex.IsMatch(title ?? "", @"\b" + Regex.Escape(kb) + @"\b", RegexOptions.IgnoreCase);
        }
        return false;
    }
}
