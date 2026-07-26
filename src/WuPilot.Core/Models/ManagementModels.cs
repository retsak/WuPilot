namespace WuPilot.Core.Models;

public enum PolicyValueKind { Boolean, Integer, Text, Choice, DateTime }
public enum PolicyOwnership { Unconfigured, Local, GroupPolicy, Mdm, WindowsUx, Runtime }
public enum PolicyRisk { Normal, Elevated, High }
public enum EvidenceConfidence { Exact, High, Medium, Low, Unavailable }

public sealed record PolicyDefinition(
    string Id,
    string DisplayName,
    string Description,
    string Category,
    PolicyValueKind ValueKind,
    string? RegistryPath,
    string? RegistryValueName,
    int? Minimum = null,
    int? Maximum = null,
    IReadOnlyDictionary<string, string>? Choices = null,
    int MinimumBuild = 17763,
    bool IsMdmOnly = false,
    bool IsLegacy = false,
    bool IsPrivateUx = false,
    bool RequiresRestart = false,
    PolicyRisk Risk = PolicyRisk.Normal,
    string? DocumentationUrl = null);

public sealed record PolicyState(
    PolicyDefinition Definition,
    string? RequestedValue,
    string? EffectiveValue,
    PolicyOwnership Ownership,
    bool IsSupported,
    bool CanEdit,
    string Status);

public sealed record SettingsSnapshot(
    DateTimeOffset CollectedAt,
    int WindowsBuild,
    IReadOnlyList<PolicyState> Policies);

public sealed record SettingChange(
    string PolicyId,
    string? Value,
    bool Remove = false,
    string? ExpectedRequestedValue = null,
    bool EnforceExpectedRequestedValue = false);

public sealed record SettingChangeResult(
    Guid BatchId,
    bool Succeeded,
    string Summary,
    IReadOnlyList<PolicyState> States,
    IReadOnlyList<SettingAuditEntry> AuditEntries,
    IReadOnlyList<SettingChangeIssue>? Issues = null);

public sealed record SettingChangeIssue(string PolicyId, string Code, string Message);

public sealed record SettingAuditEntry(
    Guid Id,
    Guid BatchId,
    DateTimeOffset ChangedAt,
    string PolicyId,
    string DisplayName,
    string? BeforeValue,
    string? AfterValue,
    string? VerifiedValue,
    PolicyOwnership Ownership,
    string WindowsBuild,
    string UserSid,
    bool Succeeded,
    bool Restored,
    string Message);

public sealed record DeliveryOptimizationSnapshot(
    DateTimeOffset CollectedAt,
    string DownloadMode,
    long BytesFromHttp,
    long BytesFromCache,
    long BytesFromLanPeers,
    long BytesFromInternetPeers,
    long BytesUploaded,
    long CacheBytes,
    double? AverageDownloadMbps,
    int ActiveDownloads,
    string? ForegroundLimit,
    string? BackgroundLimit,
    string Source,
    string? Error = null)
{
    public long TotalDownloaded => BytesFromHttp + BytesFromCache + BytesFromLanPeers + BytesFromInternetPeers;
    public double PeerSavingsPercent => TotalDownloaded == 0 ? 0 :
        100d * (BytesFromLanPeers + BytesFromInternetPeers + BytesFromCache) / TotalDownloaded;
}

public sealed record OperationMetric(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Operation,
    string? UpdateId,
    int? RevisionNumber,
    string? Title,
    long? DownloadBytes,
    TimeSpan RevalidationDuration,
    TimeSpan DownloadDuration,
    TimeSpan InstallDuration,
    TimeSpan TotalDuration,
    int ResultCode,
    int HResult,
    bool RebootRequired,
    DateTimeOffset? RebootStartedAt = null,
    DateTimeOffset? BootCompletedAt = null,
    EvidenceConfidence RebootConfidence = EvidenceConfidence.Unavailable,
    EvidenceConfidence TimingConfidence = EvidenceConfidence.Exact,
    string EvidenceSource = "WuPilot monotonic timer");

public sealed record AppReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    DateTimeOffset PublishedAt,
    Uri ReleasePage,
    Uri InstallerUrl,
    Uri ChecksumUrl,
    string InstallerName,
    long Size,
    string? GitHubDigest);

public sealed record DownloadedAppUpdate(
    AppReleaseInfo Release,
    string InstallerPath,
    string Sha256,
    bool IsAuthenticodeSigned,
    bool IsSignatureValid);
