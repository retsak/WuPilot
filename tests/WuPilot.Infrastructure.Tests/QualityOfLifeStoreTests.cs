using System.Text.Json;
using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Profiles;

namespace WuPilot.Infrastructure.Tests;

public sealed class QualityOfLifeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WuPilot-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preferences_RoundTripWorkflowStateAndFavorites()
    {
        var path = Path.Combine(_directory, "preferences.json");
        using var store = new JsonAppPreferencesStore(path);
        var expected = AppPreferences.Default with
        {
            NavigationTag = "controls",
            Theme = "Dark",
            PerformanceRangeDays = 90,
            FavoritePolicyIds = ["update.latest"],
            Window = new(200, 150, 1200, 760, true)
        };

        store.ScheduleSave(expected);
        await store.FlushAsync(CancellationToken.None);
        var actual = await store.GetAsync(CancellationToken.None);

        Assert.Equal("controls", actual.NavigationTag);
        Assert.Equal("Dark", actual.Theme);
        Assert.Equal(90, actual.PerformanceRangeDays);
        Assert.Contains("update.latest", actual.FavoritePolicyIds!);
        Assert.True(actual.Window!.IsMaximized);
    }

    [Fact]
    public async Task Preferences_InvalidValuesAreNormalized()
    {
        var path = Path.Combine(_directory, "preferences.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(AppPreferences.Default with
        {
            NavigationTag = "unknown",
            Theme = "Neon",
            PerformanceRangeDays = 13
        }));
        using var store = new JsonAppPreferencesStore(path);

        var actual = await store.GetAsync(CancellationToken.None);

        Assert.Equal("scan", actual.NavigationTag);
        Assert.Equal("System", actual.Theme);
        Assert.Equal(30, actual.PerformanceRangeDays);
    }

    [Fact]
    public async Task Preferences_CorruptFileIsRecovered()
    {
        var path = Path.Combine(_directory, "preferences.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "{not-json");
        using var store = new JsonAppPreferencesStore(path);

        var actual = await store.GetAsync(CancellationToken.None);

        Assert.Equal(AppPreferences.Default.NavigationTag, actual.NavigationTag);
        Assert.Single(Directory.GetFiles(_directory, "preferences.json.corrupt-*"));
    }

    [Fact]
    public async Task CompletionNotices_RetainNewestFiftyAndDiscardOldEntries()
    {
        var path = Path.Combine(_directory, "notices.json");
        var store = new JsonCompletionNoticeStore(path);
        for (var index = 0; index < 55; index++)
            await store.SaveAsync(new(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-index), $"Notice {index}", "Done", "scan", CompletionSeverity.Success), CancellationToken.None);
        await store.SaveAsync(new(Guid.NewGuid(), DateTimeOffset.Now.AddDays(-31), "Old", "Old", "scan", CompletionSeverity.Information), CancellationToken.None);

        var actual = await store.GetAllAsync(CancellationToken.None);

        Assert.Equal(50, actual.Count);
        Assert.Equal("Notice 0", actual[0].Title);
        Assert.DoesNotContain(actual, notice => notice.Title == "Old");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
