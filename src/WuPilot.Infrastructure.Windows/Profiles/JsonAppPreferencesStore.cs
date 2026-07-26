using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Profiles;

public sealed class JsonAppPreferencesStore(string? path = null) : IAppPreferencesStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "app-preferences.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Timer? _timer;
    private AppPreferences? _pending;

    public async Task<AppPreferences> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return AppPreferences.Default;
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var value = await JsonSerializer.DeserializeAsync<AppPreferences>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return Normalize(value);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                RecoverCorruptFile();
                return AppPreferences.Default;
            }
        }
        finally { _gate.Release(); }
    }

    public void ScheduleSave(AppPreferences preferences)
    {
        lock (_sync)
        {
            _pending = Normalize(preferences);
            _timer ??= new Timer(_ => _ = FlushAsync(CancellationToken.None), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(350, Timeout.Infinite);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        AppPreferences? value;
        lock (_sync)
        {
            value = _pending;
            _pending = null;
        }
        if (value is null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Preferences path has no parent.");
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _pending = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _gate.Release(); }
    }

    private static AppPreferences Normalize(AppPreferences? value)
    {
        if (value is null || value.SchemaVersion is < 1 or > 1) return AppPreferences.Default;
        var theme = value.Theme is "Light" or "Dark" ? value.Theme : "System";
        var navigation = value.NavigationTag is "scan" or "compare" or "controls" or "performance" or "watchlist" or "sources" or "diagnostics" or "history" or "activity" or "about"
            ? value.NavigationTag : "scan";
        var days = value.PerformanceRangeDays is 0 or 7 or 30 or 90 ? value.PerformanceRangeDays : 30;
        return value with
        {
            Theme = theme,
            NavigationTag = navigation,
            PerformanceRangeDays = days,
            ScanProviderIds = value.ScanProviderIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? AppPreferences.Default.ScanProviderIds,
            FavoritePolicyIds = value.FavoritePolicyIds?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []
        };
    }

    private void RecoverCorruptFile()
    {
        try
        {
            var corrupt = _path + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(_path, corrupt, true);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
