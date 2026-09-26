using WuPilot.Infrastructure.Windows.Export;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("WuPilot-Collector --destination <absolute folder or UNC share> [--input <existing logs folder>] [--parallel <1..8>] [--time-zone <Windows time zone ID>]\nUse --report-only --input <existing bundle> --destination <report folder> to regenerate report.html and phase-summary.json without collecting or zipping logs.\nCollects logs and writes HTML/JSON/CSV reports plus a ZIP. Run elevated for protected live logs. Ctrl+C cancels. No updates are installed and no restart is initiated.\nExit codes: 0 delivered, 2 invalid arguments/failure, 3 local ZIP retained but delivery failed, 4 delivered with missing/limited sources, 130 cancelled.");
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    string? destination = null, input = null, timeZone = null;
    var reportOnly = false;
    var parallel = 4;
    for (var i = 0; i < args.Length; i++)
    {
        var option = args[i];
        if (option == "--report-only") { reportOnly = true; continue; }
        if (++i >= args.Length) throw new ArgumentException($"Missing value for {option}.");
        switch (option)
        {
            case "--destination": destination = args[i]; break;
            case "--input": input = args[i]; break;
            case "--parallel": parallel = int.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture); break;
            case "--time-zone": timeZone = args[i]; break;
            default: throw new ArgumentException($"Unknown option {option}.");
        }
    }
    if (destination is null) throw new ArgumentException("--destination is required.");
    if (reportOnly)
    {
        if (input is null) throw new ArgumentException("--report-only requires --input <existing bundle>.");
        var report = await UpgradeReportService.RegenerateAsync(input, destination, timeZone, cancellation.Token);
        Console.WriteLine($"Report: {report}");
        return 0;
    }
    var result = await new LogCollectionService().CollectAsync(new(destination, input, parallel, SourceTimeZoneId: timeZone), new ConsoleProgress(), cancellation.Token);
    Console.WriteLine($"Local ZIP: {result.LocalZip}\nDelivered ZIP: {result.DeliveredZip}\nReport: {result.ReportPath}\nCollected: {result.Collected}; missing/limited: {result.Unavailable}");
    if (result.DeliveryError is not null) { Console.Error.WriteLine(result.DeliveryError); return 3; }
    return result.Unavailable > 0 ? 4 : 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Partial files may remain in LocalAppData/WuPilot/LogBundles."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
