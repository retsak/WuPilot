using System.Text.Json;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;

namespace WuPilot.Infrastructure.Windows.Diagnostics;

public sealed class InstalledDriverProvider : IInstalledDriverProvider
{
    public async Task<IReadOnlyList<InstalledDriverInfo>> GetInstalledDriversAsync(CancellationToken cancellationToken)
    {
        const string script = "Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop | ForEach-Object { [pscustomobject]@{ DeviceId=$_.DeviceID;DeviceName=$_.DeviceName;HardwareId=if($_.HardWareID -is [array]){$_.HardWareID[0]}else{$_.HardWareID};CompatibleId=if($_.CompatID -is [array]){$_.CompatID[0]}else{$_.CompatID};DeviceClass=$_.DeviceClass;DriverVersion=$_.DriverVersion;DriverDate=if($_.DriverDate){$_.DriverDate.ToString('o')}else{$null};Manufacturer=$_.Manufacturer;ProviderName=$_.DriverProviderName;InfName=$_.InfName;IsSigned=$_.IsSigned;Signer=$_.Signer} } | ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Installed driver inventory failed: {result.Error.Trim()}");
        }

        if (string.IsNullOrWhiteSpace(result.Output)) return [];
        using var document = JsonDocument.Parse(result.Output);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];

        return elements.Select(Map).ToArray();
    }

    private static InstalledDriverInfo Map(JsonElement value) => new(
        Text(value, "DeviceId"),
        Text(value, "DeviceName"),
        Text(value, "HardwareId"),
        Text(value, "CompatibleId"),
        Text(value, "DeviceClass"),
        Text(value, "DriverVersion"),
        Date(value, "DriverDate"),
        Text(value, "Manufacturer"),
        Text(value, "ProviderName"),
        Text(value, "InfName"),
        Boolean(value, "IsSigned"),
        Text(value, "Signer"));

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? item.ToString()
            : null;

    private static bool? Boolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean()
            : null;

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        DateTimeOffset.TryParse(Text(value, property), out var parsed) ? parsed : null;
}
