using Microsoft.UI.Xaml;

namespace WuPilot.App;

public partial class App : Application
{
    private Window? _window;
    private static readonly string StartupLogPath = Path.Combine(AppContext.BaseDirectory, "WuPilot.startup.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
            LogStartupException(args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception exception)
        {
            LogStartupException(exception);
            throw;
        }
    }

    private static void LogStartupException(Exception exception)
    {
        try
        {
            File.AppendAllText(
                StartupLogPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never replace the original activation failure.
        }
    }
}
