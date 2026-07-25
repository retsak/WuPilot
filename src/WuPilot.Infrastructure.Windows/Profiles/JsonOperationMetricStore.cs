using System.Text;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Diagnostics;

namespace WuPilot.Infrastructure.Windows.Profiles;

public sealed class JsonOperationMetricStore(string? path = null) : IOperationMetricStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "operation-metrics.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<OperationMetric>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var rebootChanged = await CorrelateRebootsAsync(stored, cancellationToken).ConfigureAwait(false);
            if (rebootChanged) await WriteAsync(stored, cancellationToken).ConfigureAwait(false);
            var inferred = await ReadEstimatedWindowsHistoryAsync(cancellationToken).ConfigureAwait(false);
            return stored.Concat(inferred).OrderByDescending(static item => item.CompletedAt).Take(5000).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(OperationMetric metric, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
            all.RemoveAll(item => item.CompletedAt < DateTimeOffset.Now.AddDays(-365));
            all.Add(metric);
            all = all.OrderByDescending(static item => item.CompletedAt).Take(5000).OrderBy(static item => item.CompletedAt).ToList();
            await WriteAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<OperationMetric>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<OperationMetric>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            File.Move(_path, _path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true);
            return [];
        }
    }

    private async Task WriteAsync(List<OperationMetric> all, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, true);
    }

    private static async Task<bool> CorrelateRebootsAsync(List<OperationMetric> metrics, CancellationToken cancellationToken)
    {
        var pending = metrics.Where(static metric => metric.RebootRequired && metric.BootCompletedAt is null).ToArray();
        if (pending.Length == 0) return false;
        var start = pending.Min(static metric => metric.CompletedAt).AddMinutes(-5).ToString("O");
        var script = $"Get-WinEvent -FilterHashtable @{{LogName='System';Id=12,13;StartTime=[datetime]'{start}'}} -ErrorAction Stop | Sort-Object TimeCreated | ForEach-Object {{ [pscustomobject]@{{TimeCreated=$_.TimeCreated.ToString('o');Id=$_.Id;ProviderName=$_.ProviderName}} }} | ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return false;
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var events = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
            var values = events.Select(element => (Time: element.GetProperty("TimeCreated").GetDateTimeOffset(), Id: element.GetProperty("Id").GetInt32())).ToArray();
            var changed = false;
            for (var index = 0; index < metrics.Count; index++)
            {
                var metric = metrics[index];
                if (!metric.RebootRequired || metric.BootCompletedAt is not null) continue;
                var shutdown = values.FirstOrDefault(item => item.Id == 13 && item.Time >= metric.CompletedAt);
                if (shutdown == default) continue;
                var boot = values.FirstOrDefault(item => item.Id == 12 && item.Time > shutdown.Time);
                if (boot == default) continue;
                metrics[index] = metric with { RebootStartedAt = shutdown.Time, BootCompletedAt = boot.Time, RebootConfidence = EvidenceConfidence.Medium };
                changed = true;
            }
            return changed;
        }
        catch (Exception exception) when (exception is JsonException or FormatException) { return false; }
    }

    private static async Task<IReadOnlyList<OperationMetric>> ReadEstimatedWindowsHistoryAsync(CancellationToken cancellationToken)
    {
        const string script = """
            Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-WindowsUpdateClient/Operational';Id=19,20,34,43,44;StartTime=(Get-Date).AddDays(-90)} -MaxEvents 1000 -ErrorAction Stop |
              Sort-Object TimeCreated | ForEach-Object { [pscustomobject]@{TimeCreated=$_.TimeCreated.ToString('o');Id=$_.Id;Message=$_.Message} } | ConvertTo-Json -Compress -Depth 3
            """;
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return [];
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
            var events = elements.Select(element => new EventRecord(
                element.GetProperty("TimeCreated").GetDateTimeOffset(),
                element.GetProperty("Id").GetInt32(),
                element.TryGetProperty("Message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                Identity(element.TryGetProperty("Message", out var text) ? text.GetString() ?? string.Empty : string.Empty))).ToArray();
            var metrics = new List<OperationMetric>();
            foreach (var completion in events.Where(static item => item.Id is 19 or 20 or 34))
            {
                var startId = completion.Id == 34 ? 44 : 43;
                var start = events.LastOrDefault(item => item.Id == startId && item.Time < completion.Time &&
                    completion.Time - item.Time < TimeSpan.FromDays(2) && (item.Identity == completion.Identity || completion.Identity is null));
                if (start is null) continue;
                var operation = completion.Id == 34 ? "Windows download (estimated)" : "Windows install (estimated)";
                var duration = completion.Time - start.Time;
                metrics.Add(new OperationMetric(StableId($"{operation}|{completion.Time:O}|{completion.Identity}"), start.Time, completion.Time,
                    operation, completion.Identity, null, FirstLine(completion.Message), null, TimeSpan.Zero,
                    completion.Id == 34 ? duration : TimeSpan.Zero, completion.Id == 34 ? TimeSpan.Zero : duration, duration,
                    completion.Id == 20 ? 4 : 2, 0, false, TimingConfidence: EvidenceConfidence.Low,
                    EvidenceSource: "WindowsUpdateClient/Operational event correlation"));
            }
            return metrics;
        }
        catch (Exception exception) when (exception is JsonException or FormatException) { return []; }
    }

    private static string? Identity(string message)
    {
        var guid = System.Text.RegularExpressions.Regex.Match(message, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (guid.Success) return guid.Value.ToUpperInvariant();
        var kb = System.Text.RegularExpressions.Regex.Match(message, @"KB\d{6,8}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return kb.Success ? kb.Value.ToUpperInvariant() : null;
    }
    private static string FirstLine(string value) => value.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Windows Update event";
    private static Guid StableId(string value)
    {
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
    private sealed record EventRecord(DateTimeOffset Time, int Id, string Message, string? Identity);
}
