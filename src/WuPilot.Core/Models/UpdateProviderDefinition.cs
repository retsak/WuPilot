namespace WuPilot.Core.Models;

public enum UpdateServerSelection
{
    Default = 0,
    ManagedServer = 1,
    WindowsUpdate = 2,
    Others = 3
}

public sealed record UpdateProviderDefinition(
    string Id,
    string DisplayName,
    string Description,
    UpdateServerSelection ServerSelection,
    string? ServiceId = null,
    bool IsDirectMicrosoftSource = false,
    string? ScanPackagePath = null)
{
    public static IReadOnlyList<UpdateProviderDefinition> BuiltIn { get; } =
    [
        new("default", "Policy default", "Uses the update source selected by local policy (managed WSUS, Microsoft Update, or Windows Update).", UpdateServerSelection.Default),
        new("wsus", "Managed WSUS", "Queries the intranet update service configured by policy.", UpdateServerSelection.ManagedServer),
        new("windows-update", "Windows Update", "Queries the public Windows Update service directly, including applicable driver metadata when the driver preset is selected.", UpdateServerSelection.WindowsUpdate, IsDirectMicrosoftSource: true),
        new("microsoft-update", "Microsoft Update", "Includes updates for Windows and other Microsoft products.", UpdateServerSelection.Others, "7971f918-a847-4430-9279-4a52d1efe18d", true),
        new("store", "Microsoft Store service", "Queries the registered Store update service. Results depend on device registration.", UpdateServerSelection.Others, "855E8A7C-ECB4-4CA3-B045-1DFA50104289", true)
    ];

    public static UpdateProviderDefinition Custom(string serviceId, string? name = null)
    {
        if (!Guid.TryParse(serviceId, out _))
        {
            throw new ArgumentException("A custom update service ID must be a GUID.", nameof(serviceId));
        }

        return new UpdateProviderDefinition(
            $"custom-{serviceId.ToLowerInvariant()}",
            string.IsNullOrWhiteSpace(name) ? "Custom WUA service" : name.Trim(),
            "Queries a WUA service by ServiceID.",
            UpdateServerSelection.Others,
            serviceId);
    }

    public static UpdateProviderDefinition OfflineScanPackage(string scanPackagePath)
    {
        if (string.IsNullOrWhiteSpace(scanPackagePath))
        {
            throw new ArgumentException("An offline scan package path is required.", nameof(scanPackagePath));
        }

        var path = scanPackagePath.Trim().Trim('"');
        if (!Path.GetExtension(path).Equals(".cab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The offline scan package must be a Microsoft-signed .cab file such as Wsusscn2.cab.", nameof(scanPackagePath));
        }

        return new UpdateProviderDefinition(
            "offline-scan",
            "Offline security catalog",
            "Uses a Microsoft-signed Wsusscn2.cab without contacting an update server. The catalog contains security metadata, not drivers or payloads.",
            UpdateServerSelection.Others,
            ScanPackagePath: path);
    }
}
