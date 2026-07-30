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
    public string InstallationCapabilityLabel => Update.RequiresVendorInstaller
        ? "Vendor installation may be required"
        : Update.MayRequestUserInput
            ? "Installer may request input"
            : string.Empty;

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

public sealed class PolicyStateItem(PolicyState state, bool isFavorite = false)
{
    public PolicyState State { get; } = state;
    public bool IsFavorite { get; set; } = isFavorite;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string DisplayName => State.Definition.DisplayName;
    public string Category => State.Definition.Category;
    public string Description => State.Definition.Description;
    public string ValueLabel => State.EffectiveValue is null ? "Windows default" :
        State.Definition.Choices?.GetValueOrDefault(State.EffectiveValue) ?? State.EffectiveValue;
    public string OwnershipLabel => State.Ownership.ToString();
    public string Status => State.Status;
    public string EditLabel => State.CanEdit ? "Editable" : "View only";
    public string RequestedLabel => State.RequestedValue ?? "Windows default";
    public string RiskLabel => State.Definition.Risk.ToString();
    public bool HasDifference => !string.Equals(State.RequestedValue, State.EffectiveValue, StringComparison.OrdinalIgnoreCase);
}

public sealed class SettingAuditItem(SettingAuditEntry entry)
{
    public SettingAuditEntry Entry { get; } = entry;
    public string Title => Entry.DisplayName;
    public string Summary => $"{Entry.ChangedAt:g} · {Entry.BeforeValue ?? "default"} → {Entry.AfterValue ?? "default"} · {(Entry.Succeeded ? "verified" : "failed")}";
}

public sealed class OperationMetricItem
{
    public OperationMetricItem(OperationMetric metric) => Metric = metric;
    public OperationMetric Metric { get; }
    public string Title => Metric.Title ?? Metric.Operation;
    public string When => Metric.CompletedAt.ToString("g");
    public string Result => Metric.ResultCode is 2 or 3 ? "Succeeded" : $"Failed · 0x{unchecked((uint)Metric.HResult):X8}";
    public string Timing => $"Total {Format(Metric.TotalDuration)} · download {Format(Metric.DownloadDuration)} · install {Format(Metric.InstallDuration)} · {Metric.TimingConfidence}";
    public string HResultLabel => $"0x{unchecked((uint)Metric.HResult):X8}";
    public string ErrorExplanation => Metric.HResult == 0
        ? "No error was reported."
        : HResultCatalog.Explain(Metric.HResult).Explanation;
    public string Recommendation => Metric.HResult == unchecked((int)0x80240020)
        ? "Retry while signed in and keep WuPilot in the foreground so any installer prompt can be answered. Windows Update or the OEM support tool is also available."
        : Metric.HResult == 0
            ? "No remediation is required."
            : HResultCatalog.Explain(Metric.HResult).Recommendation;
    public string DetailText => string.Join(Environment.NewLine,
    [
        $"Identity: {Metric.UpdateId ?? "Unavailable"}.{Metric.RevisionNumber?.ToString() ?? "?"}",
        $"Operation: {Metric.Operation}",
        $"Started: {Metric.StartedAt:O}",
        $"Completed: {Metric.CompletedAt:O}",
        $"Duration: {Format(Metric.TotalDuration)}",
        $"Final status: {Result}",
        $"Result code: {Metric.ResultCode}",
        $"HRESULT: {HResultLabel}",
        $"Description: {ErrorExplanation}",
        $"Restart required: {Metric.RebootRequired}",
        $"Update source: {Metric.UpdateSource ?? "Unavailable"}",
        $"Installation method: {Metric.InstallationMethod ?? "Unavailable"}",
        $"Hardware ID: {Metric.HardwareId ?? "Unavailable"}",
        $"Installer may request input: {Metric.EffectiveMayRequestUserInput?.ToString() ?? "Unknown"}",
        $"Downloaded bytes: {Metric.DownloadBytes?.ToString() ?? "Unavailable"}",
        $"Timing: {Timing}",
        $"Evidence source: {Metric.EvidenceSource}"
    ]);
    private static string Format(TimeSpan value) => value == default ? "n/a" : value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.0} min" : $"{value.TotalSeconds:0.0} sec";
}

public sealed class PolicyChoiceItem(string value, string displayName)
{
    public string Value { get; } = value;
    public string DisplayName { get; } = displayName;
}

public sealed class StagedPolicyChangeItem(StagedPolicyChange change)
{
    public StagedPolicyChange Change { get; } = change;
    public string Title => Change.DisplayName;
    public string Summary => $"{Change.BeforeValue ?? "Windows default"} → {(Change.Remove ? "Windows default" : Change.AfterValue)} · {Change.Risk}" +
        (Change.RequiresRestart ? " · restart required" : string.Empty);
    public string Warning => Change.Status;
}

public sealed class CompletionNoticeItem(CompletionNotice notice)
{
    public CompletionNotice Notice { get; } = notice;
    public string Title => Notice.Title;
    public string Summary => $"{Notice.CompletedAt:g} · {Notice.Message}";
    public string Severity => Notice.Severity.ToString();
}
