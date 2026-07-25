using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Tests;

public sealed class JsonOperationMetricStoreTests
{
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
                TimeSpan.Zero, TimeSpan.FromSeconds(4), 2, 0, false);
            await store.SaveAsync(metric, CancellationToken.None);
            var loaded = await store.GetAllAsync(CancellationToken.None);
            var exact = Assert.Single(loaded, item => item.Id == metric.Id);
            Assert.Equal(EvidenceConfidence.Exact, exact.TimingConfidence);
            Assert.Equal(TimeSpan.FromSeconds(4), exact.TotalDuration);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
