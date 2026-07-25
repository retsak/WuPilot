using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Tests;

public sealed class JsonWatchlistStoreTests
{
    [Fact]
    public async Task SaveSaveAllAndDelete_RoundTripByUpdateId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WuPilot.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "watchlist.json");
        try
        {
            var store = new JsonWatchlistStore(path);
            var first = Watched("id-1", 1);
            await store.SaveAsync(first, CancellationToken.None);
            await store.SaveAsync(first with { RevisionNumber = 2 }, CancellationToken.None);

            var updated = Assert.Single(await store.GetAllAsync(CancellationToken.None));
            Assert.Equal(2, updated.RevisionNumber);

            await store.SaveAllAsync([updated, Watched("id-2", 1)], CancellationToken.None);
            Assert.Equal(2, (await store.GetAllAsync(CancellationToken.None)).Count);

            await store.DeleteAsync("ID-1", CancellationToken.None);
            Assert.Equal("id-2", Assert.Single(await store.GetAllAsync(CancellationToken.None)).UpdateId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static WatchedUpdate Watched(string id, int revision) =>
        new(id, revision, $"{id} title", UpdateKind.Software, ["Policy default"], false, false, false, null, null, DateTimeOffset.Now, DateTimeOffset.Now, true);
}
