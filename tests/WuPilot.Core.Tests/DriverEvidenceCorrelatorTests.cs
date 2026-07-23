using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.Core.Tests;

public sealed class DriverEvidenceCorrelatorTests
{
    [Fact]
    public void FindBestMatch_PrefersExactHardwareId()
    {
        var exact = Installed("PCI\\VEN_8086&DEV_1234", "2.0.0.0", "2026-01-01");
        var family = Installed("PCI\\VEN_8086&DEV_1234&SUBSYS_0001", "3.0.0.0", "2026-02-01");

        var match = DriverEvidenceCorrelator.FindBestMatch(Offered("PCI\\VEN_8086&DEV_1234"), [family, exact]);

        Assert.NotNull(match);
        Assert.Equal(100, match.Confidence);
        Assert.Equal("2.0.0.0", match.Driver.DriverVersion);
        Assert.Equal("Installed hardware ID", match.MatchedOn);
    }

    [Fact]
    public void FindBestMatch_MatchesPnPInstancePrefix()
    {
        var installed = Installed(null, "1.0.0.0", "2025-01-01") with
        {
            DeviceId = "USB\\VID_1234&PID_5678\\ABCDEF"
        };

        var match = DriverEvidenceCorrelator.FindBestMatch(Offered("USB\\VID_1234&PID_5678"), [installed]);

        Assert.NotNull(match);
        Assert.Equal(92, match.Confidence);
        Assert.Equal("PnP device instance prefix", match.MatchedOn);
    }

    [Fact]
    public void FindBestMatch_DoesNotGuessFromManufacturerAlone()
    {
        var installed = Installed("PCI\\VEN_9999&DEV_0001", "1.0.0.0", "2025-01-01") with { Manufacturer = "Contoso" };
        var match = DriverEvidenceCorrelator.FindBestMatch(Offered("PCI\\VEN_1234&DEV_5678"), [installed]);
        Assert.Null(match);
    }

    [Fact]
    public void Enrich_AttachesMatchWithoutChangingNonDriverUpdate()
    {
        var driverUpdate = Update(Offered("ACPI\\CONTOSO0001"));
        var softwareUpdate = driverUpdate with { Kind = UpdateKind.Software, Driver = null, UpdateId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" };

        var enriched = DriverEvidenceCorrelator.Enrich([driverUpdate, softwareUpdate], [Installed("ACPI\\CONTOSO0001", "4.5.6.7", "2026-01-01")]);

        Assert.Equal("4.5.6.7", enriched[0].Driver?.InstalledMatch?.Driver.DriverVersion);
        Assert.Null(enriched[1].Driver);
    }

    private static DriverMetadata Offered(string hardwareId) =>
        new("Contoso", "Contoso", "Model", "System", hardwareId, DateTimeOffset.Parse("2026-06-01"), 0, 0, false, false, []);

    private static InstalledDriverInfo Installed(string? hardwareId, string version, string date) =>
        new(hardwareId, "Contoso device", hardwareId, null, "System", version, DateTimeOffset.Parse(date), "Contoso", "Contoso", "oem42.inf", true, "Microsoft Windows Hardware Compatibility Publisher");

    private static UpdateRecord Update(DriverMetadata driver) =>
        new("12345678-1234-1234-1234-1234567890ab", 1, "Contoso - System - 5.0.0.0", "Driver", UpdateKind.Driver,
            ["driver-catalog"], ["Driver catalog"], "driver-catalog", [], [], ["Drivers"], [], null, 1, 2, false, false, false,
            false, false, true, false, false, false, null, null, null, 1, 1, 0, 1, false, driver);
}
