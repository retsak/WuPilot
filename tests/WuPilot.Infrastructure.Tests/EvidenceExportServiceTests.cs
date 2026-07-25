using WuPilot.Core.Models;
using WuPilot.Infrastructure.Windows.Export;

namespace WuPilot.Infrastructure.Tests;

public sealed class EvidenceExportServiceTests
{
    [Fact]
    public async Task ExportAsync_WritesMachineAndHumanReadableBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"WuPilot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var report = CreateReport() with { TechnicianNotes = "Ticket INC-4242: reproduce before remediation." };
            var service = new EvidenceExportService(root);
            var output = await service.ExportAsync(report, CreateDiagnostics(report.Device), null, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(output, "scan-report.json")));
            Assert.True(File.Exists(Path.Combine(output, "driver-review.csv")));
            Assert.True(File.Exists(Path.Combine(output, "intune-review.html")));
            Assert.True(File.Exists(Path.Combine(output, "README.txt")));
            Assert.True(File.Exists(Path.Combine(output, "update-history.csv")));
            Assert.True(File.Exists(Path.Combine(output, "windows-update-events.json")));
            var driverCsv = await File.ReadAllTextAsync(Path.Combine(output, "driver-review.csv"));
            Assert.Contains("1.2.3.4", driverCsv);
            Assert.Contains("1.0.0.0", driverCsv);
            Assert.Contains("oem42.inf", driverCsv);
            var html = await File.ReadAllTextAsync(Path.Combine(output, "intune-review.html"));
            Assert.Contains("not the Intune driver inventory ID", html);
            Assert.Contains("INC-4242", html);
            Assert.Contains("INC-4242", await File.ReadAllTextAsync(Path.Combine(output, "README.txt")));
            Assert.Contains("INC-4242", await File.ReadAllTextAsync(Path.Combine(output, "scan-report.json")));
            Assert.Contains("0x80240016", await File.ReadAllTextAsync(Path.Combine(output, "update-history.csv")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ScanReport CreateReport()
    {
        var provider = UpdateProviderDefinition.BuiltIn.Single(static item => item.Id == "windows-update");
        var installed = new InstalledDriverInfo("ACPI\\CONTOSO0001\\0", "Contoso device", "ACPI\\CONTOSO0001", null, "Firmware", "1.0.0.0", DateTimeOffset.Parse("2025-01-01"), "Contoso", "Contoso", "oem42.inf", true, "Microsoft Windows Hardware Compatibility Publisher");
        var driver = new DriverMetadata("Contoso", "Contoso", "Model 1", "Firmware", "ACPI\\CONTOSO0001", DateTimeOffset.Parse("2026-06-01"), 0, 0, false, false, [], new InstalledDriverMatch(installed, 100, "Installed hardware ID", "ACPI\\CONTOSO0001"));
        var update = new UpdateRecord(
            "12345678-1234-1234-1234-1234567890ab", 7, "Contoso - Firmware - 1.2.3.4", "Test driver", UpdateKind.Driver,
            [provider.Id], [provider.DisplayName], provider.Id, [], [], ["Drivers"], ["driver-category"], null, 100, 200,
            false, false, false, false, false, true, false, false, false, DateTimeOffset.Parse("2026-06-01"), null, null,
            1, 1, 1, 1, false, driver);
        var now = DateTimeOffset.Parse("2026-07-22T10:00:00-05:00");
        var device = new DeviceIdentity("TEST-PC", "Contoso", "Model 1", "SERIAL", "Windows 11 Enterprise", "10.0.26100", "26100", "x64", "device-id", "tenant-id");
        return new ScanReport("1.0", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), now, now, "IsInstalled=0 and Type='Driver'", device,
            [new ProviderScanResult(provider, now, now, 2, [], [update])], [update]);
    }

    private static DiagnosticSnapshot CreateDiagnostics(DeviceIdentity device)
    {
        var history = new UpdateHistoryRecord(
            DateTimeOffset.Parse("2026-07-21T09:00:00-05:00"),
            "Contoso failed update",
            "Failure",
            "12345678-1234-1234-1234-1234567890ab",
            6,
            1,
            4,
            unchecked((int)0x80240016),
            "WuPilot",
            2,
            null,
            null);
        return new DiagnosticSnapshot(
            "1.0",
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-07-22T10:00:00-05:00"),
            device,
            "10.0.26100.1",
            true,
            false,
            new Dictionary<string, string?> { ["wuauserv"] = "Running (Manual)" },
            new Dictionary<string, string?>(),
            new Dictionary<string, string?>(),
            [new DiagnosticFinding("history", "Recent failure", DiagnosticSeverity.Warning, "One update failed.")],
            new Dictionary<string, string?> { ["WindowsUpdateClientOperationalEvents"] = "[{\"Id\":20}]" },
            [history]);
    }
}
