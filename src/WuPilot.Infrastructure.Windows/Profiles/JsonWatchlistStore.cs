using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Profiles;

public sealed class JsonWatchlistStore(string? path = null) : IWatchlistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WuPilot",
        "watchlist.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<WatchedUpdate>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadCoreAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(static update => update.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WatchedUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updates = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            updates.RemoveAll(existing => string.Equals(existing.UpdateId, update.UpdateId, StringComparison.OrdinalIgnoreCase));
            updates.Add(update);
            await WriteCoreAsync(updates, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAllAsync(IEnumerable<WatchedUpdate> updates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(
                updates
                    .DistinctBy(static update => update.UpdateId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string updateId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updates = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (updates.RemoveAll(update => string.Equals(update.UpdateId, updateId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                await WriteCoreAsync(updates, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<WatchedUpdate>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<List<WatchedUpdate>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task WriteCoreAsync(IEnumerable<WatchedUpdate> updates, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Watchlist path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(updates, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
