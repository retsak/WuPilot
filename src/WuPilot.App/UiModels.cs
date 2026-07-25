using System.ComponentModel;
using System.Runtime.CompilerServices;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.App;

public sealed class ProviderOption(UpdateProviderDefinition provider, bool isSelected = false) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;
    public UpdateProviderDefinition Provider { get; } = provider;
    public string DisplayName => Provider.DisplayName;
    public string Description => Provider.Description;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ScanPresetOption(ScanPreset value, string displayName, string description)
{
    public ScanPreset Value { get; } = value;
    public string DisplayName { get; } = displayName;
    public string Description { get; } = description;
}

public sealed class DiagnosticFindingItem(DiagnosticFinding finding)
{
    public DiagnosticFinding Finding { get; } = finding;
    public string Title => Finding.Title;
    public string Summary => Finding.Summary;
    public string? Recommendation => Finding.Recommendation;
    public string SeverityLabel => Finding.Severity.ToString();
}

public sealed class UpdateHistoryItem(UpdateHistoryRecord record)
{
    public UpdateHistoryRecord Record { get; } = record;
    public DateTimeOffset? Date => Record.Date;
    public string? Title => Record.Title;
    public int ResultCode => Record.ResultCode;
    public string DateLabel => Record.Date?.ToString("g") ?? "Date unavailable";
    public string OperationLabel => Record.Operation switch
    {
        1 => "Installation",
        2 => "Uninstallation",
        3 => "Other",
        _ => $"Operation {Record.Operation}"
    };
    public string ResultLabel => Record.ResultCode switch
    {
        2 => "Succeeded",
        3 => "Succeeded with errors",
        4 => "Failed",
        5 => "Aborted",
        _ => $"Result {Record.ResultCode}"
    };
    public string HResultLabel => $"0x{unchecked((uint)Record.HResult):X8}";
    public string UpdateIdLabel => Record.UpdateId ?? "—";
    public string SourceLabel => string.Join(" · ", new[]
    {
        Record.ClientApplicationId,
        Record.ServiceId
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class UpdateListItem(UpdateRecord update)
{
    public UpdateRecord Update { get; } = update;
    public string Title => Update.Title;
    public string TypeLabel => Update.Kind.ToString();
    public string SourceLabel => string.Join(" · ", Update.ProviderNames);
    public string Metadata => Update.IsDriver
        ? string.Join(" · ", new[]
        {
            Update.Driver?.Manufacturer,
            Update.Driver?.DriverClass,
            Update.Driver?.InstalledMatch?.Driver.DriverVersion is { Length: > 0 } installed ? $"installed {installed}" : null,
            DriverVersionParser.InferFromTitle(Update.Title) is { Length: > 0 } offered ? $"offered {offered}" : null,
            Update.Driver?.VersionDate?.ToString("yyyy-MM-dd")
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))
        : string.Join(" · ", Update.KbArticleIds.Select(static kb => kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? kb : $"KB{kb}"));
    public string SizeLabel => Update.MaximumDownloadBytes is null ? "Size unavailable" : FormatBytes(Update.MaximumDownloadBytes.Value);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

public enum ResultFilter
{
    All,
    Drivers,
    Software,
    Installed,
    Downloaded,
    Hidden,
    RestartRequired
}

public enum ResultSort
{
    Default,
    Title,
    SizeDescending,
    DateDescending,
    Severity
}

public sealed class ResultFilterOption(ResultFilter value, string displayName)
{
    public ResultFilter Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public sealed class ResultSortOption(ResultSort value, string displayName)
{
    public ResultSort Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public sealed class DiagnosticSeverityOption(DiagnosticSeverity? value, string displayName)
{
    public DiagnosticSeverity? Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public sealed class ScanChangeItem(ScanUpdateChange change)
{
    public string Title { get; } = change.Title;
    public string KindLabel { get; } = change.Kind switch
    {
        ScanChangeKind.New => "New",
        ScanChangeKind.Removed => "No longer offered",
        ScanChangeKind.RevisionChanged => "Revision changed",
        ScanChangeKind.StateChanged => "State changed",
        _ => change.Kind.ToString()
    };
    public string Summary { get; } = change.Summary;
    public string Identity { get; } = change.CurrentRevision is not null
        ? $"{change.UpdateId}.{change.CurrentRevision}"
        : $"{change.UpdateId}.{change.PreviousRevision}";
}

public sealed class SavedScanProfileItem(SavedScanProfile profile)
{
    public SavedScanProfile Profile { get; } = profile;
    public string Name => Profile.Name;
}

public sealed class UpdateSourceRegistrationItem(UpdateSourceRegistration source)
{
    public UpdateSourceRegistration Source { get; } = source;
    public string Name => Source.Name;
    public string ServiceId => Source.ServiceId;
    public string RoleLabel => string.Join(" · ", new[]
    {
        Source.IsDefaultAuService ? "Automatic Updates default" : null,
        Source.IsManaged ? "Managed" : "Unmanaged",
        Source.IsScanPackageService ? "Offline scan package" : null
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    public string CapabilityLabel => Source.OffersWindowsUpdates
        ? "Offers Windows updates"
        : "Registered for another update role";
}

public sealed class WatchedUpdateItem(WatchedUpdate update)
{
    public WatchedUpdate Update { get; } = update;
    public string Title => Update.Title;
    public string Identity => $"{Update.UpdateId}.{Update.RevisionNumber}";
    public string TypeLabel => Update.Kind.ToString();
    public string Sources => string.Join(" · ", Update.ProviderNames);
    public string StatusLabel => Update.IsOfferedInLastScan switch
    {
        true => "Offered in latest scan",
        false => "Not offered in latest scan",
        null => "Not checked this session"
    };
    public string StateLabel => $"Installed: {Update.IsInstalled} · Downloaded: {Update.IsDownloaded} · Hidden: {Update.IsHidden}";
    public string DriverLabel => Update.Kind == UpdateKind.Driver
        ? $"Offered {Update.OfferedDriverVersion ?? "version unavailable"} · {Update.OfferedDriverDate?.ToString("yyyy-MM-dd") ?? "date unavailable"}"
        : "Software update";
    public string CheckedLabel => $"Last checked {Update.LastCheckedAt:g}";
}
