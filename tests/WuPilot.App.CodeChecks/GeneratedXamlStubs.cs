// This file supplies only the fields normally emitted by the Windows-only XAML compiler.
// It lets non-Windows CI compile the real code-behind against WinUI reference assemblies.
using Microsoft.UI.Xaml.Controls;

namespace WuPilot.App;

public partial class App
{
    private void InitializeComponent() { }
}

public sealed partial class MainWindow
{
    private readonly Grid RootGrid = null!;
    private readonly Grid AppTitleBar = null!;
    private readonly Grid ScanView = null!;
    private readonly Grid DiagnosticsView = null!;
    private readonly Grid ActivityView = null!;
    private readonly Grid AboutView = null!;
    private readonly ComboBox PresetCombo = null!;
    private readonly TextBox CustomCriteriaBox = null!;
    private readonly TextBox CustomServiceIdBox = null!;
    private readonly TextBox OfflineCabPathBox = null!;
    private readonly CheckBox SupersededCheck = null!;
    private readonly TextBox FilterBox = null!;
    private readonly ListView UpdatesList = null!;
    private readonly Button ScanButton = null!;
    private readonly Button CancelButton = null!;
    private readonly Button DownloadButton = null!;
    private readonly Button InstallButton = null!;
    private readonly Button HideButton = null!;
    private readonly TextBlock DetailTitle = null!;
    private readonly TextBlock DetailDescription = null!;
    private readonly TextBlock DetailIdentity = null!;
    private readonly TextBlock DetailSources = null!;
    private readonly TextBlock DetailManufacturer = null!;
    private readonly TextBlock DetailModel = null!;
    private readonly TextBlock DetailHardware = null!;
    private readonly TextBlock DetailDate = null!;
    private readonly TextBlock DetailInstalled = null!;
    private readonly TextBlock DetailSignature = null!;
    private readonly TextBlock DetailCategories = null!;
    private readonly InfoBar DetailWarning = null!;
    private readonly ProgressRing BusyRing = null!;
    private readonly TextBlock StatusText = null!;
    private readonly TextBlock ProgressText = null!;

    private void InitializeComponent() { }
}
