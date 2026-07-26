using WuPilot.Core.Models;
using WuPilot.Core.Abstractions;

namespace WuPilot.Core.Services;

public static class WindowPlacementValidator
{
    public static WindowPlacement Clamp(WindowPlacement? placement, int left, int top, int width, int height)
    {
        var value = placement ?? new WindowPlacement();
        var safeWidth = Math.Clamp(value.Width, 900, Math.Max(900, width));
        var safeHeight = Math.Clamp(value.Height, 640, Math.Max(640, height));
        var safeX = Math.Clamp(value.X, left, left + Math.Max(0, width - safeWidth));
        var safeY = Math.Clamp(value.Y, top, top + Math.Max(0, height - safeHeight));
        return value with { X = safeX, Y = safeY, Width = safeWidth, Height = safeHeight };
    }
}

public static class PolicyValueValidator
{
    public static string? Normalize(PolicyDefinition definition, string? value, bool remove)
    {
        if (remove) return null;
        var trimmed = value?.Trim() ?? string.Empty;
        return definition.ValueKind switch
        {
            PolicyValueKind.Boolean when trimmed is "0" or "1" => trimmed,
            PolicyValueKind.Boolean => throw new ArgumentException("Choose On or Off."),
            PolicyValueKind.Integer when int.TryParse(trimmed, out var number) &&
                                             (definition.Minimum is null || number >= definition.Minimum) &&
                                             (definition.Maximum is null || number <= definition.Maximum) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PolicyValueKind.Integer => throw new ArgumentException($"Enter a whole number from {definition.Minimum ?? int.MinValue} to {definition.Maximum ?? int.MaxValue}."),
            PolicyValueKind.Choice when definition.Choices?.ContainsKey(trimmed) == true => trimmed,
            PolicyValueKind.Choice => throw new ArgumentException("Choose one of the documented values."),
            PolicyValueKind.DateTime when DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var date) =>
                date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            PolicyValueKind.DateTime => throw new ArgumentException("Choose a valid date."),
            _ when trimmed.Length <= 2_048 => trimmed,
            _ => throw new ArgumentException("The value is longer than 2,048 characters.")
        };
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
