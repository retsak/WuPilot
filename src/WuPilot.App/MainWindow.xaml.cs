using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WuPilot.Core.Abstractions;
using WuPilot.Core.Models;
using WuPilot.Core.Services;
using WuPilot.Infrastructure.Windows.Diagnostics;
using WuPilot.Infrastructure.Windows.Export;
using WuPilot.Infrastructure.Windows.Profiles;
using WuPilot.Infrastructure.Windows.Management;
using WuPilot.Infrastructure.Windows.Updates;
using WuPilot.Infrastructure.Windows.Wua;

namespace WuPilot.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IUpdateScanService _scanService;
    private readonly IUpdateActionService _actionService;
    private readonly IDiagnosticService _diagnosticService;
    private readonly IUpdateHistoryProvider _historyProvider;
    private readonly IUpdateSourceDiscoveryService _sourceDiscoveryService;
    private readonly IWatchlistStore _watchlistStore;
    private readonly IEvidenceExportService _exportService;
    private readonly IScanProfileStore _profileStore;
    private readonly IWindowsUpdateSettingsService _settingsService;
    private readonly IDeliveryOptimizationService _deliveryOptimizationService;
    private readonly IOperationMetricStore _metricStore;
    private readonly IAppUpdateService _appUpdateService;
    private readonly List<UpdateListItem> _allUpdates = [];
    private readonly List<DiagnosticFindingItem> _allDiagnosticFindings = [];
    private readonly List<UpdateHistoryItem> _allUpdateHistory = [];
    private readonly List<PolicyStateItem> _allPolicyStates = [];
    private readonly List<OperationMetricItem> _allMetrics = [];
    private CancellationTokenSource? _operationCancellation;
    private ScanReport? _scanReport;
    private ScanReport? _previousScanReport;
    private ScanRequest? _lastScanRequest;
    private DiagnosticSnapshot? _diagnostics;
    private UpdateListItem? _selectedUpdate;
    private bool _isBusy;
    private bool _profilesLoaded;
    private readonly bool _isAdministrator;
    private string _resultSummary = "No scan yet";

    public ObservableCollection<ProviderOption> ProviderOptions { get; } = [];
    public ObservableCollection<ScanPresetOption> PresetOptions { get; } = [];
    public ObservableCollection<UpdateListItem> VisibleUpdates { get; } = [];
    public ObservableCollection<DiagnosticFindingItem> DiagnosticFindings { get; } = [];
    public ObservableCollection<ResultFilterOption> ResultFilterOptions { get; } = [];
    public ObservableCollection<ResultSortOption> ResultSortOptions { get; } = [];
    public ObservableCollection<DiagnosticSeverityOption> DiagnosticSeverityOptions { get; } = [];
    public ObservableCollection<ScanChangeItem> ScanChanges { get; } = [];
    public ObservableCollection<SavedScanProfileItem> SavedProfiles { get; } = [];
    public ObservableCollection<UpdateSourceRegistrationItem> RegisteredSources { get; } = [];
    public ObservableCollection<WatchedUpdateItem> WatchedUpdates { get; } = [];
    public ObservableCollection<KeyValuePair<string, string?>> ServiceStates { get; } = [];
    public ObservableCollection<KeyValuePair<string, string?>> PolicyStates { get; } = [];
    public ObservableCollection<PolicyStateItem> VisiblePolicies { get; } = [];
    public ObservableCollection<SettingAuditItem> SettingsAudit { get; } = [];
    public ObservableCollection<OperationMetricItem> VisibleMetrics { get; } = [];
    public ObservableCollection<UpdateHistoryItem> UpdateHistory { get; } = [];
    public ObservableCollection<UpdateHistoryItem> VisibleUpdateHistory { get; } = [];
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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WuPilot.ico"));
        _isAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        ElevationBadgeText.Text = _isAdministrator ? "Administrator" : "Standard user";
        AppVersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development"}";

        var identityProvider = new WindowsDeviceIdentityProvider();
        var wua = new WuaUpdateService(identityProvider, new InstalledDriverProvider());
        var historyProvider = new WuaHistoryProvider();
        _scanService = wua;
        _actionService = wua;
        _historyProvider = historyProvider;
        _sourceDiscoveryService = new WuaUpdateSourceDiscoveryService();
        _watchlistStore = new JsonWatchlistStore();
        _diagnosticService = new WindowsDiagnosticService(identityProvider, historyProvider);
        _exportService = new EvidenceExportService();
        _profileStore = new JsonScanProfileStore();
        _settingsService = new WindowsUpdateSettingsService();
        _deliveryOptimizationService = new DeliveryOptimizationService();
        _metricStore = new JsonOperationMetricStore();
        _appUpdateService = new GitHubAppUpdateService();

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

        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.All, "All results"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.Drivers, "Drivers"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.Software, "Software"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.Installed, "Installed"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.Downloaded, "Downloaded"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.Hidden, "Hidden"));
        ResultFilterOptions.Add(new ResultFilterOption(ResultFilter.RestartRequired, "Restart required"));

        ResultSortOptions.Add(new ResultSortOption(ResultSort.Default, "Drivers, then title"));
        ResultSortOptions.Add(new ResultSortOption(ResultSort.Title, "Title"));
        ResultSortOptions.Add(new ResultSortOption(ResultSort.SizeDescending, "Largest download"));
        ResultSortOptions.Add(new ResultSortOption(ResultSort.DateDescending, "Newest deployment change"));
        ResultSortOptions.Add(new ResultSortOption(ResultSort.Severity, "Highest severity"));

        DiagnosticSeverityOptions.Add(new DiagnosticSeverityOption(null, "All severities"));
        DiagnosticSeverityOptions.Add(new DiagnosticSeverityOption(DiagnosticSeverity.Error, "Errors"));
        DiagnosticSeverityOptions.Add(new DiagnosticSeverityOption(DiagnosticSeverity.Warning, "Warnings"));
        DiagnosticSeverityOptions.Add(new DiagnosticSeverityOption(DiagnosticSeverity.Information, "Information"));

        Log($"WuPilot started as {(_isAdministrator ? "administrator" : "standard user")}. No device changes have been made.");
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_profilesLoaded) return;
        _profilesLoaded = true;
        try
        {
            await ReloadProfilesAsync();
            await ReloadWatchlistAsync();
            _ = CheckForAppUpdateAsync(force: false);
        }
        catch (Exception exception)
        {
            Log($"Saved profiles could not be loaded: {exception.Message}");
            StatusText.Text = "Saved profiles unavailable";
        }
    }

    private async Task ReloadProfilesAsync(Guid? selectedProfileId = null)
    {
        var profiles = await _profileStore.GetAllAsync(CancellationToken.None);
        Replace(SavedProfiles, profiles.Select(static profile => new SavedScanProfileItem(profile)));
        if (selectedProfileId is not null)
        {
            SavedProfileCombo.SelectedItem = SavedProfiles.FirstOrDefault(item => item.Profile.Id == selectedProfileId);
        }
    }

    private async Task ReloadWatchlistAsync()
    {
        var watched = await _watchlistStore.GetAllAsync(CancellationToken.None);
        Replace(WatchedUpdates, watched.Select(static update => new WatchedUpdateItem(update)));
        UpdateWatchlistSummary();
    }

    private void ApplySavedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfileCombo.SelectedItem is not SavedScanProfileItem selectedProfile) return;
        var profile = selectedProfile.Profile;

        foreach (var option in ProviderOptions)
        {
            option.IsSelected = profile.ProviderIds.Contains(option.Provider.Id, StringComparer.OrdinalIgnoreCase);
        }

        PresetCombo.SelectedItem = PresetOptions.FirstOrDefault(option => option.Value == profile.Preset);
        CustomCriteriaBox.Text = profile.CustomCriteria ?? string.Empty;
        SupersededCheck.IsChecked = profile.IncludePotentiallySuperseded;
        CustomServiceIdBox.Text = profile.CustomServiceId ?? string.Empty;
        OfflineCabPathBox.Text = profile.OfflineCatalogPath ?? string.Empty;
        StatusText.Text = $"Applied profile: {profile.Name}";
        Log($"Applied saved scan profile '{profile.Name}'.");
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "Profile name",
            Text = (SavedProfileCombo.SelectedItem as SavedScanProfileItem)?.Name ?? string.Empty,
            PlaceholderText = "Example: Driver review"
        };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Save scan profile",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (PresetCombo.SelectedItem is not ScanPresetOption preset) return;

        try
        {
            var existing = SavedProfiles.Select(static item => item.Profile).FirstOrDefault(profile =>
                string.Equals(profile.Name, nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            var profile = SavedScanProfile.Create(
                nameBox.Text,
                ProviderOptions.Where(static option => option.IsSelected).Select(static option => option.Provider.Id),
                preset.Value,
                preset.Value == ScanPreset.Custom ? CustomCriteriaBox.Text : null,
                SupersededCheck.IsChecked == true,
                CustomServiceIdBox.Text,
                OfflineCabPathBox.Text,
                existing?.Id);
            await _profileStore.SaveAsync(profile, CancellationToken.None);
            await ReloadProfilesAsync(profile.Id);
            StatusText.Text = $"Saved profile: {profile.Name}";
            Log($"Saved scan profile '{profile.Name}'.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Profile could not be saved", exception.Message);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfileCombo.SelectedItem is not SavedScanProfileItem selectedProfile) return;
        var profile = selectedProfile.Profile;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Delete '{profile.Name}'?",
            Content = "This removes only the saved scan configuration. It does not change Windows Update settings or scan evidence.",
            PrimaryButtonText = "Delete profile",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _profileStore.DeleteAsync(profile.Id, CancellationToken.None);
        await ReloadProfilesAsync();
        StatusText.Text = "Saved profile deleted";
        Log($"Deleted scan profile '{profile.Name}'.");
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
        _lastScanRequest = request;

        SetBusy(true, "Starting Windows Update Agent scan…");
        _operationCancellation = new CancellationTokenSource();
        var progress = CreateProgress();
        Log($"Scan started: {selectedPreset.DisplayName}; sources: {string.Join(", ", providers.Select(static provider => provider.DisplayName))}.");
        try
        {
            var completedReport = await _scanService.ScanAsync(request, progress, _operationCancellation.Token);
            _previousScanReport = _scanReport;
            _scanReport = completedReport;
            _allUpdates.Clear();
            _allUpdates.AddRange(_scanReport.Updates.Select(static update => new UpdateListItem(update)));
            ApplyFilter();
            UpdateScanInsights(_scanReport);
            UpdateScanComparison(_previousScanReport, _scanReport);
            await RefreshWatchlistFromScanAsync(_scanReport);
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

    private async void Export_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(null);

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var selectedUpdates = UpdatesList.SelectedItems.OfType<UpdateListItem>().Select(static item => item.Update).ToArray();
        if (selectedUpdates.Length == 0) return;
        await ExportAsync(selectedUpdates);
    }

    private async Task ExportAsync(IEnumerable<UpdateRecord>? selection)
    {
        if (_isBusy) return;
        if (_scanReport is null) return;
        SetBusy(true, "Creating evidence bundle…");
        try
        {
            var selectedUpdates = selection?.ToArray();
            var reportForExport = _scanReport with
            {
                TechnicianNotes = string.IsNullOrWhiteSpace(TechnicianNotesBox.Text)
                    ? null
                    : TechnicianNotesBox.Text.Trim()
            };
            var directory = await _exportService.ExportAsync(reportForExport, _diagnostics, selectedUpdates, CancellationToken.None);
            Log($"Evidence bundle exported: {directory}");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Evidence bundle created",
                Content = $"{(selectedUpdates is null ? "The scan report" : $"{selectedUpdates.Length} selected update(s)")}, driver CSV, Intune review page, and available diagnostics were saved to:\n\n{directory}",
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
            _allDiagnosticFindings.Clear();
            _allDiagnosticFindings.AddRange(_diagnostics.Findings.Select(static finding => new DiagnosticFindingItem(finding)));
            ApplyDiagnosticFilter();
            Replace(ServiceStates, _diagnostics.Services.OrderBy(static pair => pair.Key));
            Replace(PolicyStates, _diagnostics.Policies.Where(static pair => pair.Value is not null).OrderBy(static pair => pair.Key));
            var historyItems = (_diagnostics.UpdateHistory ?? []).Select(static record => new UpdateHistoryItem(record)).ToArray();
            Replace(UpdateHistory, historyItems.Take(25));
            _allUpdateHistory.Clear();
            _allUpdateHistory.AddRange(historyItems);
            ApplyHistoryFilter();
            DiagnosticSummaryText.Text = BuildDiagnosticSummary(_diagnostics);
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
            await _metricStore.SaveAsync(new OperationMetric(
                Guid.NewGuid(), result.CompletedAt - result.TotalDuration, result.CompletedAt, action.ToString(),
                update.UpdateId, update.RevisionNumber, update.Title, result.DownloadBytes, result.RevalidationDuration,
                result.DownloadDuration, result.InstallDuration, result.TotalDuration, result.ResultCode, result.HResult,
                result.RebootRequired), CancellationToken.None);
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
        var selectedItems = UpdatesList.SelectedItems.OfType<UpdateListItem>().ToArray();
        _selectedUpdate = selectedItems.Length == 1 ? selectedItems[0] : null;
        var update = _selectedUpdate?.Update;
        var selectedProvider = update is null
            ? null
            : _scanReport?.ProviderResults.Select(static result => result.Provider).FirstOrDefault(provider => provider.Id == update.PrimaryProviderId);
        var actionable = selectedProvider is not null && selectedProvider.ScanPackagePath is null;
        var enabled = update is not null;
        DownloadButton.IsEnabled = enabled && actionable && update!.IsInstalled == false;
        InstallButton.IsEnabled = enabled && actionable && update!.IsInstalled == false;
        HideButton.IsEnabled = enabled && actionable;
        CopyDetailsButton.IsEnabled = enabled;
        OpenSupportButton.IsEnabled = enabled && TryHttpUri(update!.SupportUrl, out _);
        WatchUpdateButton.IsEnabled = enabled;
        WatchUpdateButton.Content = enabled && WatchedUpdates.Any(item =>
            string.Equals(item.Update.UpdateId, update!.UpdateId, StringComparison.OrdinalIgnoreCase))
            ? "Remove from watchlist"
            : "Add to watchlist";
        ExportSelectedButton.IsEnabled = selectedItems.Length > 0;
        if (update is null)
        {
            DetailTitle.Text = selectedItems.Length > 1
                ? $"{selectedItems.Length} updates selected for evidence export."
                : "Select an update to inspect its evidence.";
            DetailDescription.Text = selectedItems.Length > 1
                ? "Mutation actions remain disabled until exactly one update is selected."
                : string.Empty;
            DetailWarning.IsOpen = false;
            return;
        }

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
    private void ResultFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void ResultSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ClearResultFilters_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = string.Empty;
        ResultFilterCombo.SelectedIndex = 0;
        ResultSortCombo.SelectedIndex = 0;
        ApplyFilter();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        UpdatesList.SelectAll();
        StatusText.Text = $"{UpdatesList.SelectedItems.Count} visible updates selected";
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        UpdatesList.SelectedItems.Clear();
        StatusText.Text = "Update selection cleared";
    }

    private void ApplyFilter()
    {
        var filter = FilterBox?.Text.Trim();
        IEnumerable<UpdateListItem> matches = _allUpdates;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(item =>
                item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.Metadata.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.SourceLabel.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.Update.KbArticleIds.Any(kb => kb.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                item.Update.CveIds.Any(cve => cve.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        var resultFilter = (ResultFilterCombo?.SelectedItem as ResultFilterOption)?.Value ?? ResultFilter.All;
        matches = resultFilter switch
        {
            ResultFilter.Drivers => matches.Where(static item => item.Update.IsDriver),
            ResultFilter.Software => matches.Where(static item => !item.Update.IsDriver),
            ResultFilter.Installed => matches.Where(static item => item.Update.IsInstalled),
            ResultFilter.Downloaded => matches.Where(static item => item.Update.IsDownloaded),
            ResultFilter.Hidden => matches.Where(static item => item.Update.IsHidden),
            ResultFilter.RestartRequired => matches.Where(static item => item.Update.RebootRequired == true),
            _ => matches
        };

        var resultSort = (ResultSortCombo?.SelectedItem as ResultSortOption)?.Value ?? ResultSort.Default;
        matches = resultSort switch
        {
            ResultSort.Title => matches.OrderBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            ResultSort.SizeDescending => matches.OrderByDescending(static item => item.Update.MaximumDownloadBytes ?? -1)
                .ThenBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            ResultSort.DateDescending => matches.OrderByDescending(static item => item.Update.LastDeploymentChangeTime ?? DateTimeOffset.MinValue)
                .ThenBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            ResultSort.Severity => matches.OrderByDescending(static item => SeverityRank(item.Update.MsrcSeverity))
                .ThenBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => matches.OrderByDescending(static item => item.Update.IsDriver)
                .ThenBy(static item => item.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        Replace(VisibleUpdates, matches);
        if (VisibleResultCountText is not null)
        {
            VisibleResultCountText.Text = $"{VisibleUpdates.Count} shown";
        }
    }

    private void ProviderShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string shortcut }) return;

        foreach (var option in ProviderOptions)
        {
            option.IsSelected = shortcut switch
            {
                "Policy" => option.Provider.Id == "default",
                "Microsoft" => option.Provider.Id is "windows-update" or "microsoft-update" or "store",
                "All" => true,
                "Clear" => false,
                _ => option.IsSelected
            };
        }
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUpdate is null) return;
        var update = _selectedUpdate.Update;
        var driver = update.Driver;
        var installed = driver?.InstalledMatch;
        CopyText(string.Join(Environment.NewLine,
        [
            update.Title,
            $"Identity: {update.UpdateId}.{update.RevisionNumber}",
            $"Type: {update.Kind}",
            $"Sources: {string.Join(", ", update.ProviderNames)}",
            $"KBs: {string.Join(", ", update.KbArticleIds)}",
            $"CVEs: {string.Join(", ", update.CveIds)}",
            $"Severity: {update.MsrcSeverity ?? "Not specified"}",
            $"Downloaded: {update.IsDownloaded}; Installed: {update.IsInstalled}; Hidden: {update.IsHidden}",
            $"Restart required: {update.RebootRequired?.ToString() ?? "Unknown"}",
            $"Driver manufacturer/provider: {driver?.Manufacturer ?? "—"} / {driver?.Provider ?? "—"}",
            $"Driver model/class: {driver?.Model ?? "—"} / {driver?.DriverClass ?? "—"}",
            $"Hardware ID: {driver?.HardwareId ?? "—"}",
            $"Offered version/date: {DriverVersionParser.InferFromTitle(update.Title) ?? "—"} / {driver?.VersionDate?.ToString("yyyy-MM-dd") ?? "—"}",
            $"Installed match: {installed?.Driver.DeviceName ?? "—"} / {installed?.Driver.DriverVersion ?? "—"} / confidence {installed?.Confidence.ToString() ?? "—"}"
        ]));
        StatusText.Text = "Update details copied";
    }

    private void OpenSupport_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUpdate is null || !TryHttpUri(_selectedUpdate.Update.SupportUrl, out var uri)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void ToggleWatch_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedUpdate is null) return;
        var update = _selectedUpdate.Update;
        var existing = WatchedUpdates.FirstOrDefault(item =>
            string.Equals(item.Update.UpdateId, update.UpdateId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            await _watchlistStore.SaveAsync(WatchedUpdate.FromUpdate(update), CancellationToken.None);
            Log($"Added '{update.Title}' to the watchlist.");
            StatusText.Text = "Update added to watchlist";
        }
        else
        {
            await _watchlistStore.DeleteAsync(update.UpdateId, CancellationToken.None);
            Log($"Removed '{update.Title}' from the watchlist.");
            StatusText.Text = "Update removed from watchlist";
        }

        await ReloadWatchlistAsync();
        WatchUpdateButton.Content = existing is null ? "Remove from watchlist" : "Add to watchlist";
    }

    private async Task RefreshWatchlistFromScanAsync(ScanReport report)
    {
        var watched = await _watchlistStore.GetAllAsync(CancellationToken.None);
        if (watched.Count == 0) return;

        var refreshed = WatchlistTracker.Refresh(watched, report);
        await _watchlistStore.SaveAllAsync(refreshed, CancellationToken.None);
        Replace(WatchedUpdates, refreshed.Select(static update => new WatchedUpdateItem(update)));
        UpdateWatchlistSummary();
        Log($"Watchlist refreshed against scan {report.ScanId}: {refreshed.Count(static item => item.IsOfferedInLastScan == true)} currently offered.");
    }

    private async void RemoveWatched_Click(object sender, RoutedEventArgs e)
    {
        if (WatchlistList.SelectedItem is not WatchedUpdateItem selected) return;
        await _watchlistStore.DeleteAsync(selected.Update.UpdateId, CancellationToken.None);
        await ReloadWatchlistAsync();
        StatusText.Text = "Watchlist item removed";
        Log($"Removed '{selected.Title}' from the watchlist.");
    }

    private void CopyWatchlist_Click(object sender, RoutedEventArgs e)
    {
        var lines = new[] { "Title\tIdentity\tStatus\tState\tSources" }
            .Concat(WatchedUpdates.Select(static item =>
                $"{item.Title}\t{item.Identity}\t{item.StatusLabel}\t{item.StateLabel}\t{item.Sources}"));
        CopyText(string.Join(Environment.NewLine, lines));
        StatusText.Text = $"{WatchedUpdates.Count} watchlist items copied";
    }

    private void UpdateWatchlistSummary()
    {
        if (WatchlistSummaryText is null) return;
        WatchlistSummaryText.Text = $"{WatchedUpdates.Count} watched · {WatchedUpdates.Count(static item => item.Update.IsOfferedInLastScan == true)} offered in latest scan · {WatchedUpdates.Count(static item => item.Update.IsOfferedInLastScan == false)} no longer offered";
    }

    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        CopyText(string.Join(Environment.NewLine, ActivityItems.Reverse()));
        StatusText.Text = "Activity copied";
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        ActivityItems.Clear();
        Log("Activity view cleared. Exported evidence was not changed.");
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string theme }) return;
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        StatusText.Text = $"Theme: {theme}";
    }

    private void DiagnosticFilter_Changed(object sender, RoutedEventArgs e) => ApplyDiagnosticFilter();
    private void DiagnosticSeverity_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyDiagnosticFilter();

    private void ApplyDiagnosticFilter()
    {
        var filter = DiagnosticFilterBox?.Text.Trim();
        var severity = (DiagnosticSeverityCombo?.SelectedItem as DiagnosticSeverityOption)?.Value;
        var matches = _allDiagnosticFindings.Where(item =>
            (severity is null || item.Finding.Severity == severity) &&
            (string.IsNullOrWhiteSpace(filter) ||
             item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             item.Summary.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             (item.Recommendation?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false)));
        Replace(DiagnosticFindings, matches);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostics is null) return;
        var lines = new List<string>
        {
            BuildDiagnosticSummary(_diagnostics),
            $"Snapshot: {_diagnostics.SnapshotId}",
            $"Collected: {_diagnostics.CollectedAt:O}",
            string.Empty
        };
        lines.AddRange(_diagnostics.Findings.Select(static finding =>
            $"[{finding.Severity}] {finding.Title}: {finding.Summary} {finding.Recommendation}".Trim()));
        CopyText(string.Join(Environment.NewLine, lines));
        StatusText.Text = "Diagnostic summary copied";
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Reading Windows Update history…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var records = await _historyProvider.GetRecentHistoryAsync(500, _operationCancellation.Token);
            _allUpdateHistory.Clear();
            _allUpdateHistory.AddRange(records.Select(static record => new UpdateHistoryItem(record)));
            Replace(UpdateHistory, _allUpdateHistory.Take(25));
            ApplyHistoryFilter();
            HistorySummaryText.Text = $"{_allUpdateHistory.Count} history events · {_allUpdateHistory.Count(static item => item.ResultCode is 3 or 4 or 5)} failures or partial results";
            Log($"Loaded {_allUpdateHistory.Count} Windows Update history events.");
        }
        catch (OperationCanceledException)
        {
            Log("Update history refresh cancelled.");
        }
        catch (Exception exception)
        {
            Log($"Update history refresh failed: {exception.Message}");
            await ShowMessageAsync("History refresh failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private async void RetryFailedSources_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _scanReport is null || _lastScanRequest is null) return;
        var failedProviders = _scanReport.ProviderResults
            .Where(static result => !result.Succeeded)
            .Select(static result => result.Provider)
            .ToArray();
        if (failedProviders.Length == 0) return;

        SetBusy(true, "Retrying failed sources…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var retryRequest = _lastScanRequest with { Providers = failedProviders };
            var retryReport = await _scanService.ScanAsync(retryRequest, CreateProgress(), _operationCancellation.Token);
            var combinedReport = ScanReportRetryMerger.Combine(
                _scanReport,
                retryReport,
                failedProviders.Select(static provider => provider.Id));

            _previousScanReport = _scanReport;
            _scanReport = combinedReport;
            _allUpdates.Clear();
            _allUpdates.AddRange(combinedReport.Updates.Select(static update => new UpdateListItem(update)));
            ApplyFilter();
            UpdateScanInsights(combinedReport);
            UpdateScanComparison(_previousScanReport, combinedReport);
            await RefreshWatchlistFromScanAsync(combinedReport);
            OnPropertyChanged(nameof(HasScanReport));
            Log($"Retried {failedProviders.Length} failed sources; {combinedReport.FailedProviderCount} remain failed.");

            var remainingFailures = combinedReport.ProviderResults.Where(static result => !result.Succeeded).ToArray();
            if (remainingFailures.Length > 0)
            {
                await ShowMessageAsync(
                    "Some sources still failed",
                    string.Join(Environment.NewLine + Environment.NewLine, remainingFailures.Select(static result =>
                        $"{result.Provider.DisplayName}: {result.ErrorCode} {result.ErrorMessage}")));
            }
        }
        catch (OperationCanceledException)
        {
            Log("Failed-source retry cancelled.");
        }
        catch (Exception exception)
        {
            Log($"Failed-source retry failed: {exception.Message}");
            await ShowMessageAsync("Retry failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void HistoryFilter_Changed(object sender, RoutedEventArgs e) => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        var filter = HistoryFilterBox?.Text.Trim();
        var failuresOnly = HistoryFailuresOnlyCheck?.IsChecked == true;
        var matches = _allUpdateHistory.Where(item =>
            (!failuresOnly || item.ResultCode is 3 or 4 or 5) &&
            (string.IsNullOrWhiteSpace(filter) ||
             (item.Title?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
             item.HResultLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
             item.SourceLabel.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             (item.Record.UpdateId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)));
        Replace(VisibleUpdateHistory, matches);
        if (VisibleHistoryCountText is not null)
        {
            VisibleHistoryCountText.Text = $"{VisibleUpdateHistory.Count} shown";
        }
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        var lines = new[] { "Date\tResult\tHRESULT\tOperation\tTitle\tSource" }
            .Concat(VisibleUpdateHistory.Select(static item =>
                $"{item.DateLabel}\t{item.ResultLabel}\t{item.HResultLabel}\t{item.OperationLabel}\t{item.Title}\t{item.SourceLabel}"));
        CopyText(string.Join(Environment.NewLine, lines));
        StatusText.Text = $"{VisibleUpdateHistory.Count} history events copied";
    }

    private async void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Reading registered WUA sources…");
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var sources = await _sourceDiscoveryService.GetRegisteredSourcesAsync(_operationCancellation.Token);
            Replace(RegisteredSources, sources.Select(static source => new UpdateSourceRegistrationItem(source)));
            RegisteredSourcesSummaryText.Text = $"{sources.Count} registered services · {sources.Count(static source => source.OffersWindowsUpdates)} offer Windows updates · {sources.Count(static source => source.IsManaged)} managed";
            Log($"Loaded {sources.Count} registered Windows Update Agent services.");
        }
        catch (OperationCanceledException)
        {
            Log("Registered source inventory cancelled.");
        }
        catch (Exception exception)
        {
            Log($"Registered source inventory failed: {exception.Message}");
            await ShowMessageAsync("Source inventory failed", exception.Message);
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void CopySources_Click(object sender, RoutedEventArgs e)
    {
        var lines = new[] { "Name\tService ID\tRole\tCapability" }
            .Concat(RegisteredSources.Select(static item =>
                $"{item.Name}\t{item.ServiceId}\t{item.RoleLabel}\t{item.CapabilityLabel}"));
        CopyText(string.Join(Environment.NewLine, lines));
        StatusText.Text = $"{RegisteredSources.Count} registered sources copied";
    }

    private void UseSourceForScan_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredSourcesList.SelectedItem is not UpdateSourceRegistrationItem selected) return;
        foreach (var option in ProviderOptions)
        {
            option.IsSelected = false;
        }
        CustomServiceIdBox.Text = selected.ServiceId;
        Navigation.SelectedItem = ScanNav;
        ShowView("scan");
        StatusText.Text = $"Custom scan source set to {selected.Name}";
        Log($"Selected registered WUA service '{selected.Name}' for a custom read-only scan.");
    }

    private void OpenWindowsUpdate_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });

    private void ShowComparison_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = CompareNav;
        ShowView("compare");
    }

    private void ReturnToScan_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = ScanNav;
        ShowView("scan");
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomCriteriaBox is null) return;

        CustomCriteriaBox.Visibility = PresetCombo.SelectedItem is ScanPresetOption { Value: ScanPreset.Custom }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ScanView is null || CompareView is null || WatchlistView is null || SourcesView is null || ControlsView is null || PerformanceView is null || DiagnosticsView is null || HistoryView is null || ActivityView is null || AboutView is null) return;

        var tag = args.SelectedItemContainer?.Tag as string ?? "scan";
        ShowView(tag);
    }

    private void ShowView(string tag)
    {
        ScanView.Visibility = tag == "scan" ? Visibility.Visible : Visibility.Collapsed;
        CompareView.Visibility = tag == "compare" ? Visibility.Visible : Visibility.Collapsed;
        WatchlistView.Visibility = tag == "watchlist" ? Visibility.Visible : Visibility.Collapsed;
        SourcesView.Visibility = tag == "sources" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsView.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        ControlsView.Visibility = tag == "controls" ? Visibility.Visible : Visibility.Collapsed;
        PerformanceView.Visibility = tag == "performance" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "controls" && _allPolicyStates.Count == 0) _ = RefreshSettingsAsync();
        if (tag == "performance") _ = RefreshPerformanceAsync();
    }

    private void UpdateScanInsights(ScanReport report)
    {
        var insights = ScanReportAnalyzer.BuildInsights(report);
        ResultSummary = $"{insights.TotalUpdates} updates · {insights.DriverUpdates} drivers · {insights.FailedProviders} source failures";
        InsightCountsText.Text = $"{insights.SoftwareUpdates} software · {insights.InstalledUpdates} installed · {insights.DownloadedUpdates} downloaded · {insights.HiddenUpdates} hidden";
        InsightSafetyText.Text = $"{insights.MandatoryUpdates} mandatory · {insights.RebootRequiredUpdates} may require restart · {insights.SuccessfulProviders}/{report.ProviderResults.Count} sources succeeded";
        InsightSizeText.Text = $"{FormatBytes(insights.KnownMaximumDownloadBytes)} known maximum download · {insights.UpdatesWithUnknownSize} unknown sizes · {FormatDuration(insights.Duration)}";
        RetryFailedButton.IsEnabled = insights.FailedProviders > 0;
        ScanInsightCard.Visibility = Visibility.Visible;
    }

    private void UpdateScanComparison(ScanReport? previous, ScanReport current)
    {
        ScanChanges.Clear();
        if (previous is null)
        {
            ComparisonSummaryText.Text = "Run another scan to compare newly offered, removed, revised, or state-changed updates.";
            ComparisonPreviewText.Text = "Run another scan to enable comparison.";
            ComparisonContextText.Text = "No previous scan is available in this session.";
            ComparisonEmptyText.Visibility = Visibility.Visible;
            ViewChangesButton.IsEnabled = false;
            return;
        }

        var comparison = ScanReportAnalyzer.Compare(previous, current);
        foreach (var change in comparison.Changes.Select(static change => new ScanChangeItem(change)))
        {
            ScanChanges.Add(change);
        }

        var criteriaChanged = !string.Equals(previous.Criteria, current.Criteria, StringComparison.Ordinal);
        var previousSources = previous.ProviderResults.Select(static result => result.Provider.Id).Order(StringComparer.OrdinalIgnoreCase);
        var currentSources = current.ProviderResults.Select(static result => result.Provider.Id).Order(StringComparer.OrdinalIgnoreCase);
        var sourcesChanged = !previousSources.SequenceEqual(currentSources, StringComparer.OrdinalIgnoreCase);

        ComparisonSummaryText.Text = comparison.HasChanges
            ? $"{comparison.NewUpdates} new · {comparison.RemovedUpdates} no longer offered · {comparison.RevisionChanges} revisions · {comparison.StateChanges} state changes · {comparison.UnchangedUpdates} unchanged"
            : $"No update changes · {comparison.UnchangedUpdates} unchanged";
        ComparisonPreviewText.Text = ComparisonSummaryText.Text;
        ComparisonContextText.Text = $"Previous {previous.CompletedAt:g} → current {current.CompletedAt:g}" +
            (criteriaChanged || sourcesChanged ? " · Caution: scan criteria or sources changed." : string.Empty);
        ComparisonEmptyText.Visibility = comparison.HasChanges ? Visibility.Collapsed : Visibility.Visible;
        ViewChangesButton.IsEnabled = true;
        Log($"Scan comparison: {ComparisonSummaryText.Text}.");
    }

    private static string BuildDiagnosticSummary(DiagnosticSnapshot snapshot)
    {
        var errors = snapshot.Findings.Count(static finding => finding.Severity == DiagnosticSeverity.Error);
        var warnings = snapshot.Findings.Count(static finding => finding.Severity == DiagnosticSeverity.Warning);
        return $"{errors} errors · {warnings} warnings · {snapshot.Findings.Count - errors - warnings} informational · restart pending: {snapshot.RebootPending}";
    }

    private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 4,
        "important" => 3,
        "moderate" => 2,
        "low" => 1,
        _ => 0
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} min"
            : $"{Math.Max(0, duration.TotalSeconds):0.#} sec";

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static bool TryHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private async void RefreshSettings_Click(object sender, RoutedEventArgs e) => await RefreshSettingsAsync();

    private async Task RefreshSettingsAsync()
    {
        try
        {
            var snapshot = await _settingsService.GetSnapshotAsync(CancellationToken.None);
            _allPolicyStates.Clear();
            _allPolicyStates.AddRange(snapshot.Policies.Select(static state => new PolicyStateItem(state)));
            var audit = await _settingsService.GetAuditAsync(CancellationToken.None);
            Replace(SettingsAudit, audit.Take(25).Select(static entry => new SettingAuditItem(entry)));
            ApplyPolicyFilter();
            ControlsSummaryText.Text = $"{snapshot.Policies.Count} policies · Windows build {snapshot.WindowsBuild} · {snapshot.Policies.Count(static policy => policy.CanEdit)} locally editable";
        }
        catch (Exception exception)
        {
            ControlsSummaryText.Text = $"Settings unavailable: {exception.Message}";
            Log($"Settings refresh failed: {exception.Message}");
        }
    }

    private void PolicyFilter_Changed(object sender, RoutedEventArgs e) => ApplyPolicyFilter();
    private void PolicyFilter_Changed(object sender, TextChangedEventArgs e) => ApplyPolicyFilter();

    private void ApplyPolicyFilter()
    {
        var query = PolicyFilterBox.Text?.Trim();
        var showLegacy = ShowLegacyPolicyCheck.IsChecked == true;
        var filtered = _allPolicyStates.Where(item =>
            (showLegacy || item.State.IsSupported && !item.State.Definition.IsLegacy) &&
            (string.IsNullOrWhiteSpace(query) ||
             item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.ValueLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        Replace(VisiblePolicies, filtered.OrderBy(static item => item.Category).ThenBy(static item => item.DisplayName));
        VisiblePolicyCountText.Text = $"{VisiblePolicies.Count} shown";
    }

    private void PolicyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item)
        {
            ApplyPolicyButton.IsEnabled = ClearPolicyButton.IsEnabled = false;
            return;
        }
        PolicyDetailTitle.Text = item.DisplayName;
        PolicyDetailStatus.Text = $"{item.Description}\n\nCurrent: {item.ValueLabel}\nOwner: {item.OwnershipLabel}\n{item.Status}";
        PolicyValueBox.Text = item.State.RequestedValue ?? string.Empty;
        PolicyValueBox.PlaceholderText = item.State.Definition.Choices is null
            ? "Enter the documented value"
            : string.Join(" · ", item.State.Definition.Choices.Select(static pair => $"{pair.Key}={pair.Value}"));
        ApplyPolicyButton.IsEnabled = ClearPolicyButton.IsEnabled = item.State.CanEdit;
    }

    private async void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item) return;
        await ApplySettingChangesAsync([new(item.State.Definition.Id, PolicyValueBox.Text)], item.State.Definition.Risk);
    }

    private async void ClearPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item) return;
        await ApplySettingChangesAsync([new(item.State.Definition.Id, null, Remove: true)], item.State.Definition.Risk);
    }

    private async void QuickToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var state = _allPolicyStates.FirstOrDefault(item => item.State.Definition.Id == id);
        if (state is null || !state.State.CanEdit)
        {
            await ShowMessageAsync("Control unavailable", state?.Status ?? "Refresh settings before using quick controls.");
            return;
        }
        await ApplySettingChangesAsync([new(id, state.State.EffectiveValue == "1" ? "0" : "1")], state.State.Definition.Risk);
    }

    private async void PauseUpdates_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await ApplySettingChangesAsync([new("update.pause-quality-start", today), new("update.pause-feature-start", today)], PolicyRisk.Elevated);
    }

    private async void ResumeUpdates_Click(object sender, RoutedEventArgs e) =>
        await ApplySettingChangesAsync([new("update.pause-quality-start", null, true), new("update.pause-feature-start", null, true)], PolicyRisk.Elevated);

    private async Task ApplySettingChangesAsync(IReadOnlyList<SettingChange> changes, PolicyRisk risk)
    {
        var warning = risk == PolicyRisk.High
            ? "This can change update sources, safeguards, or user access. A bad value can prevent updates."
            : "This writes elevated device-local Windows Update policy or Settings state. Domain or MDM policy may overwrite it.";
        if (!await ConfirmAsync("Apply Windows Update setting?", $"{warning}\n\nWuPilot will journal the previous value, verify readback, and roll back the batch if any write fails.")) return;
        SetBusy(true, "Applying and verifying settings…");
        try
        {
            var result = await _settingsService.ApplyAsync(changes, CancellationToken.None);
            Log($"Settings batch {result.BatchId}: {result.Summary}");
            await RefreshSettingsAsync();
            await ShowMessageAsync(result.Succeeded ? "Settings applied" : "Settings rolled back", result.Summary);
        }
        catch (Exception exception) { await ShowMessageAsync("Settings change failed", exception.Message); }
        finally { SetBusy(false, "Ready"); }
    }

    private async void ExportSettingsAudit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _settingsService.ExportAuditAsync(CancellationToken.None);
            Log($"Settings audit exported: {path}");
            await ShowMessageAsync("Audit exported", path);
        }
        catch (Exception exception) { await ShowMessageAsync("Audit export failed", exception.Message); }
    }

    private async void RestoreSetting_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsAuditList.SelectedItem is not SettingAuditItem selected) return;
        if (!await ConfirmAsync("Restore previous value?", $"{selected.Title}\n\nWuPilot will refuse restoration if the setting drifted after this change.")) return;
        try
        {
            var result = await _settingsService.RestoreAsync(selected.Entry.Id, allowConflict: false, CancellationToken.None);
            Log($"Restored setting from audit {selected.Entry.Id}: {result.Summary}");
            await RefreshSettingsAsync();
        }
        catch (Exception exception) { await ShowMessageAsync("Restore stopped", exception.Message); }
    }

    private async void RefreshPerformance_Click(object sender, RoutedEventArgs e) => await RefreshPerformanceAsync();
    private async void PerformanceRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_profilesLoaded) return;
        await RefreshPerformanceAsync();
    }

    private async Task RefreshPerformanceAsync()
    {
        try
        {
            var telemetryTask = _deliveryOptimizationService.GetSnapshotAsync(CancellationToken.None);
            var metricsTask = _metricStore.GetAllAsync(CancellationToken.None);
            await Task.WhenAll(telemetryTask, metricsTask);
            var telemetry = await telemetryTask;
            DoDownloadedText.Text = FormatBytes(telemetry.TotalDownloaded);
            DoSavingsText.Text = $"{telemetry.PeerSavingsPercent:0.#}%";
            DoUploadedText.Text = FormatBytes(telemetry.BytesUploaded);
            DoModeText.Text = $"{telemetry.DownloadMode} · {telemetry.ActiveDownloads} active";
            DoDetailText.Text = telemetry.Error is null
                ? $"HTTP/CDN: {FormatBytes(telemetry.BytesFromHttp)}\nConnected Cache: {FormatBytes(telemetry.BytesFromCache)}\nLAN peers: {FormatBytes(telemetry.BytesFromLanPeers)}\nInternet peers: {FormatBytes(telemetry.BytesFromInternetPeers)}\nCache: {FormatBytes(telemetry.CacheBytes)}\nForeground/background limits: {telemetry.ForegroundLimit ?? "default"} / {telemetry.BackgroundLimit ?? "default"}\nSource: {telemetry.Source}"
                : $"Telemetry unavailable: {telemetry.Error}";
            var days = PerformanceRangeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var parsed) ? parsed : 30;
            _allMetrics.Clear();
            _allMetrics.AddRange((await metricsTask).Select(static metric => new OperationMetricItem(metric)));
            var visible = days == 0 ? _allMetrics : _allMetrics.Where(item => item.Metric.CompletedAt >= DateTimeOffset.Now.AddDays(-days));
            Replace(VisibleMetrics, visible);
            PerformanceSummaryText.Text = $"{VisibleMetrics.Count} retained WuPilot operations · exact monotonic total timing";
        }
        catch (Exception exception) { PerformanceSummaryText.Text = $"Performance data unavailable: {exception.Message}"; }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForAppUpdateAsync(force: true);

    private async Task CheckForAppUpdateAsync(bool force)
    {
        try
        {
            if (!force && !AutomaticUpdatesEnabled()) return;
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var release = await _appUpdateService.CheckAsync(current, force, CancellationToken.None);
            if (release is null)
            {
                if (force) await ShowMessageAsync("WuPilot is up to date", $"Installed version: {current.ToString(3)}");
                AppUpdateStatusText.Text = $"No newer stable release was found. Installed: {current.ToString(3)}";
                return;
            }
            AppUpdateStatusText.Text = $"{release.Name} is available.";
            if (!await ConfirmAsync("WuPilot update available", $"{release.Name}\nPublished {release.PublishedAt:g}\n{release.Size / 1024d / 1024d:0.0} MB\n\n{release.Notes}\n\nDownload and verify the {RuntimeArchitectureLabel()} installer?")) return;
            SetBusy(true, "Downloading WuPilot update…");
            var downloaded = await _appUpdateService.DownloadAsync(release, CreateProgress(), CancellationToken.None);
            var signatureWarning = downloaded.IsAuthenticodeSigned
                ? "The Authenticode signature is valid."
                : "This release is not Authenticode-signed. Its GitHub and sidecar SHA-256 digests match, but Windows may show an unknown-publisher warning.";
            if (!await ConfirmAsync("Install verified update?", $"SHA-256: {downloaded.Sha256}\n\n{signatureWarning}\n\nWuPilot will close and launch the installer.")) return;
            _appUpdateService.LaunchInstaller(downloaded);
            Close();
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text = $"Update check failed: {exception.Message}";
            if (force) await ShowMessageAsync("Update check failed", exception.Message);
            Log($"App update check failed: {exception.Message}");
        }
        finally { if (_isBusy) SetBusy(false, "Ready"); }
    }

    private static bool AutomaticUpdatesEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WuPilot");
        return Convert.ToInt32(key?.GetValue("AutomaticUpdateChecks", 1), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
    private static string RuntimeArchitectureLabel() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, Content = message, PrimaryButtonText = "Continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
