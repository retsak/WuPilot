using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Tests;

public sealed class JsonOperationMetricStoreTests
{
    [Fact]
    public async Task ContendedProcessLockHonorsCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wupilot-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "metrics.json");
        try
        {
            await using var heldLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JsonOperationMetricStore(path).GetAllAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EventDurationsRequireIdentityAndDoNotReuseCompletedStarts()
    {
        const string identity = "11111111-2222-3333-4444-555555555555";
        var events = new[]
        {
            new { TimeCreated = "2026-09-01T10:00:00Z", Id = 43, Message = "start", Identity = identity },
            new { TimeCreated = "2026-09-01T10:03:00Z", Id = 19, Message = "unrelated without identity", Identity = "" },
            new { TimeCreated = "2026-09-01T10:04:00Z", Id = 19, Message = "done", Identity = identity },
            new { TimeCreated = "2026-09-01T10:05:00Z", Id = 19, Message = "duplicate", Identity = identity }
        };
        var metrics = JsonOperationMetricStore.ParseEstimatedWindowsHistory(System.Text.Json.JsonSerializer.Serialize(events));
        var metric = Assert.Single(metrics);
        Assert.Equal(TimeSpan.FromMinutes(4), metric.InstallDuration);
        Assert.Equal(EvidenceConfidence.Low, metric.TimingConfidence);
    }

    [Fact]
    public async Task ConcurrentStoreInstancesDoNotLoseWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wupilot-metrics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "metrics.json");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => new JsonOperationMetricStore(path).SaveAsync(
                new OperationMetric(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, "Download", i.ToString(), 1, "Test", 1,
                    TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(1), 2, 0, false), default))).WaitAsync(TimeSpan.FromSeconds(10));
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(30, document.RootElement.GetArrayLength());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Save_RoundTripsExactMetric()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wupilot-metrics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "metrics.json");
        try
        {
            var store = new JsonOperationMetricStore(path);
            var metric = new OperationMetric(Guid.NewGuid(), DateTimeOffset.Now.AddSeconds(-4), DateTimeOffset.Now,
                "Download", "id", 1, "Test update", 1024, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
                TimeSpan.Zero, TimeSpan.FromSeconds(4), 2, 0, false, MayRequestUserInput: true);
            await store.SaveAsync(metric, CancellationToken.None);
            var loaded = await store.GetAllAsync(CancellationToken.None);
            var exact = Assert.Single(loaded, item => item.Id == metric.Id);
            Assert.Equal(EvidenceConfidence.Exact, exact.TimingConfidence);
            Assert.Equal(TimeSpan.FromSeconds(4), exact.TotalDuration);
            Assert.True(exact.EffectiveMayRequestUserInput);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
