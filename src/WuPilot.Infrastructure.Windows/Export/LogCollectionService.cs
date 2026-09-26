using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WuPilot.Core.Models;
using WuPilot.Core.Services;
using WuPilot.Infrastructure.Windows.Diagnostics;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Windows.Export;

public sealed record LogCollectionOptions(string Destination, string? InputDirectory = null, int Parallelism = 4, long MaxFileBytes = 64 * 1024 * 1024, int MaxFiles = 300, string? SourceTimeZoneId = null);
public sealed record CollectedLog(string Source, string? File, string Status, long Bytes, double Seconds, string? Sha256, string? Detail);
public sealed record LogCollectionResult(string LocalZip, string? DeliveredZip, string ReportPath, string? DeliveryError, int Collected, int Unavailable);

public sealed class LogCollectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    public LogCollectionService(string? localRoot = null) => _root = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "LogBundles");

    public Task<LogCollectionResult> CollectAsync(LogCollectionOptions options, IProgress<string>? progress, CancellationToken token) =>
        Task.Run(() => CollectCoreAsync(options, progress, token), token);

    private async Task<LogCollectionResult> CollectCoreAsync(LogCollectionOptions options, IProgress<string>? progress, CancellationToken token)
    {
        if (options.Parallelism is < 1 or > 8 || options.MaxFileBytes < 1 || options.MaxFiles is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Destination) || !Path.IsPathFullyQualified(options.Destination)) throw new ArgumentException("Use an absolute local folder or UNC share for the destination.");
        if (options.InputDirectory is not null && !Directory.Exists(options.InputDirectory)) throw new DirectoryNotFoundException(options.InputDirectory);
        var id = $"{Environment.MachineName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var work = Path.Combine(_root, id);
        var raw = Path.Combine(work, "logs");
        Directory.CreateDirectory(raw);
        var manifest = new ConcurrentBag<CollectedLog>();
        var inputs = new List<string>();
        var roots = options.InputDirectory is not null ? new[] { Path.GetFullPath(options.InputDirectory) } : DefaultRoots();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root)) { inputs.Add(root); continue; }
            if (!Directory.Exists(root)) { manifest.Add(new(root, null, "Missing", 0, 0, null, "Not present on this device.")); continue; }
            try
            {
                var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, MaxRecursionDepth = 5 });
                foreach (var file in files)
                {
                    if (!new[] { ".log", ".xml", ".json", ".etl", ".evtx", ".txt" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    if (inputs.Count >= options.MaxFiles) { manifest.Add(new(root, null, "Limit", 0, 0, null, "File count limit reached; collection is partial.")); break; }
                    inputs.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { manifest.Add(new(root, null, "Unavailable", 0, 0, null, ex.Message)); }
        }
        await Parallel.ForEachAsync(inputs.Distinct(StringComparer.OrdinalIgnoreCase).Take(options.MaxFiles).Select((path, index) => (path, index)), new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = token }, async (item, ct) =>
        {
            var timer = Stopwatch.StartNew();
            var name = $"{item.index:D4}-{Path.GetFileName(item.path)}";
            var target = Path.Combine(raw, name);
            try
            {
                progress?.Report($"Collecting {Path.GetFileName(item.path)}");
                await using (var input = new FileStream(item.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true))
                {
                    if (input.Length > options.MaxFileBytes) { manifest.Add(new(item.path, null, "Limit", input.Length, timer.Elapsed.TotalSeconds, null, "File exceeds size limit.")); return; }
                    // Snapshot the initial length so growing servicing logs cannot create an unbounded copy.
                    var remaining = input.Length;
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    var buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                        if (read == 0) throw new IOException("Source was truncated during collection.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        remaining -= read;
                    }
                }
                await using var hashInput = File.OpenRead(target);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct).ConfigureAwait(false));
                manifest.Add(new(item.path, "logs/" + name, "Collected", hashInput.Length, timer.Elapsed.TotalSeconds, hash, "Live file snapshot; not a transactional snapshot."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Delete(target);
                manifest.Add(new(item.path, null, "Unavailable", 0, timer.Elapsed.TotalSeconds, null, ex.Message));
            }
        }).ConfigureAwait(false);

        IReadOnlyList<OperationMetric> metrics = [];
        if (options.InputDirectory is null)
        {
            progress?.Report("Reading operation timings and exporting event logs");
            var metricTask = ReadMetricsAsync(manifest, token);
            await Parallel.ForEachAsync(new[] { "System", "Setup", "Microsoft-Windows-WindowsUpdateClient/Operational" }, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = token }, async (channel, ct) =>
            {
                var name = channel.Replace('/', '-') + ".evtx";
                var timer = Stopwatch.StartNew();
                try
                {
                    var result = await ProcessRunner.RunAsync("wevtutil.exe", ["epl", channel, Path.Combine(raw, name), "/q:*[System[TimeCreated[timediff(@SystemTime) <= 2592000000]]]"], TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                    long bytes = 0;
                    string? hash = null;
                    if (result.ExitCode == 0)
                    {
                        await using var stream = File.OpenRead(Path.Combine(raw, name));
                        bytes = stream.Length;
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                    }
                    else File.Delete(Path.Combine(raw, name));
                    manifest.Add(new(channel, result.ExitCode == 0 ? "logs/" + name : null, result.ExitCode == 0 ? "Collected" : "Unavailable", bytes, timer.Elapsed.TotalSeconds, hash, result.Error));
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { manifest.Add(new(channel, null, "Unavailable", 0, timer.Elapsed.TotalSeconds, null, ex.Message)); }
            }).ConfigureAwait(false);
            metrics = await metricTask.ConfigureAwait(false);
        }
        else
        {
            var metricFile = inputs.FirstOrDefault(p => Path.GetFileName(p).Equals("operation-metrics.json", StringComparison.OrdinalIgnoreCase));
            var snapshot = manifest.FirstOrDefault(m => m.Source == metricFile && m.Status == "Collected");
            if (snapshot?.File is not null)
            {
                try { metrics = JsonSerializer.Deserialize<List<OperationMetric>>(await File.ReadAllTextAsync(Path.Combine(work, snapshot.File), token), JsonOptions) ?? []; }
                catch (JsonException ex) { manifest.Add(new(metricFile!, null, "Parse error", 0, 0, null, ex.Message)); }
            }
        }
        progress?.Report("Analyzing logs and writing reports");
        var analyses = new List<UpgradeLogAnalysis>();
        foreach (var file in manifest.Where(m => m.Status == "Collected" && m.File is not null && Path.GetExtension(m.File).Equals(".log", StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.File))
        {
            token.ThrowIfCancellationRequested();
            analyses.Add(UpgradeLogAnalyzer.Analyze(file.Source, ReadLines(Path.Combine(work, file.File!), token)));
        }
        await WriteJson(Path.Combine(work, "manifest.json"), manifest.OrderBy(m => m.Source).ToArray(), token);
        await WriteJson(Path.Combine(work, "analysis.json"), analyses, token);
        await WriteJson(Path.Combine(work, "operation-metrics.json"), metrics, token);
        var report = await UpgradeReportService.WriteAsync(work, work, analyses, metrics, manifest.ToArray(), options.SourceTimeZoneId, token).ConfigureAwait(false);
        var csv = new StringBuilder("Operation,Title,StartedAt,DownloadSeconds,InstallSeconds,TotalSeconds,ShutdownToBootSeconds,Confidence,ResultCode,HResult\r\n");
        foreach (var m in metrics) csv.AppendLine(string.Join(',', new[] { m.Operation, m.Title, m.StartedAt.ToString("O"), StageSeconds(m.DownloadDuration), StageSeconds(m.InstallDuration), Seconds(m.TotalDuration), RebootSeconds(m), m.TimingConfidence.ToString(), m.ResultCode.ToString(), $"0x{m.HResult:X8}" }.Select(Csv)));
        await File.WriteAllTextAsync(Path.Combine(work, "timings.csv"), csv.ToString(), token);
        token.ThrowIfCancellationRequested();
        progress?.Report("Compressing local bundle");
        var zip = work + ".zip";
        using (var archive = ZipFile.Open(zip + ".partial", ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(Path.GetRelativePath(work, file).Replace('\\', '/'), CompressionLevel.Fastest);
                await using var output = entry.Open();
                await using var input = File.OpenRead(file);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }
        token.ThrowIfCancellationRequested();
        File.Move(zip + ".partial", zip);
        string? delivered = null, deliveryError = null;
        try
        {
            progress?.Report("Delivering ZIP to destination");
            Directory.CreateDirectory(options.Destination);
            var destination = Path.Combine(options.Destination, Path.GetFileName(zip));
            if (!Path.GetFullPath(destination).Equals(Path.GetFullPath(zip), StringComparison.OrdinalIgnoreCase))
            {
                await using (var input = File.OpenRead(zip))
                await using (var output = new FileStream(destination + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await using (var source = File.OpenRead(zip))
                await using (var copy = File.OpenRead(destination + ".partial"))
                {
                    var expected = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
                    var actual = await SHA256.HashDataAsync(copy, token).ConfigureAwait(false);
                    if (!expected.SequenceEqual(actual)) throw new IOException("Destination ZIP checksum verification failed.");
                }
                File.Move(destination + ".partial", destination);
            }
            delivered = destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { deliveryError = $"Delivery incomplete: {ex.Message}. Local ZIP retained. Any destination .partial file is incomplete."; }
        return new(zip, delivered, report, deliveryError, manifest.Count(m => m.Status == "Collected"), manifest.Count(m => m.Status != "Collected"));
    }

    private static IEnumerable<string> ReadLines(string path, CancellationToken token)
    {
        foreach (var line in File.ReadLines(path)) { token.ThrowIfCancellationRequested(); yield return line; }
    }

    private static async Task<IReadOnlyList<OperationMetric>> ReadMetricsAsync(ConcurrentBag<CollectedLog> manifest, CancellationToken token)
    {
        try { return await new JsonOperationMetricStore(readOnly: true).GetAllAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            manifest.Add(new("Operation timings", null, "Unavailable", 0, 0, null, ex.Message));
            return [];
        }
    }

    private static string[] DefaultRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows)!;
        return [Path.Combine(windows, "Panther"), Path.Combine(drive, "$WINDOWS.~BT", "Sources", "Panther"), Path.Combine(drive, "$WINDOWS.~BT", "Sources", "Rollback"), Path.Combine(windows, "Logs", "MoSetup"), Path.Combine(windows, "Logs", "SetupDiag"), Path.Combine(windows, "Logs", "CBS", "CBS.log"), Path.Combine(windows, "Logs", "DISM", "dism.log"), Path.Combine(windows, "INF", "setupapi.dev.log"), Path.Combine(windows, "Logs", "WindowsUpdate"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "operation-metrics.json")];
    }
    private static Task WriteJson<T>(string path, T value, CancellationToken token) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), token);
    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static string StageSeconds(TimeSpan value) => value == TimeSpan.Zero ? "Unavailable/not performed" : Seconds(value);
    private static string RebootSeconds(OperationMetric m) => m.BootCompletedAt is { } boot && m.RebootStartedAt is { } shutdown && boot >= shutdown ? Seconds(boot - shutdown) : "Unavailable";
    private static string Csv(string? value) => "\"" + ((value ?? "").StartsWith('=') || (value ?? "").StartsWith('+') || (value ?? "").StartsWith('-') || (value ?? "").StartsWith('@') ? "'" : "") + (value ?? "").Replace("\"", "\"\"") + "\"";
}
