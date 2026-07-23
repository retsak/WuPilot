using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Diagnostics;

public sealed class WindowsDeviceIdentityProvider : IDeviceIdentityProvider
{
    public async Task<DeviceIdentity> GetAsync(CancellationToken cancellationToken)
    {
        const string script = "$cs=Get-CimInstance Win32_ComputerSystem; $bios=Get-CimInstance Win32_BIOS; $os=Get-CimInstance Win32_OperatingSystem; [pscustomobject]@{Manufacturer=$cs.Manufacturer;Model=$cs.Model;SerialNumber=$bios.SerialNumber;OsCaption=$os.Caption;OsVersion=$os.Version;OsBuild=$os.BuildNumber;Architecture=$os.OSArchitecture}|ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

        string? manufacturer = null;
        string? model = null;
        string? serialNumber = null;
        string? osCaption = null;
        string? osVersion = Environment.OSVersion.Version.ToString();
        string? osBuild = Environment.OSVersion.Version.Build.ToString();
        string? architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            manufacturer = GetString(root, "Manufacturer");
            model = GetString(root, "Model");
            serialNumber = GetString(root, "SerialNumber");
            osCaption = GetString(root, "OsCaption");
            osVersion = GetString(root, "OsVersion") ?? osVersion;
            osBuild = GetString(root, "OsBuild") ?? osBuild;
            architecture = GetString(root, "Architecture") ?? architecture;
        }

        var (deviceId, tenantId) = await ReadJoinIdentityAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceIdentity(
            Environment.MachineName,
            manufacturer,
            model,
            serialNumber,
            osCaption,
            osVersion,
            osBuild,
            architecture,
            deviceId,
            tenantId);
    }

    private static async Task<(string? DeviceId, string? TenantId)> ReadJoinIdentityAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("dsregcmd.exe", ["/status"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return (null, null);

        string? deviceId = null;
        string? tenantId = null;
        using var reader = new StringReader(result.Output);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("DeviceId", StringComparison.OrdinalIgnoreCase)) deviceId = parts[1];
            if (parts[0].Equals("TenantId", StringComparison.OrdinalIgnoreCase)) tenantId = parts[1];
        }

        return (deviceId, tenantId);
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
}
