namespace WuPilot.Core.Models;

public sealed record UpdateSourceRegistration(
    string Name,
    string ServiceId,
    bool IsManaged,
    bool IsDefaultAuService,
    bool IsScanPackageService,
    bool OffersWindowsUpdates);
