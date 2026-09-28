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

    [Fact]
    public void ProbesCurrentAndRetainedSetupAndServicingEvidence()
    {
        var roots = LogCollectionService.DefaultRoots(@"C:\Windows");
        Assert.Contains(@"C:\Windows.old\$WINDOWS.~BT\Sources\Panther", roots);
        Assert.Contains(@"C:\Windows.old\$WINDOWS.~BT\Sources\Rollback", roots);
        Assert.Contains(@"C:\Windows.old\Windows\Panther", roots);
        Assert.Contains(@"C:\Windows\Logs\CBS", roots);
        Assert.Contains(@"C:\Windows.old\Windows\System32\winevt\Logs\Setup.evtx", roots);
    }

    [Fact]
    public async Task SelectedOlderUpdateSurvivesNewerUpdateAndRawEvidenceIsRetained()
    {
        var input = Folder("input");
        var oldId = "916031E1-9D13-48E9-A262-C8A0DB93FBAC";
        var newId = "A16031E1-9D13-48E9-A262-C8A0DB93FBAC";
        string Attempt(string date, string id, string kb) => $"{date} 06:00:00 Initializing UpdateId = [{id}.1]\n{date} 06:00:01 GenerateDownloadRequest: Enter\n{date} 06:00:02 Installing feature: Feature: CumulativeUpdate_{kb}\n{date} 06:01:01 ReportEventDownloadRequestEnd: DownloadComplete = [TRUE]\n{date} 06:02:00 Install: Enter\n{date} 06:04:00 Reboot required: [TRUE]\n";
        await File.WriteAllTextAsync(Path.Combine(input, "UpdateAgent.log"), Attempt("2026-01-01", oldId, "KB12345") + Attempt("2026-09-01", newId, "KB67890"));
        await File.WriteAllBytesAsync(Path.Combine(input, "CbsPersist.cab"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(input, "setupmem.dmp"), [3, 4]);
        var result = await new LogCollectionService(Folder("local")).CollectAsync(new(Folder("share"), input, SourceTimeZoneId: "UTC", SelectedUpdate: "12345"), null, default);
        var bundle = Path.GetDirectoryName(result.ReportPath)!;
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "phase-summary.json")));
        var update = Assert.Single(summary.RootElement.GetProperty("updates").EnumerateArray());
        Assert.Equal(oldId, update.GetProperty("updateId").GetString());
        Assert.Equal("00:01:00", update.GetProperty("download").GetProperty("duration").GetString());
        Assert.Equal(3, result.Collected);
        var regenerated = Folder("regenerated");
        await UpgradeReportService.RegenerateAsync(bundle, regenerated, "UTC", default, newId);
        using var newer = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(regenerated, "phase-summary.json")));
        Assert.Equal(newId, Assert.Single(newer.RootElement.GetProperty("updates").EnumerateArray()).GetProperty("updateId").GetString());
        await UpgradeReportService.RegenerateAsync(bundle, regenerated, "UTC", default, "KB00000");
        using var missing = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(regenerated, "phase-summary.json")));
        Assert.Empty(missing.RootElement.GetProperty("updates").EnumerateArray());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
