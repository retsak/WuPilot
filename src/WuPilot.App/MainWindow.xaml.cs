using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Core.Services;
using WuPilot.Infrastructure.Windows.Diagnostics;
using WuPilot.Infrastructure.Windows.Export;
using WuPilot.Infrastructure.Windows.Wua;

namespace WuPilot.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IUpdateScanService _scanService;
    private readonly IUpdateActionService _actionService;
    private readonly IDiagnosticService _diagnosticService;
    private readonly IEvidenceExportService _exportService;
    private readonly List<UpdateListItem> _allUpdates = [];
    private CancellationTokenSource? _operationCancellation;
    private ScanReport? _scanReport;
    private DiagnosticSnapshot? _diagnostics;
    private UpdateListItem? _selectedUpdate;
    private bool _isBusy;
    private string _resultSummary = "No scan yet";

    public ObservableCollection<ProviderOption> ProviderOptions { get; } = [];
    public ObservableCollection<ScanPresetOption> PresetOptions { get; } = [];
    public ObservableCollection<UpdateListItem> VisibleUpdates { get; } = [];
    public ObservableCollection<DiagnosticFinding> DiagnosticFindings { get; } = [];
    public ObservableCollection<KeyValuePair<string, string?>> ServiceStates { get; } = [];
    public ObservableCollection<KeyValuePair<string, string?>> PolicyStates { get; } = [];
    public ObservableCollection<UpdateHistoryRecord> UpdateHistory { get; } = [];
    public ObservableCollection<string> ActivityItems { get; } = [];

    public bool HasScanReport => _scanReport is not null;
    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (_resultSummary == value) return;
            _resultSummary = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));

        var identityProvider = new WindowsDeviceIdentityProvider();
        var wua = new WuaUpdateService(identityProvider, new InstalledDriverProvider());
        _scanService = wua;
        _actionService = wua;
        _diagnosticService = new WindowsDiagnosticService(identityProvider, new WuaHistoryProvider());
        _exportService = new EvidenceExportService();

        foreach (var provider in UpdateProviderDefinition.BuiltIn)
        {
            ProviderOptions.Add(new ProviderOption(provider, provider.Id == "default"));
        }

        PresetOptions.Add(new ScanPresetOption(ScanPreset.MissingUpdates, "Missing updates", "All visible applicable software and drivers."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.MissingSoftware, "Missing software", "Applicable non-driver updates."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.MissingDrivers, "Missing drivers", "Applicable driver and firmware updates."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.InstalledUpdates, "Installed updates", "Updates WUA reports as installed."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.HiddenUpdates, "Hidden updates", "Updates hidden on this device."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.EverythingApplicable, "Everything applicable", "Includes hidden applicable updates."));
        PresetOptions.Add(new ScanPresetOption(ScanPreset.Custom, "Advanced criteria", "A custom WUA search expression."));
        Log("WuPilot started elevated. No device changes have been made.");
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var providers = ProviderOptions.Where(static option => option.IsSelected).Select(static option => option.Provider).ToList();
        if (!string.IsNullOrWhiteSpace(CustomServiceIdBox.Text))
        {
            try
            {
                providers.Add(UpdateProviderDefinition.Custom(CustomServiceIdBox.Text));
            }
            catch (ArgumentException exception)
            {
                await ShowMessageAsync("Invalid custom service", exception.Message);
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(OfflineCabPathBox.Text))
        {
            try
            {
                providers.Add(UpdateProviderDefinition.OfflineScanPackage(OfflineCabPathBox.Text));
            }
            catch (ArgumentException exception)
            {
                await ShowMessageAsync("Invalid offline catalog", exception.Message);
                return;
            }
        }
        if (providers.Count == 0)
        {
            await ShowMessageAsync("Select a source", "Choose at least one update source before scanning.");
            return;
        }

        if (PresetCombo.SelectedItem is not ScanPresetOption selectedPreset) return;
        var request = new ScanRequest(
            providers,
            selectedPreset.Value,
            selectedPreset.Value == ScanPreset.Custom ? CustomCriteriaBox.Text : null,
            SupersededCheck.IsChecked == true);

        SetBusy(true, "Starting Windows Update Agent scan…");
        _operationCancellation = new CancellationTokenSource();
        var progress = CreateProgress();
        Log($"Scan started: {selectedPreset.DisplayName}; sources: {string.Join(", ", providers.Select(static provider => provider.DisplayName))}.");
        try
        {
            _scanReport = await _scanService.ScanAsync(request, progress, _operationCancellation.Token);
            _allUpdates.Clear();
            _allUpdates.AddRange(_scanReport.Updates.Select(static update => new UpdateListItem(update)));
            ApplyFilter();
            ResultSummary = $"{_scanReport.Updates.Count} updates · {_scanReport.DriverCount} drivers · {_scanReport.FailedProviderCount} source failures";
            OnPropertyChanged(nameof(HasScanReport));
            Log($"Scan {_scanReport.ScanId} completed with {_scanReport.Updates.Count} unique updates. Failed sources: {_scanReport.FailedProviderCount}.");

            var failed = _scanReport.ProviderResults.Where(static result => !result.Succeeded).ToArray();
            if (failed.Length > 0)
            {
                await ShowMessageAsync(
                    "Scan completed with source errors",
                    string.Join(Environment.NewLine + Environment.NewLine, failed.Select(static result => $"{result.Provider.DisplayName}: {result.ErrorCode} {result.ErrorMessage}")));
            }
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled. WUA cannot interrupt every synchronous provider call immediately.");
            StatusText.Text = "Scan cancelled";
        }
        catch (Exception exception)
        {
            Log($"Scan failed: {exception.GetType().Name}: {exception.Message}");
            await ShowMessageAsync("Scan failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StatusText.Text = "Cancellation requested; waiting for the current WUA call…";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_scanReport is null) return;
        SetBusy(true, "Creating evidence bundle…");
        try
        {
            var directory = await _exportService.ExportAsync(_scanReport, _diagnostics, null, CancellationToken.None);
            Log($"Evidence bundle exported: {directory}");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Evidence bundle created",
                Content = $"The scan report, driver CSV, Intune review page, and available diagnostics were saved to:\n\n{directory}",
                PrimaryButtonText = "Open folder",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            Log($"Evidence export failed: {exception.Message}");
            await ShowMessageAsync("Export failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Collecting diagnostics…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            _diagnostics = await _diagnosticService.CollectAsync(CreateProgress(), _operationCancellation.Token);
            Replace(DiagnosticFindings, _diagnostics.Findings);
            Replace(ServiceStates, _diagnostics.Services.OrderBy(static pair => pair.Key));
            Replace(PolicyStates, _diagnostics.Policies.Where(static pair => pair.Value is not null).OrderBy(static pair => pair.Key));
            Replace(UpdateHistory, (_diagnostics.UpdateHistory ?? []).Take(25));
            Log($"Diagnostics {_diagnostics.SnapshotId} completed with {_diagnostics.Findings.Count} findings.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log($"Diagnostics failed: {exception.Message}");
            await ShowMessageAsync("Diagnostics failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<RepairAction>(tag, out var action)) return;
        var especiallySensitive = action is RepairAction.ResetWindowsUpdateCache or RepairAction.RestoreComponentStore;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Run {Readable(action)}?",
            Content = especiallySensitive
                ? "This changes local servicing state. Close other installers first. The update cache reset stops services and renames cache folders to recoverable timestamped paths; RestoreHealth can download repair content. No restart is performed automatically."
                : "This action runs locally as administrator. No restart is performed automatically. Review the activity result and re-run diagnostics afterward.",
            PrimaryButtonText = "Run action",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, $"Running {Readable(action)}…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var result = await _diagnosticService.RepairAsync(action, CreateProgress(), _operationCancellation.Token);
            Log($"Repair {action}: exit {result.ExitCode}; {result.Summary} Recovery: {result.RecoveryPath ?? "n/a"}");
            await ShowMessageAsync(
                result.Succeeded ? "Action completed" : "Action failed",
                $"{result.Summary}\n\nExit code: {result.ExitCode}\nRecovery path: {result.RecoveryPath ?? "Not applicable"}\n\n{TrimForDialog(result.Output, result.Error)}");
        }
        catch (OperationCanceledException)
        {
            Log($"Repair {action} was cancelled.");
        }
        catch (Exception exception)
        {
            Log($"Repair {action} failed: {exception.Message}");
            await ShowMessageAsync("Repair failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_selectedUpdate is null || sender is not Button { Tag: string tag } || !Enum.TryParse<UpdateAction>(tag, out var action)) return;
        var update = _selectedUpdate.Update;
        var provider = _scanReport?.ProviderResults.Select(static result => result.Provider).FirstOrDefault(item => item.Id == update.PrimaryProviderId)
            ?? ProviderOptions.Select(static option => option.Provider).FirstOrDefault(item => item.Id == update.PrimaryProviderId);
        if (provider is null)
        {
            await ShowMessageAsync("Source unavailable", "The original scan source is no longer configured.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"{action} this update?",
            Content = action switch
            {
                UpdateAction.Install => $"{update.Title}\n\nThis downloads and installs only on this test device. License terms will be accepted if required. WuPilot will not restart the device.",
                UpdateAction.Download => $"{update.Title}\n\nThis downloads payload into the Windows Update cache and accepts license terms if required. It does not install the update.",
                UpdateAction.Hide => $"{update.Title}\n\nHiding changes local WUA visibility and does not change Intune approval state.",
                UpdateAction.Show => $"{update.Title}\n\nThis makes the update visible to local WUA searches again.",
                _ => update.Title
            },
            PrimaryButtonText = action.ToString(),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, $"{action} in progress…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var result = await _actionService.ExecuteAsync(new UpdateActionRequest(update, provider, action, AcceptEula: true), CreateProgress(), _operationCancellation.Token);
            Log($"{action} {update.UpdateId}.{update.RevisionNumber}: result {result.ResultCode}, HRESULT 0x{unchecked((uint)result.HResult):X8}, reboot={result.RebootRequired}. {result.Message}");
            await ShowMessageAsync(result.Succeeded ? $"{action} completed" : $"{action} failed", $"{result.Message}\n\nResult: {result.ResultCode}\nHRESULT: 0x{unchecked((uint)result.HResult):X8}\nRestart required: {result.RebootRequired}\n\nRe-scan to refresh applicability and state.");
        }
        catch (OperationCanceledException)
        {
            Log($"{action} cancellation requested for {update.UpdateId}.");
        }
        catch (Exception exception)
        {
            Log($"{action} failed: {exception.Message}");
            await ShowMessageAsync($"{action} failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void UpdatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedUpdate = UpdatesList.SelectedItem as UpdateListItem;
        var update = _selectedUpdate?.Update;
        var selectedProvider = update is null
            ? null
            : _scanReport?.ProviderResults.Select(static result => result.Provider).FirstOrDefault(provider => provider.Id == update.PrimaryProviderId);
        var actionable = selectedProvider is not null && selectedProvider.ScanPackagePath is null;
        var enabled = update is not null;
        DownloadButton.IsEnabled = enabled && actionable && update!.IsInstalled == false;
        InstallButton.IsEnabled = enabled && actionable && update!.IsInstalled == false;
        HideButton.IsEnabled = enabled && actionable;
        if (update is null) return;

        HideButton.Content = update.IsHidden ? "Show" : "Hide";
        HideButton.Tag = update.IsHidden ? "Show" : "Hide";
        DetailTitle.Text = update.Title;
        DetailDescription.Text = update.Description ?? "No description was supplied by the update service.";
        DetailIdentity.Text = $"{update.UpdateId}.{update.RevisionNumber}";
        DetailSources.Text = string.Join(", ", update.ProviderNames);
        DetailManufacturer.Text = string.Join(" / ", new[] { update.Driver?.Manufacturer, update.Driver?.Provider }.Where(static value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } manufacturer ? manufacturer : "—";
        DetailModel.Text = string.Join(" / ", new[] { update.Driver?.Model, update.Driver?.DriverClass }.Where(static value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } model ? model : "—";
        DetailHardware.Text = update.Driver?.HardwareId ?? "—";
        DetailDate.Text = string.Join(" / ", new[] { DriverVersionParser.InferFromTitle(update.Title), update.Driver?.VersionDate?.ToString("yyyy-MM-dd") }.Where(static value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } versionDate ? versionDate : "—";
        var installed = update.Driver?.InstalledMatch;
        DetailInstalled.Text = installed is null
            ? "No confident local match"
            : string.Join(" / ", new[] { installed.Driver.DeviceName, installed.Driver.DriverVersion, installed.Driver.DriverDate?.ToString("yyyy-MM-dd"), installed.Driver.InfName }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        DetailSignature.Text = installed is null
            ? "—"
            : $"{(installed.Driver.IsSigned == true ? "Signed" : installed.Driver.IsSigned == false ? "Unsigned" : "Signature unknown")} · {installed.Driver.Signer ?? "Signer unavailable"} · match {installed.Confidence}% ({installed.MatchedOn})";
        DetailCategories.Text = update.Categories.Count > 0 ? string.Join(", ", update.Categories) : "—";
        DetailWarning.IsOpen = update.IsDriver;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterBox?.Text.Trim();
        var matches = string.IsNullOrWhiteSpace(filter)
            ? _allUpdates
            : _allUpdates.Where(item =>
                item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.Metadata.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.SourceLabel.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Replace(VisibleUpdates, matches);
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomCriteriaBox.Visibility = PresetCombo.SelectedItem is ScanPresetOption { Value: ScanPreset.Custom }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag as string ?? "scan";
        ScanView.Visibility = tag == "scan" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private Progress<OperationProgress> CreateProgress() => new(progress =>
    {
        StatusText.Text = progress.Message;
        ProgressText.Text = progress.Percent is null ? progress.Stage : $"{progress.Stage} · {progress.Percent}%";
    });

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        BusyRing.IsActive = busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        StatusText.Text = status;
        if (!busy) ProgressText.Text = string.Empty;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private void Log(string message)
    {
        ActivityItems.Insert(0, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}");
        while (ActivityItems.Count > 500) ActivityItems.RemoveAt(ActivityItems.Count - 1);
    }

    private static string Readable(RepairAction action) => action switch
    {
        RepairAction.StartRequiredServices => "start required services",
        RepairAction.ResetWindowsUpdateCache => "reset update cache",
        RepairAction.ScanComponentStore => "DISM ScanHealth",
        RepairAction.RestoreComponentStore => "DISM RestoreHealth",
        RepairAction.GenerateWindowsUpdateLog => "generate WindowsUpdate.log",
        _ => action.ToString()
    };

    private static string TrimForDialog(string output, string error)
    {
        var combined = string.Join(Environment.NewLine, new[] { output, error }.Where(static value => !string.IsNullOrWhiteSpace(value))).Trim();
        return combined.Length <= 4_000 ? combined : combined[..4_000] + Environment.NewLine + "…output truncated in dialog";
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> values)
    {
        destination.Clear();
        foreach (var value in values) destination.Add(value);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
