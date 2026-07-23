using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace WuPilot.Infrastructure.Windows.Wua;

internal static class WuaCom
{
    public static dynamic Create(string programmaticId)
    {
        var type = Type.GetTypeFromProgID(programmaticId, throwOnError: true)
            ?? throw new PlatformNotSupportedException($"The COM class '{programmaticId}' is not available.");
        return Activator.CreateInstance(type)
            ?? throw new COMException($"The COM class '{programmaticId}' could not be created.");
    }

    public static T? Try<T>(Func<object?> reader, T? fallback = default)
    {
        try
        {
            var value = reader();
            if (value is null) return fallback;
            if (value is T typed) return typed;
            return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or FormatException or OverflowException or RuntimeBinderException)
        {
            return fallback;
        }
    }

    public static DateTimeOffset? TryDate(Func<object?> reader)
    {
        var date = Try<DateTime?>(reader);
        if (date is null || date == DateTime.MinValue) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(date.Value, DateTimeKind.Local));
    }

    public static IReadOnlyList<string> ReadStringCollection(Func<dynamic> reader)
    {
        var values = new List<string>();
        try
        {
            dynamic collection = reader();
            var count = Convert.ToInt32(collection.Count);
            for (var index = 0; index < count; index++)
            {
                var value = Convert.ToString(collection.Item(index));
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
            }
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            // A newer WUA interface may not be available on an older OS build.
        }

        return values;
    }

    public static void FinalRelease(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
            // Already released.
        }
    }
}
