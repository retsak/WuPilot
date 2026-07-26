using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Diagnostics;

namespace WuPilot.Infrastructure.Windows.Updates;

public sealed class GitHubAppUpdateService(HttpClient? client = null, string? statePath = null) : IAppUpdateService
{
    private readonly HttpClient _client = client ?? CreateClient();
    private readonly string _statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "update-state.json");
    private const string LatestRelease = "https://api.github.com/repos/retsak/WuPilot/releases/latest";

    public async Task<AppReleaseInfo?> CheckAsync(Version currentVersion, bool force, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        if (!force && !string.IsNullOrWhiteSpace(state.ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            await WriteStateAsync(state with { LastCheckedAt = DateTimeOffset.Now }, cancellationToken).ConfigureAwait(false);
            return state.CachedReleaseJson is null ? null : ParseRelease(state.CachedReleaseJson, currentVersion);
        }
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var info = ParseRelease(json, currentVersion);
        await WriteStateAsync(new(DateTimeOffset.Now, response.Headers.ETag?.Tag, info?.Tag, json), cancellationToken).ConfigureAwait(false);
        return info;
    }

    private static AppReleaseInfo? ParseRelease(string json, Version currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= currentVersion) return null;
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var expected = $"WuPilot-{version.ToString(3)}-win-{architecture}-setup.exe";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var installer = assets.SingleOrDefault(asset => asset.GetProperty("name").GetString() == expected);
        var checksum = assets.SingleOrDefault(asset => asset.GetProperty("name").GetString() == expected + ".sha256");
        if (installer.ValueKind == JsonValueKind.Undefined || checksum.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Release {tag} does not contain the expected {architecture} installer and checksum.");
        return new AppReleaseInfo(version, tag, root.GetProperty("name").GetString() ?? tag,
            root.GetProperty("body").GetString() ?? string.Empty, root.GetProperty("published_at").GetDateTimeOffset(),
            RequireGitHubUri(root.GetProperty("html_url").GetString()), RequireGitHubUri(installer.GetProperty("browser_download_url").GetString()),
            RequireGitHubUri(checksum.GetProperty("browser_download_url").GetString()), expected, installer.GetProperty("size").GetInt64(),
            installer.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }

    public async Task<DownloadedAppUpdate> DownloadAsync(AppReleaseInfo release, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "Updates", release.Version.ToString(3));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.InstallerName);
        progress?.Report(new("App update", $"Downloading {release.InstallerName}…", 10));
        await using (var source = await _client.GetStreamAsync(release.InstallerUrl, cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(path))
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        if (new FileInfo(path).Length != release.Size)
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded installer size does not match the GitHub release metadata.");
        }
        var sidecar = await _client.GetStringAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        var expected = sidecar.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(path), cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var github = release.GitHubDigest?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (expected?.Length != 64 || github?.Length != 64 || actual != expected || actual != github)
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded installer failed SHA-256 verification.");
        }
        progress?.Report(new("App update", "Verifying installer signature…", 90));
        var escaped = path.Replace("'", "''", StringComparison.Ordinal);
        var signature = await ProcessRunner.PowerShellAsync($"(Get-AuthenticodeSignature -LiteralPath '{escaped}').Status.ToString()", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var status = signature.Output.Trim();
        var signed = !status.Equals("NotSigned", StringComparison.OrdinalIgnoreCase);
        var valid = status.Equals("Valid", StringComparison.OrdinalIgnoreCase);
        if (signed && !valid) { File.Delete(path); throw new InvalidDataException($"Installer signature status is {status}."); }
        return new(release, path, actual, signed, valid);
    }

    public void LaunchInstaller(DownloadedAppUpdate update)
    {
        Process.Start(new ProcessStartInfo(update.InstallerPath) { UseShellExecute = true, Arguments = "/CLOSEAPPLICATIONS /NORESTART" });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WuPilot", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    private static Uri RequireGitHubUri(string? value)
    {
        var uri = new Uri(value ?? throw new InvalidDataException("Release URL is missing."), UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release assets must be served over HTTPS by GitHub.");
        return uri;
    }
    private async Task<UpdateState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath)) return new(default, null, null, null);
        try { return JsonSerializer.Deserialize<UpdateState>(await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false)) ?? new(default,null,null,null); }
        catch (JsonException) { return new(default, null, null, null); }
    }
    private async Task WriteStateAsync(UpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(_statePath, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
    }
    private sealed record UpdateState(DateTimeOffset LastCheckedAt, string? ETag, string? LatestTag, string? CachedReleaseJson);
}
