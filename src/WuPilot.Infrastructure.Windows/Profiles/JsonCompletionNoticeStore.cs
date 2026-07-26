using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Profiles;

public sealed class JsonCompletionNoticeStore(string? path = null) : ICompletionNoticeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "completion-notices.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<CompletionNotice>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CompletionNotice notice, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            items.RemoveAll(item => item.Id == notice.Id);
            items.Insert(0, notice);
            await WriteCoreAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DismissAsync(Guid noticeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            items.RemoveAll(item => item.Id == noticeId);
            await WriteCoreAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCoreAsync([], cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<List<CompletionNotice>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var values = await JsonSerializer.DeserializeAsync<List<CompletionNotice>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
            return values.Where(item => item.CompletedAt >= DateTimeOffset.Now.AddDays(-30))
                .OrderByDescending(item => item.CompletedAt).Take(50).ToList();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            try { File.Move(_path, _path + $".corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}", true); } catch (IOException) { }
            return [];
        }
    }

    private async Task WriteCoreAsync(IReadOnlyList<CompletionNotice> values, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Notice path has no parent.");
        Directory.CreateDirectory(directory);
        var retained = values.Where(item => item.CompletedAt >= DateTimeOffset.Now.AddDays(-30))
            .OrderByDescending(item => item.CompletedAt).Take(50).ToArray();
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(retained, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }
}
