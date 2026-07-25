using System.Net;
using System.Runtime.InteropServices;
using WuPilot.Infrastructure.Windows.Updates;

namespace WuPilot.Infrastructure.Tests;

public sealed class GitHubAppUpdateServiceTests
{
    [Fact]
    public async Task Check_SelectsStableInstallerForCurrentArchitecture()
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var installer = $"WuPilot-0.2.0-win-{architecture}-setup.exe";
        var json = $$"""
            {
              "tag_name":"v0.2.0","name":"WuPilot 0.2.0","body":"Notes","draft":false,"prerelease":false,
              "published_at":"2026-07-25T00:00:00Z","html_url":"https://github.com/retsak/WuPilot/releases/tag/v0.2.0",
              "assets":[
                {"name":"{{installer}}","browser_download_url":"https://github.com/retsak/WuPilot/releases/download/v0.2.0/{{installer}}","size":123,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
                {"name":"{{installer}}.sha256","browser_download_url":"https://github.com/retsak/WuPilot/releases/download/v0.2.0/{{installer}}.sha256","size":99,"digest":null}
              ]
            }
            """;
        using var client = new HttpClient(new StubHandler(json));
        var state = Path.Combine(Path.GetTempPath(), $"wupilot-update-{Guid.NewGuid():N}.json");
        try
        {
            var service = new GitHubAppUpdateService(client, state);
            var release = await service.CheckAsync(new Version(0, 1, 0), force: true, CancellationToken.None);
            Assert.NotNull(release);
            Assert.Equal(new Version(0, 2, 0), release.Version);
            Assert.Equal(installer, release.InstallerName);
        }
        finally
        {
            if (File.Exists(state)) File.Delete(state);
        }
    }

    [Fact]
    public async Task Check_IgnoresInstalledVersion()
    {
        const string json = """{"tag_name":"v0.2.0","name":"WuPilot","body":"","draft":false,"prerelease":false,"published_at":"2026-07-25T00:00:00Z","html_url":"https://github.com/retsak/WuPilot/releases/tag/v0.2.0","assets":[]}""";
        using var client = new HttpClient(new StubHandler(json));
        var service = new GitHubAppUpdateService(client, Path.Combine(Path.GetTempPath(), $"wupilot-update-{Guid.NewGuid():N}.json"));
        Assert.Null(await service.CheckAsync(new Version(0, 2, 0), force: true, CancellationToken.None));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test\"") }
            });
    }
}
