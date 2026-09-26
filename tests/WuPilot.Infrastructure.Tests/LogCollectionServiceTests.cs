using System.IO.Compression;
using System.Text.Json;
using WuPilot.Infrastructure.Windows.Export;

namespace WuPilot.Infrastructure.Tests;

public sealed class LogCollectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WuPilot-collection-test-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }

    [Fact]
    public async Task ConcurrentCollectionsCreateDistinctCompleteBundlesAndEscapeHtml()
    {
        var input = Folder("input");
        await File.WriteAllTextAsync(Path.Combine(input, "setupact.log"), "2026-09-01 12:00:00 Error <script>alert(1)</script>");
        var service = new LogCollectionService(Folder("local"));
        var options = new LogCollectionOptions(Folder("share"), input);
        var results = await Task.WhenAll(service.CollectAsync(options, null, default), service.CollectAsync(options, null, default)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(results[0].LocalZip, results[1].LocalZip);
        foreach (var result in results)
        {
            Assert.Null(result.DeliveryError);
            Assert.True(File.Exists(result.DeliveredZip));
            using var archive = ZipFile.OpenRead(result.DeliveredZip!);
            Assert.NotNull(archive.GetEntry("analysis.json"));
            Assert.NotNull(archive.GetEntry("timings.csv"));
            var html = await File.ReadAllTextAsync(result.ReportPath);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>", html);
        }
    }

    [Fact]
    public async Task DeliveryFailurePreservesLocalZipAndRecordsSizeLimit()
    {
        var input = Folder("input");
        await File.WriteAllTextAsync(Path.Combine(input, "large.log"), new string('x', 100));
        var destination = Path.Combine(Folder("destination"), "not-a-directory");
        await File.WriteAllTextAsync(destination, "file");
        var result = await new LogCollectionService(Folder("local")).CollectAsync(new(destination, input, MaxFileBytes: 10), null, default);
        Assert.NotNull(result.DeliveryError);
        Assert.True(File.Exists(result.LocalZip));
        Assert.Null(result.DeliveredZip);
        Assert.Equal(1, result.Unavailable);
        var manifest = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(result.ReportPath)!, "manifest.json"));
        Assert.Contains("Limit", manifest);
    }

    [Fact]
    public async Task CancellationDoesNotPublishArchive()
    {
        var input = Folder("input");
        var destination = Folder("share");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LogCollectionService(Folder("local")).CollectAsync(new(destination, input), null, cts.Token));
        Assert.Empty(Directory.GetFiles(destination));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
