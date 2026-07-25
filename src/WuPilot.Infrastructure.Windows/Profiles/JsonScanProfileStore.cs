using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Profiles;

public sealed class JsonScanProfileStore(string? path = null) : IScanProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WuPilot",
        "scan-profiles.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<SavedScanProfile>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadCoreAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SavedScanProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            profiles.RemoveAll(existing =>
                existing.Id == profile.Id ||
                string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            profiles.Add(profile);
            await WriteCoreAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (profiles.RemoveAll(profile => profile.Id == profileId) > 0)
            {
                await WriteCoreAsync(profiles, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<SavedScanProfile>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<List<SavedScanProfile>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task WriteCoreAsync(IReadOnlyList<SavedScanProfile> profiles, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Scan profile path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
