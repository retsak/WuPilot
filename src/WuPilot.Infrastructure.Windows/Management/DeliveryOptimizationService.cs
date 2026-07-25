using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Diagnostics;

namespace WuPilot.Infrastructure.Windows.Management;

public sealed class DeliveryOptimizationService : IDeliveryOptimizationService
{
    public async Task<DeliveryOptimizationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference='Stop'
            function value($o,[string[]]$names) {
              foreach($n in $names) {
                $property=$o.PSObject.Properties[$n]
                if($null -ne $property -and $null -ne $property.Value) { return $property.Value }
              }
              return 0
            }
            $p=Get-DeliveryOptimizationPerfSnap
            $s=@(Get-DeliveryOptimizationStatus)
            [pscustomobject]@{
              Mode=(Get-DODownloadMode).ToString()
              Http=[long](value $p @('DownloadHttpBytes','BytesFromHttp'))
              Cache=[long](value $p @('DownloadCacheHostBytes','BytesFromCacheServer'))
              Lan=[long](value $p @('DownloadLanPeerBytes','BytesFromLanPeers'))
              Internet=[long](value $p @('DownloadInternetPeerBytes','BytesFromInternetPeers'))
              Uploaded=[long]((value $p @('UploadLanPeerBytes')) + (value $p @('UploadInternetPeerBytes')))
              CacheBytes=[long](value $p @('CacheSizeBytes'))
              Mbps=[double](value $p @('AverageDownloadSpeedMbps'))
              Active=[int](@($s|Where-Object {$_.Status -notin @('Complete','Paused')}).Count)
              Foreground=(Get-DOPercentageMaxForegroundBandwidth).ToString()
              Background=(Get-DOPercentageMaxBackgroundBandwidth).ToString()
            } | ConvertTo-Json -Compress
            """;
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return new(DateTimeOffset.Now, "Unavailable", 0, 0, 0, 0, 0, 0, null, 0, null, null, "DeliveryOptimization PowerShell module", result.Error.Trim());
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            return new(DateTimeOffset.Now, Text(root,"Mode") ?? "Unknown", Number(root,"Http"), Number(root,"Cache"), Number(root,"Lan"),
                Number(root,"Internet"), Number(root,"Uploaded"), Number(root,"CacheBytes"), Double(root,"Mbps"), (int)Number(root,"Active"),
                Text(root,"Foreground"), Text(root,"Background"), "Get-DeliveryOptimizationPerfSnap");
        }
        catch (JsonException exception)
        {
            return new(DateTimeOffset.Now, "Unavailable", 0, 0, 0, 0, 0, 0, null, 0, null, null, "DeliveryOptimization PowerShell module", exception.Message);
        }
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static double? Double(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}
