using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Tests;

public sealed class JsonScanProfileStoreTests
{
    [Fact]
    public async Task SaveGetAndDelete_RoundTripsProfilesAndReplacesDuplicateName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WuPilot.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        try
        {
            var store = new JsonScanProfileStore(path);
            var original = SavedScanProfile.Create(
                "Driver review",
                ["default"],
                ScanPreset.MissingDrivers,
                null,
                false,
                null,
                null);
            await store.SaveAsync(original, CancellationToken.None);

            var replacement = SavedScanProfile.Create(
                "driver REVIEW",
                ["windows-update"],
                ScanPreset.MissingUpdates,
                null,
                true,
                null,
                null);
            await store.SaveAsync(replacement, CancellationToken.None);

            var saved = Assert.Single(await store.GetAllAsync(CancellationToken.None));
            Assert.Equal(replacement.Id, saved.Id);
            Assert.Equal(["windows-update"], saved.ProviderIds);

            await store.DeleteAsync(replacement.Id, CancellationToken.None);
            Assert.Empty(await store.GetAllAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
