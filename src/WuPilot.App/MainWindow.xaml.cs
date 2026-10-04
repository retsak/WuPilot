using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
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
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly ICompletionNoticeStore _completionNoticeStore;
    private readonly IShellProgressService _shellProgressService;
    private readonly IClock _clock;
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
    private bool _syncingQuickControls;
    private bool _applyingSettings;
    private bool _readingSettings;
    private bool _historyLoaded;
    private bool _sourcesLoaded;
    private Task? _settingsRefreshTask;
    private bool _collectingLogs;
    private bool _checkingAppUpdate;
    private Task? _performanceRefreshTask;
    private bool _profilesLoaded;
    private bool _isRestoringPreferences;
    private bool _operationFailed;
    private bool _closeAfterOperation;
    private bool _allowClose;
    private bool _windowIsActive = true;
    private bool _closePromptOpen;
    private string _currentPageTag = "scan";
    private OperationStatus? _operationStatus;
    private AppPreferences _preferences = AppPreferences.Default;
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
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
    public ObservableCollection<PolicyChoiceItem> PolicyChoices { get; } = [];
    public ObservableCollection<QuickControlItem> QuickControls { get; } = [];
    public ObservableCollection<StagedPolicyChangeItem> PolicyChangeCart { get; } = [];
    public ObservableCollection<CompletionNoticeItem> CompletionNotices { get; } = [];
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
        _preferencesStore = new JsonAppPreferencesStore();
        _completionNoticeStore = new JsonCompletionNoticeStore();
        _shellProgressService = new WindowsShellProgressService();
        _clock = new SystemClock();
        foreach (var id in new[] { "update.microsoft-products", "update.latest", "update.metered", "update.restart-notifications" })
            QuickControls.Add(new QuickControlItem(PolicyCatalog.All.First(definition => definition.Id == id)));
        PolicyChangeCart.CollectionChanged += (_, _) => SyncCommandCenter();
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        Activated += MainWindow_Activated;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        _elapsedTimer.Tick += ElapsedTimer_Tick;

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
            try { _shellProgressService.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
            catch (Exception exception) { Log($"Taskbar progress is unavailable: {exception.Message}"); }
            _preferences = await _preferencesStore.GetAsync(CancellationToken.None);
            ApplyPreferences(_preferences);
            await ReloadProfilesAsync();
            await ReloadWatchlistAsync();
            await ReloadCompletionNoticesAsync();
            _ = CheckForAppUpdateAsync(force: false);
        }
        catch (Exception exception)
        {
            Log($"Saved profiles could not be loaded: {exception.Message}");
            StatusText.Text = "Saved profiles unavailable";
        }
    }

    private void ApplyPreferences(AppPreferences preferences)
    {
        _isRestoringPreferences = true;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var placement = WindowPlacementValidator.Clamp(preferences.Window, work.X, work.Y, work.Width, work.Height);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
            if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            ApplyTheme(preferences.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default });
            Navigation.IsPaneOpen = preferences.NavigationPaneOpen;
            foreach (var provider in ProviderOptions) provider.IsSelected = preferences.ScanProviderIds?.Contains(provider.Provider.Id, StringComparer.OrdinalIgnoreCase) == true;
            PresetCombo.SelectedItem = PresetOptions.FirstOrDefault(item => item.Value.ToString() == preferences.ScanPreset) ?? PresetOptions[2];
            CustomServiceIdBox.Text = preferences.CustomServiceId;
            OfflineCabPathBox.Text = preferences.OfflineCatalogPath;
            CustomCriteriaBox.Text = preferences.CustomCriteria;
            SupersededCheck.IsChecked = preferences.IncludeSuperseded;
            ResultFilterCombo.SelectedItem = ResultFilterOptions.FirstOrDefault(item => item.Value.ToString() == preferences.ResultFilter) ?? ResultFilterOptions[0];
            ResultSortCombo.SelectedItem = ResultSortOptions.FirstOrDefault(item => item.Value.ToString() == preferences.ResultSort) ?? ResultSortOptions[0];
            PerformanceRangeCombo.SelectedItem = PerformanceRangeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Convert.ToString(item.Tag) == preferences.PerformanceRangeDays.ToString());
            PolicyFilterBox.Text = preferences.PolicySearch;
            ShowLegacyPolicyCheck.IsChecked = preferences.ShowLegacyPolicies;
            TaskbarAttentionToggle.IsOn = preferences.FlashTaskbarOnCompletion;
            NavigateTo(preferences.NavigationTag);
            DispatcherQueue.TryEnqueue(RefreshPageLayout);
        }
        finally { _isRestoringPreferences = false; }
    }

    private void RefreshPageLayout()
    {
        PageScrollViewer.InvalidateMeasure();
        PageScrollViewer.InvalidateArrange();
        PageScrollViewer.UpdateLayout();
    }

    private AppPreferences CapturePreferences()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        return _preferences with
        {
            Window = new(position.X, position.Y, size.Width, size.Height, maximized),
            NavigationTag = _currentPageTag,
            NavigationPaneOpen = Navigation.IsPaneOpen,
            Theme = RootGrid.RequestedTheme switch { ElementTheme.Light => "Light", ElementTheme.Dark => "Dark", _ => "System" },
            ScanProviderIds = ProviderOptions.Where(static item => item.IsSelected).Select(static item => item.Provider.Id).ToArray(),
            ScanPreset = (PresetCombo.SelectedItem as ScanPresetOption)?.Value.ToString() ?? "MissingDrivers",
            CustomServiceId = CustomServiceIdBox.Text ?? string.Empty,
            OfflineCatalogPath = OfflineCabPathBox.Text ?? string.Empty,
            CustomCriteria = CustomCriteriaBox.Text ?? string.Empty,
            IncludeSuperseded = SupersededCheck.IsChecked == true,
            ResultFilter = (ResultFilterCombo.SelectedItem as ResultFilterOption)?.Value.ToString() ?? "All",
            ResultSort = (ResultSortCombo.SelectedItem as ResultSortOption)?.Value.ToString() ?? "Default",
            PerformanceRangeDays = SelectedPerformanceDays(),
            PolicySearch = PolicyFilterBox.Text ?? string.Empty,
            PolicyCategory = SelectedComboTag(PolicyCategoryCombo),
            PolicyOwnership = SelectedComboTag(PolicyOwnershipCombo),
            PolicyRisk = SelectedComboTag(PolicyRiskCombo),
            PolicyStateFilter = SelectedComboTag(PolicyStateFilterCombo),
            ShowLegacyPolicies = ShowLegacyPolicyCheck.IsChecked == true,
            FavoritePolicyIds = _allPolicyStates.Where(static item => item.IsFavorite).Select(static item => item.State.Definition.Id).ToArray(),
            FlashTaskbarOnCompletion = TaskbarAttentionToggle.IsOn
        };
    }

    private void SavePreferencesSoon()
    {
        if (_isRestoringPreferences || !_profilesLoaded) return;
        _preferences = CapturePreferences();
        _preferencesStore.ScheduleSave(_preferences);
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
        ResultsStateText.Text = _scanReport is null
            ? "Scanning; results will appear when all selected sources finish."
            : "Scanning; showing the previous completed result set.";
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
            ResultsStateText.Text = $"Scan completed {_scanReport.CompletedAt:g}.";
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
            ResultsStateText.Text = _scanReport is null ? "Scan cancelled; no results loaded." : "Scan cancelled; previous completed results retained.";
        }
        catch (Exception exception)
        {
            Log($"Scan failed: {exception.GetType().Name}: {exception.Message}");
            ResultsStateText.Text = _scanReport is null ? "Scan failed; no results loaded." : "Scan failed; previous completed results retained.";
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
        if (_operationStatus?.IsCancellable != true) return;
        _operationCancellation?.Cancel();
        if (_operationStatus is not null) _operationStatus = _operationStatus with { State = OperationRunState.CancellationRequested };
        _shellProgressService.SetProgress(ShellProgressState.Paused, _operationStatus?.Percent);
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
        SetBusy(true, "Creating evidence bundle…", cancellable: false);
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
        catch (OperationCanceledException)
        {
            Log("Diagnostics cancelled.");
        }
        catch (Exception exception)
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
                UpdateAction.Install => $"{update.Title}\n\nThis downloads and installs only on this test device. License terms will be accepted if required. WuPilot will not restart the device." +
                    (update.MayRequestUserInput ? "\n\nThe update metadata says its installer may request input. WuPilot will still attempt it; keep this window in the foreground and respond if Windows displays a prompt." : string.Empty),
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
            await SaveOperationMetricAsync(update, provider, action, result);
            Log($"{action} {update.UpdateId}.{update.RevisionNumber}: result {result.ResultCode}, HRESULT 0x{unchecked((uint)result.HResult):X8}, reboot={result.RebootRequired}. {result.Message}");
            if (result.HResult == unchecked((int)0x80240020))
            {
                await ShowInteractiveInstallFailureAsync(update);
            }
            else
            {
                var explanation = result.HResult == 0 ? string.Empty : $"\n{HResultCatalog.Explain(result.HResult).Explanation}";
                await ShowMessageAsync(result.Succeeded ? $"{action} completed" : $"{action} failed", $"{result.Message}{explanation}\n\nResult: {result.ResultCode}\nHRESULT: 0x{unchecked((uint)result.HResult):X8}\nRestart required: {result.RebootRequired}\n\nRe-scan only if you need refreshed applicability and state.");
            }
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

    private async void InstallAll_Click(object sender, RoutedEventArgs e) =>
        await RunBulkActionAsync(_allUpdates.Where(static item => !item.Update.IsInstalled).ToArray(), UpdateAction.Install, "all applicable");

    private async void InstallSelected_Click(object sender, RoutedEventArgs e) =>
        await RunBulkActionAsync(UpdatesList.SelectedItems.OfType<UpdateListItem>().ToArray(), UpdateAction.Install, "selected");

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        await RunBulkActionAsync(UpdatesList.SelectedItems.OfType<UpdateListItem>().ToArray(), UpdateAction.Download, "selected");

    private async Task RunBulkActionAsync(IReadOnlyList<UpdateListItem> candidates, UpdateAction action, string scope)
    {
        if (_isBusy || candidates.Count == 0) return;

        var promptCapable = action == UpdateAction.Install
            ? candidates.Where(static item => item.Update.MayRequestUserInput).ToArray()
            : Array.Empty<UpdateListItem>();
        var runnable = candidates
            .Where(item => !item.Update.IsInstalled)
            .Select(item => (Item: item, Provider: FindProvider(item.Update)))
            .Where(static pair => pair.Provider is not null && pair.Provider.ScanPackagePath is null)
            .Select(static pair => (pair.Item, Provider: pair.Provider!))
            .ToArray();

        if (runnable.Length == 0)
        {
            await ShowMessageAsync($"{action} unavailable", "None of these updates can use this action from their original scan source.");
            return;
        }

        var skipped = candidates.Count - runnable.Length;
        var summary = $"{action} {runnable.Length} {scope} update(s) on this device?\n\n" +
            (promptCapable.Length > 0 ? $"{promptCapable.Length} update(s) are marked as capable of requesting input. They will be attempted, not excluded; keep WuPilot in the foreground for any Windows prompts.\n" : string.Empty) +
            (skipped > 0 ? $"{skipped} update(s) are already installed or have a source that cannot perform this action.\n" : string.Empty) +
            "\nWuPilot will process the eligible updates sequentially and will not restart the device.";
        if (!await ConfirmAsync($"Confirm bulk {action.ToString().ToLowerInvariant()}", summary)) return;

        SetBusy(true, $"{action}ing {runnable.Length} updates…");
        InstallAllButton.IsEnabled = InstallSelectedButton.IsEnabled = DownloadSelectedButton.IsEnabled = false;
        _operationCancellation = new CancellationTokenSource();
        var succeeded = 0;
        var failed = 0;
        try
        {
            for (var index = 0; index < runnable.Length; index++)
            {
                _operationCancellation.Token.ThrowIfCancellationRequested();
                var (item, provider) = runnable[index];
                StatusText.Text = $"{action} {index + 1} of {runnable.Length}: {item.Title}";
                var result = await _actionService.ExecuteAsync(
                    new UpdateActionRequest(item.Update, provider, action, AcceptEula: true),
                    CreateProgress(),
                    _operationCancellation.Token);
                await SaveOperationMetricAsync(item.Update, provider, action, result);
                if (result.Succeeded) succeeded++; else failed++;
                Log($"Bulk {action} {item.Update.IdentityKey}: result {result.ResultCode}, HRESULT 0x{unchecked((uint)result.HResult):X8}. {result.Message}");
            }

            var title = failed == 0 ? $"Bulk {action} completed" : $"Bulk {action} completed with failures";
            await ShowMessageAsync(title,
                $"{succeeded} succeeded · {failed} failed · {skipped} excluded.\n\n" +
                "Re-scan when you want to refresh applicability and state.");
        }
        catch (OperationCanceledException)
        {
            Log($"Bulk {action} cancelled after {succeeded + failed} of {runnable.Length} updates.");
        }
        finally
        {
            SetBusy(false, "Ready");
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            UpdateBulkActionState();
        }
    }

    private UpdateProviderDefinition? FindProvider(UpdateRecord update) =>
        _scanReport?.ProviderResults.Select(static result => result.Provider).FirstOrDefault(provider => provider.Id == update.PrimaryProviderId)
        ?? ProviderOptions.Select(static option => option.Provider).FirstOrDefault(provider => provider.Id == update.PrimaryProviderId);

    private async Task SaveOperationMetricAsync(UpdateRecord update, UpdateProviderDefinition provider, UpdateAction action, UpdateActionResult result)
    {
        await _metricStore.SaveAsync(new OperationMetric(
            Guid.NewGuid(), result.CompletedAt - result.TotalDuration, result.CompletedAt, action.ToString(),
            update.UpdateId, update.RevisionNumber, update.Title, result.DownloadBytes, result.RevalidationDuration,
            result.DownloadDuration, result.InstallDuration, result.TotalDuration, result.ResultCode, result.HResult,
            result.RebootRequired, UpdateSource: provider.DisplayName,
            InstallationMethod: action == UpdateAction.Install ? "WuPilot WUA installer with source prompts enabled" : $"WUA {action}",
            HardwareId: update.Driver?.HardwareId, MayRequestUserInput: update.CanRequestUserInput), CancellationToken.None);
    }

    private async Task ShowInteractiveInstallFailureAsync(UpdateRecord update)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Installation needs an interactive user",
            Content = $"{update.Title}\n\nWuPilot attempted this installation, but Windows returned HRESULT 0x80240020 (WU_E_NO_INTERACTIVE_USER). Microsoft defines this as no interactive user being available to finish the operation.\n\nRetry while signed in and keep WuPilot in the foreground, or continue through Windows Update or the OEM support tool.",
            PrimaryButtonText = "Open Windows Update",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Launcher.LaunchUriAsync(new Uri("ms-settings:windowsupdate"));
    }

    private void UpdateBulkActionState()
    {
        if (InstallAllButton is null) return;
        var selected = UpdatesList?.SelectedItems.OfType<UpdateListItem>().ToArray() ?? [];
        InstallAllButton.IsEnabled = !_isBusy && _allUpdates.Any(static item => !item.Update.IsInstalled);
        InstallSelectedButton.IsEnabled = !_isBusy && selected.Any(static item => !item.Update.IsInstalled);
        DownloadSelectedButton.IsEnabled = !_isBusy && selected.Any(static item => !item.Update.IsInstalled);
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
        InstallButton.Content = "Install";
        HideButton.IsEnabled = enabled && actionable;
        CopyDetailsButton.IsEnabled = enabled;
        OpenSupportButton.IsEnabled = enabled && TryHttpUri(update!.SupportUrl, out _);
        WatchUpdateButton.IsEnabled = enabled;
        WatchUpdateButton.Content = enabled && WatchedUpdates.Any(item =>
            string.Equals(item.Update.UpdateId, update!.UpdateId, StringComparison.OrdinalIgnoreCase))
            ? "Remove from watchlist"
            : "Add to watchlist";
        ExportSelectedButton.IsEnabled = selectedItems.Length > 0;
        SelectedResultCountText.Text = $"{selectedItems.Length} selected";
        UpdateBulkActionState();
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
        DetailWarning.Title = update.MayRequestUserInput ? "Installer may request input" : "Review before installation";
        DetailWarning.Message = update.MayRequestUserInput
            ? "This metadata flag is a caution, not a block. WuPilot will attempt the update and allow Windows source prompts; keep the window in the foreground."
            : "Direct installation tests this device only. Use the evidence bundle and Intune deployment rings for broad rollout.";
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) { ApplyFilter(); SavePreferencesSoon(); }
    private void ResultFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { ApplyFilter(); SavePreferencesSoon(); }
    private void ResultSort_SelectionChanged(object sender, SelectionChangedEventArgs e) { ApplyFilter(); SavePreferencesSoon(); }

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
        UpdateBulkActionState();
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
        WatchlistSummaryText.Text = WatchedUpdates.Count == 0
            ? "Select an update in Scan and review, then choose Add to watchlist."
            : $"{WatchedUpdates.Count} watched · {WatchedUpdates.Count(static item => item.Update.IsOfferedInLastScan == true)} offered in latest scan · {WatchedUpdates.Count(static item => item.Update.IsOfferedInLastScan == false)} no longer offered";
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
        ApplyTheme(theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        });
        StatusText.Text = $"Theme: {theme}";
        SavePreferencesSoon();
    }

    private void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;

        // NavigationView owns templated pane surfaces that do not always refresh
        // inherited resources when an ancestor changes theme at runtime.
        Navigation.RequestedTheme = theme;
        AppTitleBar.RequestedTheme = theme;

        UpdateTitleBarTheme(theme == ElementTheme.Default ? RootGrid.ActualTheme : theme);
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (RootGrid.RequestedTheme == ElementTheme.Default)
            UpdateTitleBarTheme(RootGrid.ActualTheme);
    }

    private void UpdateTitleBarTheme(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.InactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
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
            _historyLoaded = true;
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
    private void HistoryRange_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilter();

    private void ResetHistoryFilters_Click(object sender, RoutedEventArgs e)
    {
        HistoryFilterBox.Text = string.Empty;
        HistoryFailuresOnlyCheck.IsChecked = false;
        HistoryRangeCombo.SelectedIndex = 0;
        HistoryOperationCombo.SelectedIndex = 0;
        ApplyHistoryFilter();
    }

    private void HistorySelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateHistoryDetails();

    private void UpdateHistoryDetails()
    {
        if (HistoryList is null || HistoryDetailsText is null || CopyHistoryDetailsButton is null) return;
        var selected = HistoryList.SelectedItem as UpdateHistoryItem;
        var record = selected?.Record;
        HistoryDetailsText.Text = record is null ? "Select an event to review its description and error guidance." :
            $"{record.Title ?? "Title unavailable"}\n{record.Date?.ToString("g") ?? "Date unavailable"} · {UpdateHistoryAnalyzer.OperationLabel(record.Operation)} · {selected!.ResultLabel}\nUpdate ID: {record.UpdateId ?? "Unavailable"} · Revision: {record.RevisionNumber?.ToString() ?? "Unavailable"}\nClient: {record.ClientApplicationId ?? "Unavailable"} · Service: {record.ServiceId ?? "Unavailable"}\n{record.Description}\n\n{UpdateHistoryAnalyzer.Guidance(record)}";
        CopyHistoryDetailsButton.IsEnabled = record is not null;
    }

    private void CopyHistoryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not UpdateHistoryItem) return;
        CopyText(HistoryDetailsText.Text);
        StatusText.Text = "History event details copied";
    }

    private void CopyHistoryFailures_Click(object sender, RoutedEventArgs e)
    {
        CopyText(UpdateHistoryAnalyzer.BuildFailureSummary(VisibleUpdateHistory.Select(item => item.Record)));
        StatusText.Text = "Filtered history failure summary copied";
    }

    private void ApplyHistoryFilter()
    {
        if (HistoryFilterBox is null || HistoryRangeCombo is null || HistoryOperationCombo is null || HistoryList is null) return;
        var selected = (HistoryList.SelectedItem as UpdateHistoryItem)?.Record;
        _ = int.TryParse(SelectedComboTag(HistoryRangeCombo), out var days);
        _ = int.TryParse(SelectedComboTag(HistoryOperationCombo), out var operation);
        var now = _clock?.Now ?? DateTimeOffset.UtcNow;
        var matches = _allUpdateHistory.Where(item => UpdateHistoryAnalyzer.Matches(item.Record,
            HistoryFilterBox.Text, HistoryFailuresOnlyCheck?.IsChecked == true, operation, days, now));
        Replace(VisibleUpdateHistory, matches);
        HistoryList.SelectedItem = VisibleUpdateHistory.FirstOrDefault(item => item.Record == selected);
        UpdateHistoryDetails();
        if (VisibleHistoryCountText is not null)
        {
            VisibleHistoryCountText.Text = $"{VisibleUpdateHistory.Count} shown · {VisibleUpdateHistory.Count(item => UpdateHistoryAnalyzer.IsFailure(item.Record))} failures or partial results";
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
            _sourcesLoaded = true;
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
        NavigateTo("compare");
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
        SavePreferencesSoon();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ScanView is null || CompareView is null || WatchlistView is null || SourcesView is null || ControlsView is null || PerformanceView is null || DiagnosticsView is null || HistoryView is null || ActivityView is null || AboutView is null) return;

        var tag = args.SelectedItemContainer?.Tag as string ?? "scan";
        ShowView(tag);
    }

    private void ShowView(string tag)
    {
        _currentPageTag = tag;
        ControlsActionBar.Visibility = tag == "controls" ? Visibility.Visible : Visibility.Collapsed;
        ScanToolsBar.Visibility = tag is "scan" or "compare" or "watchlist" or "sources" ? Visibility.Visible : Visibility.Collapsed;
        RecordsToolsBar.Visibility = tag is "history" or "activity" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tab in ScanToolsBar.Children.Concat(RecordsToolsBar.Children).OfType<ToggleButton>())
            tab.IsChecked = string.Equals(tab.Tag as string, tag, StringComparison.Ordinal);
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
        PageScrollViewer.ChangeView(horizontalOffset: 0, verticalOffset: 0, zoomFactor: null, disableAnimation: true);
        if (tag == "controls" && _allPolicyStates.Count == 0) _ = RefreshSettingsAsync();
        if (tag == "performance") _ = RefreshPerformanceAsync();
        if (tag == "sources" && !_sourcesLoaded && !_isBusy) RefreshSources_Click(RootGrid, new RoutedEventArgs());
        if (tag == "history" && !_historyLoaded && !_isBusy) RefreshHistory_Click(RootGrid, new RoutedEventArgs());
        SavePreferencesSoon();
    }

    private void NavigateTo(string tag)
    {
        var parent = tag is "compare" or "watchlist" or "sources" ? "scan" : tag == "activity" ? "history" : tag;
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(candidate => string.Equals(candidate.Tag as string, parent, StringComparison.Ordinal));
        if (item is not null) Navigation.SelectedItem = item;
        ShowView(item is null ? "scan" : tag);
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) NavigateTo(tag);
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

    private async void RefreshSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_applyingSettings) await RefreshSettingsAsync();
    }

    private Task RefreshSettingsAsync() => _settingsRefreshTask is { IsCompleted: false }
        ? _settingsRefreshTask : _settingsRefreshTask = ReadSettingsAsync();

    private async Task ReadSettingsAsync()
    {
        _readingSettings = true;
        SyncCommandCenter();
        try
        {
            var snapshot = await _settingsService.GetSnapshotAsync(CancellationToken.None);
            var audit = await _settingsService.GetAuditAsync(CancellationToken.None);
            _allPolicyStates.Clear();
            var favorites = _preferences.FavoritePolicyIds ?? Array.Empty<string>();
            _allPolicyStates.AddRange(snapshot.Policies.Select(state => new PolicyStateItem(state, favorites.Contains(state.Definition.Id))));
            EnsurePolicyCategoryOptions();
            Replace(SettingsAudit, audit.Take(25).Select(static entry => new SettingAuditItem(entry)));
            ApplyPolicyFilter();
            ControlsSummaryText.Text = $"{snapshot.Policies.Count} policies · Windows build {snapshot.WindowsBuild} · {snapshot.Policies.Count(static policy => policy.CanEdit)} locally editable";
            SyncCommandCenter();
        }
        catch (Exception exception)
        {
            ControlsSummaryText.Text = $"Settings unavailable: {exception.Message}";
            Log($"Settings refresh failed: {exception.Message}");
        }
        finally { _readingSettings = false; SyncCommandCenter(); }
    }

    private void PolicyFilter_Changed(object sender, RoutedEventArgs e) { ApplyPolicyFilter(); SavePreferencesSoon(); }
    private void PolicyFilter_Changed(object sender, TextChangedEventArgs e) { ApplyPolicyFilter(); SavePreferencesSoon(); }
    private void PolicyFilterCombo_Changed(object sender, SelectionChangedEventArgs e) { ApplyPolicyFilter(); SavePreferencesSoon(); }

    private void ApplyPolicyFilter()
    {
        if (PolicyFilterBox is null || PolicyCategoryCombo is null || PolicyOwnershipCombo is null ||
            PolicyRiskCombo is null || PolicyStateFilterCombo is null || ShowLegacyPolicyCheck is null ||
            VisiblePolicyCountText is null) return;
        var query = PolicyFilterBox.Text?.Trim();
        var showLegacy = ShowLegacyPolicyCheck.IsChecked == true;
        var category = SelectedComboTag(PolicyCategoryCombo);
        var ownership = SelectedComboTag(PolicyOwnershipCombo);
        var risk = SelectedComboTag(PolicyRiskCombo);
        var stateFilter = SelectedComboTag(PolicyStateFilterCombo);
        var filtered = _allPolicyStates.Where(item =>
            (showLegacy || item.State.IsSupported && !item.State.Definition.IsLegacy) &&
            (category == "All" || item.Category == category) &&
            (ownership == "All" || item.State.Ownership.ToString() == ownership) &&
            (risk == "All" || item.State.Definition.Risk.ToString() == risk) &&
            (stateFilter == "All" ||
             stateFilter == "Editable" && item.State.CanEdit ||
             stateFilter == "ViewOnly" && !item.State.CanEdit ||
             stateFilter == "Different" && item.HasDifference ||
             stateFilter == "Favorites" && item.IsFavorite) &&
            (string.IsNullOrWhiteSpace(query) ||
             item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.ValueLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        var selectedId = (PolicyList.SelectedItem as PolicyStateItem)?.State.Definition.Id;
        Replace(VisiblePolicies, filtered.OrderBy(static item => item.Category).ThenBy(static item => item.DisplayName));
        PolicyList.SelectedItem = VisiblePolicies.FirstOrDefault(item => item.State.Definition.Id == selectedId);
        VisiblePolicyCountText.Text = $"{VisiblePolicies.Count} shown";
    }

    private void EnsurePolicyCategoryOptions()
    {
        var selected = _preferences.PolicyCategory;
        PolicyCategoryCombo.Items.Clear();
        PolicyCategoryCombo.Items.Add(new ComboBoxItem { Content = "All categories", Tag = "All" });
        foreach (var category in _allPolicyStates.Select(static item => item.Category).Distinct().OrderBy(static value => value))
            PolicyCategoryCombo.Items.Add(new ComboBoxItem { Content = category, Tag = category });
        SelectComboTag(PolicyCategoryCombo, string.IsNullOrWhiteSpace(selected) ? "All" : selected);
        SelectComboTag(PolicyOwnershipCombo, _preferences.PolicyOwnership);
        SelectComboTag(PolicyRiskCombo, _preferences.PolicyRisk);
        SelectComboTag(PolicyStateFilterCombo, _preferences.PolicyStateFilter);
    }

    private void PolicyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item)
        {
            ApplyPolicyButton.IsEnabled = ClearPolicyButton.IsEnabled = false;
            PolicyDetailTitle.Text = "Select a policy.";
            PolicyDetailStatus.Text = "Choose a policy to inspect its current value and stage a change.";
            PolicyBooleanEditor.Visibility = PolicyChoiceEditor.Visibility = PolicyNumberEditor.Visibility =
                PolicyDateEditor.Visibility = PolicyValueBox.Visibility = Visibility.Collapsed;
            PolicyDocumentationLink.Visibility = Visibility.Collapsed;
            PolicyValidationInfo.IsOpen = false;
            return;
        }
        PolicyDetailTitle.Text = item.DisplayName;
        PolicyDetailStatus.Text = $"{item.Description}\n\nRequested: {item.RequestedLabel}\nEffective: {item.ValueLabel}\nOwner: {item.OwnershipLabel} · Risk: {item.RiskLabel}\n" +
            $"{(item.State.Definition.RequiresRestart ? "Restart required after change. " : "Policy refresh/readback required. ")}{item.Status}";
        ConfigurePolicyEditor(item.State);
        PolicyDocumentationLink.NavigateUri = TryHttpUri(item.State.Definition.DocumentationUrl, out var docs) ? docs : null;
        PolicyDocumentationLink.Visibility = PolicyDocumentationLink.NavigateUri is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyPolicyButton.IsEnabled = ClearPolicyButton.IsEnabled = item.State.CanEdit;
    }

    private async void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item) return;
        try
        {
            StagePolicyChange(item, ReadPolicyEditorValue(item.State.Definition), false);
            PolicyValidationInfo.IsOpen = false;
        }
        catch (Exception exception)
        {
            PolicyValidationInfo.Message = exception.Message;
            PolicyValidationInfo.IsOpen = true;
        }
        await Task.CompletedTask;
    }

    private async void ClearPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (PolicyList.SelectedItem is not PolicyStateItem item) return;
        StagePolicyChange(item, null, true);
        await Task.CompletedTask;
    }

    private void SyncCommandCenter()
    {
        if (PendingChangesText is null) return;
        _syncingQuickControls = true;
        try
        {
            foreach (var control in QuickControls)
                control.Update(_allPolicyStates.FirstOrDefault(item => item.State.Definition.Id == control.Id)?.State,
                    PolicyChangeCart.FirstOrDefault(item => item.Change.PolicyId == control.Id)?.Change, _isBusy || _applyingSettings || _readingSettings);
            PendingChangesText.Text = PolicyChangeCart.Count == 0 ? "No pending changes" : $"{PolicyChangeCart.Count} pending change(s) · device settings unchanged until applied";
            StickyPendingText.Text = PolicyChangeCart.Count == 0 ? "No pending changes" : $"{PolicyChangeCart.Count} pending change(s) · not yet applied";
            ApplyCartButton.IsEnabled = PolicyChangeCart.Count > 0 && !_isBusy && !_applyingSettings && !_readingSettings;
            DiscardCartButton.IsEnabled = PolicyChangeCart.Count > 0 && !_isBusy && !_applyingSettings;
            RemoveCartButton.IsEnabled = PolicyChangeCartList.SelectedItem is not null && !_isBusy && !_applyingSettings;
            ApplyPolicyButton.IsEnabled = ClearPolicyButton.IsEnabled = PolicyList.SelectedItem is PolicyStateItem { State.CanEdit: true } && !_isBusy && !_applyingSettings && !_readingSettings;
            var pause = _allPolicyStates.Where(item => item.State.Definition.Id is "update.pause-quality-start" or "update.pause-feature-start").ToArray();
            PauseUpdatesButton.IsEnabled = ResumeUpdatesButton.IsEnabled = pause.Length == 2 && pause.All(item => item.State.CanEdit) && !_isBusy && !_applyingSettings && !_readingSettings;
            PauseStateText.Text = pause.Length == 0 ? "Refresh to read pause state." : string.Join(" · ", pause.Select(item =>
            {
                var pending = PolicyChangeCart.FirstOrDefault(entry => entry.Change.PolicyId == item.State.Definition.Id);
                var label = item.State.Definition.Id == "update.pause-quality-start" ? "Quality" : "Feature";
                return $"{label}: {(pending is null ? item.State.EffectiveValue ?? "Windows default" : pending.Change.Remove ? "resume pending" : "pause pending")}";
            }));
        }
        finally { _syncingQuickControls = false; }
    }

    private void QuickControl_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingQuickControls || sender is not ToggleSwitch { Tag: QuickControlItem control } toggle || toggle.IsOn == control.IsOn) return;
        var state = _allPolicyStates.FirstOrDefault(item => item.State.Definition.Id == control.Id);
        if (state is null || !state.State.CanEdit || _isBusy || _applyingSettings || _readingSettings) { SyncCommandCenter(); return; }
        StagePolicyChange(state, toggle.IsOn ? "1" : "0", false);
    }

    private async void PauseUpdates_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var id in new[] { "update.pause-quality-start", "update.pause-feature-start" })
            if (_allPolicyStates.FirstOrDefault(item => item.State.Definition.Id == id) is { } state) StagePolicyChange(state, today, false);
        await Task.CompletedTask;
    }

    private async void ResumeUpdates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var id in new[] { "update.pause-quality-start", "update.pause-feature-start" })
            if (_allPolicyStates.FirstOrDefault(item => item.State.Definition.Id == id) is { } state) StagePolicyChange(state, null, true);
        await Task.CompletedTask;
    }

    private async Task<bool> ApplySettingChangesAsync(IReadOnlyList<SettingChange> changes, PolicyRisk risk)
    {
        if (_isBusy || _applyingSettings) return false;
        _applyingSettings = true;
        SyncCommandCenter();
        try
        {
            if (_settingsRefreshTask is { IsCompleted: false }) await _settingsRefreshTask;
            var warning = risk == PolicyRisk.High
                ? "This can change update sources, safeguards, or user access. A bad value can prevent updates."
                : "This writes elevated device-local Windows Update policy or Settings state. Domain or MDM policy may overwrite it.";
            var review = string.Join("\n\n", PolicyChangeCart.Select(item => $"{item.Title}\n{item.Summary}"));
            if (!await ConfirmAsync("Apply Windows Update settings?", $"{review}\n\n{warning}\n\n{changes.Count} change(s) will be journaled, verified together, and rolled back if any write fails.")) return false;
            if (_isBusy) return false;
            SetBusy(true, "Applying and verifying settings…", cancellable: false);
            try
            {
                var result = await _settingsService.ApplyAsync(changes, CancellationToken.None);
                Log($"Settings batch {result.BatchId}: {result.Summary}");
                await RefreshSettingsAsync();
                await ShowMessageAsync(result.Succeeded ? "Settings applied" : "Settings rolled back", result.Summary);
                return result.Succeeded;
            }
            catch (Exception exception) { await ShowMessageAsync("Settings change failed", exception.Message); return false; }
            finally { SetBusy(false, "Ready"); }
        }
        finally { _applyingSettings = false; SyncCommandCenter(); }
    }

    private void ConfigurePolicyEditor(PolicyState state)
    {
        var pending = PolicyChangeCart.FirstOrDefault(item => item.Change.PolicyId == state.Definition.Id);
        var value = pending is null ? state.RequestedValue ?? state.EffectiveValue : pending.Change.AfterValue;
        PolicyValidationInfo.IsOpen = false;
        PolicyBooleanEditor.Visibility = PolicyChoiceEditor.Visibility = PolicyNumberEditor.Visibility =
            PolicyDateEditor.Visibility = PolicyValueBox.Visibility = Visibility.Collapsed;
        var definition = state.Definition;
        switch (definition.ValueKind)
        {
            case PolicyValueKind.Boolean:
                PolicyBooleanEditor.Visibility = Visibility.Visible;
                PolicyBooleanEditor.IsOn = value == "1";
                break;
            case PolicyValueKind.Choice:
                PolicyChoices.Clear();
                foreach (var choice in definition.Choices ?? new Dictionary<string, string>())
                    PolicyChoices.Add(new(choice.Key, choice.Value));
                PolicyChoiceEditor.SelectedItem = PolicyChoices.FirstOrDefault(choice => choice.Value == value) ?? PolicyChoices.FirstOrDefault();
                PolicyChoiceEditor.Visibility = Visibility.Visible;
                break;
            case PolicyValueKind.Integer:
                PolicyNumberEditor.Minimum = definition.Minimum ?? int.MinValue;
                PolicyNumberEditor.Maximum = definition.Maximum ?? int.MaxValue;
                PolicyNumberEditor.Value = double.TryParse(value, out var number) ? number : definition.Minimum ?? 0;
                PolicyNumberEditor.Visibility = Visibility.Visible;
                break;
            case PolicyValueKind.DateTime:
                PolicyDateEditor.Date = DateTimeOffset.TryParse(value, out var date) ? date : DateTimeOffset.Now;
                PolicyDateEditor.Visibility = Visibility.Visible;
                break;
            default:
                PolicyValueBox.Text = value ?? string.Empty;
                PolicyValueBox.Visibility = Visibility.Visible;
                break;
        }
    }

    private string? ReadPolicyEditorValue(PolicyDefinition definition)
    {
        var raw = definition.ValueKind switch
        {
            PolicyValueKind.Boolean => PolicyBooleanEditor.IsOn ? "1" : "0",
            PolicyValueKind.Choice => (PolicyChoiceEditor.SelectedItem as PolicyChoiceItem)?.Value,
            PolicyValueKind.Integer => double.IsNaN(PolicyNumberEditor.Value) ? null : ((int)PolicyNumberEditor.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            PolicyValueKind.DateTime => PolicyDateEditor.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _ => PolicyValueBox.Text
        };
        return PolicyValueValidator.Normalize(definition, raw, false);
    }

    private void StagePolicyChange(PolicyStateItem item, string? value, bool remove)
    {
        if (_isBusy || _applyingSettings || _readingSettings || !item.State.CanEdit) return;
        var existing = PolicyChangeCart.FirstOrDefault(entry => entry.Change.PolicyId == item.State.Definition.Id);
        var change = PolicyChangePlanner.Stage(item.State, existing?.Change, value, remove);
        if (change is null)
        {
            if (existing is not null) PolicyChangeCart.Remove(existing);
            StatusText.Text = $"{item.DisplayName}: pending change cleared.";
        }
        else
        {
            if (existing is null) PolicyChangeCart.Add(new(change));
            else PolicyChangeCart[PolicyChangeCart.IndexOf(existing)] = new(change);
            StatusText.Text = $"{item.DisplayName} staged. Review pending changes.";
        }
        SyncCommandCenter();
    }

    private void FavoritePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PolicyStateItem item }) return;
        item.IsFavorite = !item.IsFavorite;
        ApplyPolicyFilter();
        SavePreferencesSoon();
    }

    private async void ApplyPolicyCart_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _applyingSettings) return;
        if (PolicyChangeCart.Count == 0)
        {
            await ShowMessageAsync("Change cart is empty", "Stage one or more editable policies first.");
            return;
        }
        var changes = PolicyChangeCart.Select(entry => new SettingChange(entry.Change.PolicyId, entry.Change.AfterValue,
            entry.Change.Remove, entry.Change.BeforeValue, EnforceExpectedRequestedValue: true)).ToArray();
        var risk = PolicyChangeCart.Max(static entry => entry.Change.Risk);
        if (await ApplySettingChangesAsync(changes, risk)) PolicyChangeCart.Clear();
    }

    private void RemovePolicyCartItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _applyingSettings) return;
        if (PolicyChangeCartList.SelectedItem is StagedPolicyChangeItem selected) PolicyChangeCart.Remove(selected);
    }

    private void ClearPolicyCart_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _applyingSettings) return;
        PolicyChangeCart.Clear();
        SyncCommandCenter();
    }

    private void PolicyCart_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncCommandCenter();

    private void CopyAuditDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsAuditList.SelectedItem is not SettingAuditItem selected) return;
        var entry = selected.Entry;
        CopyText($"{entry.ChangedAt:O}\n{entry.DisplayName} ({entry.PolicyId})\nBefore: {entry.BeforeValue ?? "Windows default"}\nAfter: {entry.AfterValue ?? "Windows default"}\nVerified: {entry.VerifiedValue ?? "Windows default"}\nOwner: {entry.Ownership}\nResult: {entry.Message}");
        StatusText.Text = "Audit details copied";
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
        if (_isBusy || _applyingSettings || SettingsAuditList.SelectedItem is not SettingAuditItem selected) return;
        _applyingSettings = true;
        SyncCommandCenter();
        try
        {
            if (_settingsRefreshTask is { IsCompleted: false }) await _settingsRefreshTask;
            if (!await ConfirmAsync("Restore previous value?", $"{selected.Title}\n\nWuPilot will refuse restoration if the setting drifted after this change.")) return;
            if (_isBusy) return;
            SetBusy(true, "Restoring and verifying settings…", cancellable: false);
            try
            {
                var result = await _settingsService.RestoreAsync(selected.Entry.Id, allowConflict: false, CancellationToken.None);
                Log($"Restored setting from audit {selected.Entry.Id}: {result.Summary}");
                await RefreshSettingsAsync();
                await ShowMessageAsync(result.Succeeded ? "Setting restored" : "Restore stopped", result.Summary);
            }
            finally { SetBusy(false, "Ready"); }
        }
        catch (Exception exception) { await ShowMessageAsync("Restore stopped", exception.Message); }
        finally { _applyingSettings = false; SyncCommandCenter(); }
    }

    private IReadOnlyList<UpgradeTimeline> _bundleTimelines = [];
    public ObservableCollection<string> VisibleUpdateTimelines { get; } = [];

    private async Task LoadBundlePerformanceAsync(string bundle)
    {
        var evidence = await UpgradeReportService.ReadBundleTimelinesAsync(bundle, CancellationToken.None);
        _bundleTimelines = evidence.Updates;
        BundleTimingStatus.Text = $"{evidence.Updates.Count} update attempts from {bundle}" +
            (evidence.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", evidence.Warnings));
        ApplyPerformanceFilter();
    }

    private async void AnalyzeBundle_Click(object sender, RoutedEventArgs e)
    {
        if (_collectingLogs) return;
        AnalyzeBundleButton.IsEnabled = false;
        CollectLogsButton.IsEnabled = false;
        try
        {
            var bundle = LogInputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(bundle)) throw new ArgumentException("Enter an extracted bundle folder in Analyze existing logs.");
            await LoadBundlePerformanceAsync(bundle);
            var destination = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuPilot", "Reports", Guid.NewGuid().ToString("N"));
            var report = await UpgradeReportService.RegenerateAsync(bundle, destination, null, CancellationToken.None, PerformanceUpdateBox.Text);
            OpenLogReportButton.Tag = report;
            OpenLogReportButton.IsEnabled = true;
            LogCollectionStatus.Text = $"Report regenerated from retained evidence: {report}";
        }
        catch (Exception exception) { BundleTimingStatus.Text = $"Bundle analysis failed: {exception.Message}"; }
        finally { AnalyzeBundleButton.IsEnabled = true; CollectLogsButton.IsEnabled = true; }
    }

    private void PerformanceUpdate_Changed(object sender, TextChangedEventArgs e)
    {
        if (PerformanceSummaryText is null) return;
        ApplyPerformanceFilter();
    }

    private void ApplyPerformanceFilter()
    {
        var selection = PerformanceUpdateBox.Text;
        var days = SelectedPerformanceDays();
        string Duration(TimeSpan? span) => span is null ? "Unavailable" : $"{span.Value.TotalSeconds:0} s";
        Replace(VisibleUpdateTimelines, _bundleTimelines.Where(t => UpdateTimingFilter.Matches(selection, t.UpdateId, t.Title) &&
            (!string.IsNullOrWhiteSpace(selection) || days == 0 || t.AttemptedAt >= DateTimeOffset.Now.AddDays(-days))).Select(t =>
            $"{t.Title} | {t.AttemptedAt:yyyy-MM-dd HH:mm:ss zzz}\nUpdate ID: {t.UpdateId}\nDownload: {Duration(t.Download.Duration)} | Install to restart required: {Duration(t.Install.Duration)}\nWaiting for restart: {Duration(t.WaitingForRestart)} | Restart to package installed: {Duration(t.Reboot.Duration)}\n{t.Status} | Log boundaries / correlated restart estimate; not desktop readiness."));
        Replace(VisibleMetrics, _allMetrics.Where(item => UpdateTimingFilter.Matches(selection, item.Metric.UpdateId, item.Metric.Title) && (!string.IsNullOrWhiteSpace(selection) || days == 0 || item.Metric.CompletedAt >= DateTimeOffset.Now.AddDays(-days))));
        PerformanceSummaryText.Text = $"{VisibleMetrics.Count} matching operations | {(string.IsNullOrWhiteSpace(selection) ? "Selected date range" : "All retained history")} | Missing timings remain unavailable";
    }

    private async void RefreshPerformance_Click(object sender, RoutedEventArgs e) => await RefreshPerformanceAsync();
    private void PerformanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PerformanceList.SelectedItem is not OperationMetricItem selected)
        {
            OperationDetailTitle.Text = "Select an operation to inspect its diagnostics.";
            OperationDetailText.Text = "No operation selected.";
            OperationRecommendation.IsOpen = false;
            CopyOperationButton.IsEnabled = false;
            return;
        }

        OperationDetailTitle.Text = selected.Title;
        OperationDetailText.Text = selected.DetailText;
        OperationRecommendation.Message = selected.Recommendation;
        OperationRecommendation.IsOpen = selected.Metric.ResultCode is not (2 or 3);
        CopyOperationButton.IsEnabled = true;
    }

    private void CopyOperationDetails_Click(object sender, RoutedEventArgs e)
    {
        if (PerformanceList.SelectedItem is not OperationMetricItem selected) return;
        CopyText($"{selected.Title}\n{selected.DetailText}\nRecommended next action: {selected.Recommendation}");
        StatusText.Text = "Operation details copied";
    }

    private async void PerformanceRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_profilesLoaded) return;
        SavePreferencesSoon();
        await RefreshPerformanceAsync();
    }

    private Task RefreshPerformanceAsync()
    {
        return _performanceRefreshTask is { IsCompleted: false } ? _performanceRefreshTask : _performanceRefreshTask = RefreshPerformanceCoreAsync();
    }

    private async Task RefreshPerformanceCoreAsync()
    {
        try
        {
            var selectedMetricId = (PerformanceList.SelectedItem as OperationMetricItem)?.Metric.Id;
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
            _allMetrics.Clear();
            _allMetrics.AddRange((await metricsTask).Select(static metric => new OperationMetricItem(metric)));
            ApplyPerformanceFilter();
            if (selectedMetricId is not null)
                PerformanceList.SelectedItem = VisibleMetrics.FirstOrDefault(item => item.Metric.Id == selectedMetricId);

        }
        catch (Exception exception) { PerformanceSummaryText.Text = $"Performance data unavailable: {exception.Message}"; }
    }

    private async void CollectLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_collectingLogs) return;
        _collectingLogs = true;
        CollectLogsButton.IsEnabled = false;
        AnalyzeBundleButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        RoutedEventHandler cancel = (_, _) => cancellation.Cancel();
        CancelLogCollectionButton.Click += cancel;
        CancelLogCollectionButton.IsEnabled = true;
        try
        {
            var destination = LogDestinationBox.Text.Trim();
            if (string.IsNullOrEmpty(destination)) destination = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WuPilot", "LogBundles");
            var input = string.IsNullOrWhiteSpace(LogInputBox.Text) ? null : LogInputBox.Text.Trim();
            var result = await new LogCollectionService().CollectAsync(new(destination, input, SelectedUpdate: PerformanceUpdateBox.Text), new Progress<string>(message => LogCollectionStatus.Text = message), cancellation.Token);
            LogCollectionStatus.Text = $"{result.Collected} collected; {result.Missing} missing; {result.Limited} limited; {result.Failed} failed. ZIP: {result.DeliveredZip ?? result.LocalZip}\nReport: {result.ReportPath}\n{result.DeliveryError}";
            Log(LogCollectionStatus.Text);
            OpenLogReportButton.Tag = result.ReportPath;
            OpenLogReportButton.IsEnabled = true;
            await LoadBundlePerformanceAsync(System.IO.Path.GetDirectoryName(result.ReportPath)!);
        }
        catch (OperationCanceledException) { LogCollectionStatus.Text = "Collection cancelled. Partial local files may remain in LocalAppData/WuPilot/LogBundles."; }
        catch (Exception exception) { LogCollectionStatus.Text = $"Log collection failed: {exception.Message}"; Log(LogCollectionStatus.Text); }
        finally
        {
            CancelLogCollectionButton.Click -= cancel;
            CancelLogCollectionButton.IsEnabled = false;
            CollectLogsButton.IsEnabled = true;
            AnalyzeBundleButton.IsEnabled = true;
            _collectingLogs = false;
        }
    }

    private void OpenLogReport_Click(object sender, RoutedEventArgs e)
    {
        try { if (OpenLogReportButton.Tag is string path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { LogCollectionStatus.Text = $"Could not open report: {exception.Message}"; }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForAppUpdateAsync(force: true);

    private async Task CheckForAppUpdateAsync(bool force)
    {
        if (_checkingAppUpdate) return;
        _checkingAppUpdate = true;
        var ownsBusyState = false;
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
            if (!force)
            {
                Log($"{release.Name} is available. Open About to review and download the update.");
                return;
            }
            if (_isBusy || _collectingLogs) return;
            if (!await ConfirmAsync("WuPilot update available", $"{release.Name}\nPublished {release.PublishedAt:g}\n{release.Size / 1024d / 1024d:0.0} MB\n\n{release.Notes}\n\nDownload and verify the {RuntimeArchitectureLabel()} installer?")) return;
            if (_isBusy || _collectingLogs) return;
            ownsBusyState = true;
            SetBusy(true, "Downloading WuPilot update…", cancellable: false);
            var downloaded = await _appUpdateService.DownloadAsync(release, CreateProgress(), CancellationToken.None);
            var signatureWarning = downloaded.IsAuthenticodeSigned
                ? "The Authenticode signature is valid."
                : "This release is not Authenticode-signed. Its GitHub and sidecar SHA-256 digests match, but Windows may show an unknown-publisher warning.";
            if (!await ConfirmAsync("Install verified update?", $"SHA-256: {downloaded.Sha256}\n\n{signatureWarning}\n\nWuPilot will close and launch the installer.")) return;
            if (_collectingLogs) { AppUpdateStatusText.Text = "Finish log collection before installing the application update."; return; }
            _appUpdateService.LaunchInstaller(downloaded);
            Close();
        }
        catch (Exception exception)
        {
            AppUpdateStatusText.Text = $"Update check failed: {exception.Message}";
            if (force) await ShowMessageAsync("Update check failed", exception.Message);
            Log($"App update check failed: {exception.Message}");
        }
        finally
        {
            if (ownsBusyState) SetBusy(false, "Ready");
            _checkingAppUpdate = false;
        }
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
        GlobalProgressBar.IsIndeterminate = progress.Percent is null;
        if (progress.Percent is not null) GlobalProgressBar.Value = progress.Percent.Value;
        _shellProgressService.SetProgress(progress.Percent is null ? ShellProgressState.Indeterminate : ShellProgressState.Normal, progress.Percent);
        if (_operationStatus is not null)
            _operationStatus = _operationStatus with { Stage = progress.Stage, Message = progress.Message, Percent = progress.Percent, Elapsed = _clock.Now - _operationStatus.StartedAt };
    });

    private void SetBusy(bool busy, string status, bool cancellable = true)
    {
        var wasBusy = _isBusy;
        _isBusy = busy;
        SyncCommandCenter();
        BusyRing.IsActive = busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        GlobalCancelButton.IsEnabled = busy && cancellable;
        StatusText.Text = status;
        if (busy && !wasBusy)
        {
            _operationFailed = false;
            _operationStatus = new(Guid.NewGuid(), status.TrimEnd('…', '.'), _currentPageTag, "Starting", status, null,
                _clock.Now, TimeSpan.Zero, cancellable, OperationRunState.Running);
            OperationOriginText.Text = $"From: {ReadablePage(_currentPageTag)}";
            GlobalProgressBar.IsIndeterminate = true;
            _shellProgressService.SetProgress(ShellProgressState.Indeterminate);
            _elapsedTimer.Start();
        }
        else if (!busy && wasBusy)
        {
            _elapsedTimer.Stop();
            GlobalProgressBar.IsIndeterminate = false;
            GlobalProgressBar.Value = 0;
            ProgressText.Text = string.Empty;
            ElapsedText.Text = string.Empty;
            OperationOriginText.Text = string.Empty;
            var state = _operationCancellation?.IsCancellationRequested == true ? OperationRunState.Cancelled :
                _operationFailed ? OperationRunState.Failed : OperationRunState.Succeeded;
            if (_operationStatus is not null) _ = CompleteOperationAsync(_operationStatus with { State = state, Elapsed = _clock.Now - _operationStatus.StartedAt });
            _operationStatus = null;
            if (_closeAfterOperation && !_collectingLogs) { _allowClose = true; Close(); }
        }
        UpdateBulkActionState();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (_isBusy && (title.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("rolled back", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("stopped", StringComparison.OrdinalIgnoreCase)))
            _operationFailed = true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private async Task CompleteOperationAsync(OperationStatus operation)
    {
        var severity = operation.State switch
        {
            OperationRunState.Failed => CompletionSeverity.Error,
            OperationRunState.Cancelled => CompletionSeverity.Warning,
            _ => CompletionSeverity.Success
        };
        var notice = new CompletionNotice(Guid.NewGuid(), _clock.Now,
            operation.State == OperationRunState.Succeeded ? $"{operation.Operation} completed" :
            operation.State == OperationRunState.Cancelled ? $"{operation.Operation} cancelled" : $"{operation.Operation} failed",
            $"{FormatDuration(operation.Elapsed)} · {operation.Message}", operation.OriginatingPage, severity);
        await _completionNoticeStore.SaveAsync(notice, CancellationToken.None);
        await ReloadCompletionNoticesAsync();
        _shellProgressService.SetProgress(operation.State == OperationRunState.Failed ? ShellProgressState.Error : ShellProgressState.None);
        if (_preferences.FlashTaskbarOnCompletion && !_shellProgressService.IsForeground()) _shellProgressService.RequestAttention();
    }

    private async Task ReloadCompletionNoticesAsync()
    {
        var notices = await _completionNoticeStore.GetAllAsync(CancellationToken.None);
        Replace(CompletionNotices, notices.Select(static notice => new CompletionNoticeItem(notice)));
        CompletionEmptyText.Visibility = CompletionNotices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompletionButton.Content = CompletionNotices.Count == 0 ? "Notices" : $"Notices ({CompletionNotices.Count})";
    }

    private void ElapsedTimer_Tick(object? sender, object e)
    {
        if (_operationStatus is null) return;
        var elapsed = _clock.Now - _operationStatus.StartedAt;
        _operationStatus = _operationStatus with { Elapsed = elapsed };
        ElapsedText.Text = FormatDuration(elapsed);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) SavePreferencesSoon();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args) =>
        _windowIsActive = args.WindowActivationState != WindowActivationState.Deactivated;

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_collectingLogs)
        {
            LogCollectionStatus.Text = "Log collection is running. Use Cancel on Performance, or wait for collection to finish before closing.";
            StatusText.Text = "Log collection is running. Cancel it from Performance before closing.";
            return;
        }
        if (_closePromptOpen) return;
        if (_isBusy)
        {
            _closePromptOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "An operation is still running",
                    Content = "WuPilot will not terminate Windows Update Agent or repair work in the middle of a call. Keep WuPilot open, or request cancellation and close after the current safe boundary returns.",
                    PrimaryButtonText = _operationStatus?.IsCancellable == true ? "Request cancellation" : "Close when finished",
                    CloseButtonText = "Keep running",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    _closeAfterOperation = true;
                    if (_operationStatus?.IsCancellable == true)
                    {
                        _operationCancellation?.Cancel();
                        _shellProgressService.SetProgress(ShellProgressState.Paused, _operationStatus.Percent);
                        StatusText.Text = "Cancellation requested; WuPilot will close after the current call returns.";
                    }
                    else StatusText.Text = "WuPilot will close when the current operation finishes.";
                }
            }
            finally { _closePromptOpen = false; }
            return;
        }
        _preferences = CapturePreferences();
        _preferencesStore.ScheduleSave(_preferences);
        await _preferencesStore.FlushAsync(CancellationToken.None);
        _allowClose = true;
        Close();
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && e.Key is VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3)
        {
            NavigateTo(e.Key == VirtualKey.Number1 ? "scan" : e.Key == VirtualKey.Number2 ? "controls" : "performance");
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.F)
        {
            FocusActiveSearch();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            RefreshActivePage();
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.Enter && _currentPageTag == "scan" && !_isBusy)
        {
            Scan_Click(ScanButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && _isBusy && _operationStatus?.IsCancellable == true)
        {
            Cancel_Click(GlobalCancelButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && shift && e.Key == VirtualKey.E && _scanReport is not null)
        {
            await ExportAsync(null);
            e.Handled = true;
        }
    }

    private void FocusActiveSearch()
    {
        if (_currentPageTag == "controls") ControlsAdvanced.IsExpanded = true;
        Control? control = _currentPageTag switch
        {
            "scan" => FilterBox,
            "controls" => PolicyFilterBox,
            "diagnostics" => DiagnosticFilterBox,
            "history" => HistoryFilterBox,
            _ => null
        };
        var searchPage = _currentPageTag;
        DispatcherQueue.TryEnqueue(async () =>
        {
            // The expander animates its height; wait for its scroll extent before revealing search.
            if (searchPage == "controls") await Task.Delay(350);
            if (_currentPageTag != searchPage) return;
            PageScrollViewer.UpdateLayout();
            control?.Focus(FocusState.Keyboard);
            control?.StartBringIntoView();
            if (_currentPageTag == "controls" && control is not null)
            {
                var position = control.TransformToVisual(PageScrollViewer).TransformPoint(new Windows.Foundation.Point(0, 0));
                PageScrollViewer.ChangeView(null, Math.Max(0, PageScrollViewer.VerticalOffset + position.Y - 24), null, disableAnimation: true);
            }
        });
    }

    private void RefreshActivePage()
    {
        if (_isBusy || _applyingSettings) return;
        switch (_currentPageTag)
        {
            case "controls": _ = RefreshSettingsAsync(); break;
            case "performance": _ = RefreshPerformanceAsync(); break;
            case "diagnostics": Diagnostics_Click(RootGrid, new RoutedEventArgs()); break;
            case "history": RefreshHistory_Click(RootGrid, new RoutedEventArgs()); break;
            case "sources": RefreshSources_Click(RootGrid, new RoutedEventArgs()); break;
            case "watchlist": _ = ReloadWatchlistAsync(); break;
        }
    }

    private void CompletionButton_Click(object sender, RoutedEventArgs e) { }

    private async void OpenCompletionNotice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CompletionNoticeItem item }) return;
        NavigateTo(item.Notice.SourcePage);
        await _completionNoticeStore.DismissAsync(item.Notice.Id, CancellationToken.None);
        await ReloadCompletionNoticesAsync();
        CompletionFlyout.Hide();
        _shellProgressService.SetProgress(ShellProgressState.None);
    }

    private async void DismissCompletionNotice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CompletionNoticeItem item }) return;
        await _completionNoticeStore.DismissAsync(item.Notice.Id, CancellationToken.None);
        await ReloadCompletionNoticesAsync();
        if (CompletionNotices.Count == 0) _shellProgressService.SetProgress(ShellProgressState.None);
    }

    private async void ClearCompletionNotices_Click(object sender, RoutedEventArgs e)
    {
        await _completionNoticeStore.ClearAsync(CancellationToken.None);
        await ReloadCompletionNoticesAsync();
        _shellProgressService.SetProgress(ShellProgressState.None);
    }

    private void TaskbarAttentionToggle_Toggled(object sender, RoutedEventArgs e) => SavePreferencesSoon();

    private async void ResetPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Reset saved layout and filters?", "This resets window placement, navigation, theme, workflow fields, filters, favorites, and notification preference. Scan results and device settings are not affected.")) return;
        await _preferencesStore.ResetAsync(CancellationToken.None);
        _preferences = AppPreferences.Default;
        ApplyPreferences(_preferences);
        foreach (var item in _allPolicyStates) item.IsFavorite = false;
        ApplyPolicyFilter();
        StatusText.Text = "Saved layout and filters reset";
    }

    private static string SelectedComboTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item ? Convert.ToString(item.Tag) ?? "All" : "All";

    private static void SelectComboTag(ComboBox combo, string? tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(Convert.ToString(item.Tag), tag, StringComparison.OrdinalIgnoreCase)) ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private int SelectedPerformanceDays() =>
        PerformanceRangeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var days) ? days : 30;

    private static string ReadablePage(string tag) => tag switch
    {
        "scan" => "Scan and review",
        "controls" => "Command center",
        "performance" => "Performance",
        "diagnostics" => "Diagnostics",
        "history" => "Update history",
        "sources" => "Registered sources",
        "watchlist" => "Watchlist",
        _ => tag
    };

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
