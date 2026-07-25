using WuPilot.Core.Models;

namespace WuPilot.Core.Services;

public static class PolicyCatalog
{
    private const string Wu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string Au = Wu + @"\AU";
    private const string Do = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    private const string Ux = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string UpdateDocs = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-update";
    private const string DoDocs = "https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-reference";

    public static IReadOnlyList<PolicyDefinition> All { get; } =
    [
        B("update.microsoft-products", "Receive updates for other Microsoft products", "Include Microsoft product updates with Windows Update.", "Quick controls", Ux, "AllowMUUpdateService", ux: true),
        B("update.latest", "Get the latest updates as soon as available", "Opt in to continuous innovation and early non-security improvements.", "Quick controls", Ux, "IsContinuousInnovationOptedIn", ux: true, minBuild: 22621),
        B("update.metered", "Download over metered connections", "Allow automatic Windows Update downloads on metered networks.", "Quick controls", Wu, "AllowAutoWindowsUpdateDownloadOverMeteredNetwork"),
        B("update.restart-notifications", "Restart-required notifications", "Show notifications when a restart is required.", "Quick controls", Ux, "RestartNotificationsAllowed2", ux: true),
        B("update.expedited-restart", "Get me up to date", "Allow expedited restarts outside active hours when Windows supports the UX state.", "Quick controls", Ux, "IsExpedited", ux: true, minBuild: 22000),
        B("update.smart-active-hours", "Automatically adjust active hours", "Let Windows infer active hours from device activity.", "Quick controls", Ux, "SmartActiveHoursState", ux: true),
        new("update.pause-quality-start", "Pause quality updates", "Start date for pausing quality updates.", "Quick controls", PolicyValueKind.DateTime, Wu, "PauseQualityUpdatesStartTime", DocumentationUrl: UpdateDocs),
        new("update.pause-feature-start", "Pause feature updates", "Start date for pausing feature updates.", "Quick controls", PolicyValueKind.DateTime, Wu, "PauseFeatureUpdatesStartTime", DocumentationUrl: UpdateDocs),
        I("update.active-start", "Active hours start", "Start hour, using local time (0-23).", "Quick controls", Wu, "ActiveHoursStart", 0, 23),
        I("update.active-end", "Active hours end", "End hour, using local time (0-23).", "Quick controls", Wu, "ActiveHoursEnd", 0, 23),
        B("update.exclude-drivers", "Exclude drivers from quality updates", "Do not include Windows Update drivers in quality updates.", "Update offerings", Wu, "ExcludeWUDriversInQualityUpdate"),
        I("update.defer-quality", "Quality update deferral days", "Number of days to defer quality updates.", "Update offerings", Wu, "DeferQualityUpdatesPeriodInDays", 0, 30),
        I("update.defer-feature", "Feature update deferral days", "Number of days to defer feature updates.", "Update offerings", Wu, "DeferFeatureUpdatesPeriodInDays", 0, 365),
        B("update.target-release", "Target a release", "Remain on the configured Windows product and target release.", "Update offerings", Wu, "TargetReleaseVersion", risk: PolicyRisk.Elevated),
        T("update.product-version", "Product version", "Windows product used with target release, such as Windows 11.", "Update offerings", Wu, "ProductVersion", risk: PolicyRisk.Elevated),
        T("update.target-release-info", "Target release version", "Feature release used with target release, such as 24H2.", "Update offerings", Wu, "TargetReleaseVersionInfo", risk: PolicyRisk.Elevated),
        B("update.disable-safeguards", "Disable feature-update safeguards", "Ignore known safeguard holds. Use only on test devices.", "Update offerings", Wu, "DisableWUfBSafeguards", risk: PolicyRisk.High),
        C("update.optional-content", "Optional content", "Control optional and preview content availability.", "Update offerings", Wu, "AllowOptionalContent", ("0","Disabled"), ("1","Optional content"), ("2","Optional content and CFRs"), ("3","CFRs only")),
        T("update.wsus-url", "Update service URL", "Internal WSUS service URL.", "Update source", Wu, "WUServer", risk: PolicyRisk.High),
        T("update.wsus-status-url", "Statistics server URL", "Internal WSUS reporting URL.", "Update source", Wu, "WUStatusServer", risk: PolicyRisk.High),
        B("update.use-wsus", "Use managed update service", "Use the configured WSUS service for automatic updates.", "Update source", Au, "UseWUServer", risk: PolicyRisk.High),
        B("update.block-public", "Block public Microsoft update services", "Prevent connections to public Windows Update locations.", "Update source", Wu, "DoNotConnectToWindowsUpdateInternetLocations", risk: PolicyRisk.High),
        C("update.scan-source-quality", "Quality update scan source", "Choose WSUS or Windows Update for quality updates.", "Update source", Wu, "SetPolicyDrivenUpdateSourceForQualityUpdates", ("0","WSUS"), ("1","Windows Update")),
        C("update.scan-source-feature", "Feature update scan source", "Choose WSUS or Windows Update for feature updates.", "Update source", Wu, "SetPolicyDrivenUpdateSourceForFeatureUpdates", ("0","WSUS"), ("1","Windows Update")),
        C("update.scan-source-driver", "Driver update scan source", "Choose WSUS or Windows Update for drivers.", "Update source", Wu, "SetPolicyDrivenUpdateSourceForDriverUpdates", ("0","WSUS"), ("1","Windows Update")),
        C("update.scan-source-other", "Other update scan source", "Choose WSUS or Windows Update for other updates.", "Update source", Wu, "SetPolicyDrivenUpdateSourceForOtherUpdates", ("0","WSUS"), ("1","Windows Update")),
        C("update.auto-mode", "Automatic update mode", "Configure notification, download, and scheduled installation behavior.", "Automatic updates", Au, "AUOptions", ("2","Notify before download"), ("3","Auto download / notify install"), ("4","Scheduled install"), ("5","Local admin choice")),
        B("update.disable-auto", "Disable automatic updates", "Turn off Automatic Updates policy.", "Automatic updates", Au, "NoAutoUpdate", risk: PolicyRisk.High),
        I("update.schedule-day", "Scheduled install day", "0 is every day; 1-7 is Sunday-Saturday.", "Automatic updates", Au, "ScheduledInstallDay", 0, 7),
        I("update.schedule-time", "Scheduled install hour", "Scheduled installation hour (0-23).", "Automatic updates", Au, "ScheduledInstallTime", 0, 23),
        I("update.quality-deadline", "Quality update deadline", "Days before quality updates are enforced.", "Restart and deadlines", Wu, "ConfigureDeadlineForQualityUpdates", 0, 30),
        I("update.feature-deadline", "Feature update deadline", "Days before feature updates are enforced.", "Restart and deadlines", Wu, "ConfigureDeadlineForFeatureUpdates", 0, 30),
        I("update.deadline-grace", "Deadline grace period", "Grace days before an enforced restart.", "Restart and deadlines", Wu, "ConfigureDeadlineGracePeriod", 0, 7),
        B("update.no-reboot-active", "No automatic restart during active hours", "Prevent update restarts during active hours.", "Restart and deadlines", Wu, "NoAutoRebootWithLoggedOnUsers"),
        C("update.notification-level", "Update notification level", "Control update and restart notifications.", "Notifications", Wu, "UpdateNotificationLevel", ("0","Default"), ("1","Disable except restart warnings"), ("2","Disable all")),
        B("update.disable-pause-ui", "Disable pause controls", "Prevent users from pausing updates in Windows Settings.", "User experience", Wu, "SetDisablePauseUXAccess"),
        B("update.disable-update-ui", "Disable Windows Update UX", "Remove user access to Windows Update scanning and controls.", "User experience", Wu, "SetDisableUXWUAccess", risk: PolicyRisk.High),
        new("do.mode", "Delivery Optimization download mode", "Choose HTTP-only, LAN, group, internet, simple, or bypass behavior.", "Delivery Optimization", PolicyValueKind.Choice, Do, "DODownloadMode", Choices: new Dictionary<string, string> { ["0"]="HTTP only", ["1"]="LAN peers", ["2"]="Group peers", ["3"]="Internet peers", ["99"]="Simple", ["100"]="Bypass" }, Risk: PolicyRisk.Elevated, DocumentationUrl: DoDocs),
        I("do.background-kbps", "Maximum background bandwidth", "Absolute background bandwidth limit in KB/s; 0 is dynamic.", "Delivery Optimization", Do, "DOMaxBackgroundDownloadBandwidth", 0, 4_294_967),
        I("do.foreground-kbps", "Maximum foreground bandwidth", "Absolute foreground bandwidth limit in KB/s; 0 is dynamic.", "Delivery Optimization", Do, "DOMaxForegroundDownloadBandwidth", 0, 4_294_967),
        I("do.background-percent", "Maximum background percentage", "Percentage of measured bandwidth usable in background.", "Delivery Optimization", Do, "DOPercentageMaxBackgroundBandwidth", 0, 100),
        I("do.foreground-percent", "Maximum foreground percentage", "Percentage of measured bandwidth usable in foreground.", "Delivery Optimization", Do, "DOPercentageMaxForegroundBandwidth", 0, 100),
        I("do.upload-cap", "Monthly upload cap", "Monthly peer upload cap in GB.", "Delivery Optimization", Do, "DOMonthlyUploadDataCap", 0, 100000),
        I("do.cache-percent", "Maximum cache size", "Delivery Optimization cache size as a percentage of disk.", "Delivery Optimization", Do, "DOMaxCacheSize", 1, 100),
        I("do.cache-age", "Maximum cache age", "Maximum content cache age in seconds.", "Delivery Optimization", Do, "DOMaxCacheAge", 0, int.MaxValue),
        I("do.min-file", "Minimum file size to cache", "Minimum peer-cacheable file size in MB.", "Delivery Optimization", Do, "DOMinFileSizeToCache", 0, 100000),
        T("do.group-id", "Peer group ID", "GUID used to create an explicit peer group.", "Delivery Optimization", Do, "DOGroupID", risk: PolicyRisk.Elevated),
        T("do.cache-host", "Connected Cache host", "Microsoft Connected Cache hostname.", "Delivery Optimization", Do, "DOCacheHost", risk: PolicyRisk.Elevated),
        B("do.disallow-cache-vpn", "Disallow Connected Cache over VPN", "Prevent Connected Cache downloads while connected through VPN.", "Delivery Optimization", Do, "DODisallowCacheServerDownloadsOnVPN", minBuild: 22621),
        new("mdm.maintenance-window", "Maintenance window policies", "Effective MDM maintenance-window configuration.", "MDM-only", PolicyValueKind.Text, null, null, IsMdmOnly: true, DocumentationUrl: UpdateDocs),
        new("mdm.pause-start", "MDM pause start times", "Effective quality and feature pause state from MDM.", "MDM-only", PolicyValueKind.Text, null, null, IsMdmOnly: true, DocumentationUrl: UpdateDocs),
        new("legacy.dual-scan", "Disable dual scan (legacy)", "Legacy dual-scan policy retained for evidence.", "Legacy", PolicyValueKind.Boolean, Wu, "DisableDualScan", IsLegacy: true, DocumentationUrl: UpdateDocs)
    ];

    private static PolicyDefinition B(string id, string name, string description, string category, string path, string value, bool ux = false, int minBuild = 17763, PolicyRisk risk = PolicyRisk.Normal) =>
        new(id, name, description, category, PolicyValueKind.Boolean, path, value, 0, 1, MinimumBuild: minBuild, IsPrivateUx: ux, Risk: risk, DocumentationUrl: category == "Delivery Optimization" ? DoDocs : UpdateDocs);
    private static PolicyDefinition I(string id, string name, string description, string category, string path, string value, int min, int max, int minBuild = 17763) =>
        new(id, name, description, category, PolicyValueKind.Integer, path, value, min, max, MinimumBuild: minBuild, DocumentationUrl: category == "Delivery Optimization" ? DoDocs : UpdateDocs);
    private static PolicyDefinition T(string id, string name, string description, string category, string path, string value, PolicyRisk risk = PolicyRisk.Normal) =>
        new(id, name, description, category, PolicyValueKind.Text, path, value, Risk: risk, DocumentationUrl: category == "Delivery Optimization" ? DoDocs : UpdateDocs);
    private static PolicyDefinition C(string id, string name, string description, string category, string path, string value, params (string Value, string Label)[] choices) =>
        new(id, name, description, category, PolicyValueKind.Choice, path, value, Choices: choices.ToDictionary(static choice => choice.Value, static choice => choice.Label), DocumentationUrl: category == "Delivery Optimization" ? DoDocs : UpdateDocs);
    private static PolicyDefinition C(string id, string name, string description, string category, string path, string value, (string,string) a, (string,string) b, (string,string) c, (string,string) d, PolicyRisk risk = PolicyRisk.Normal) =>
        new(id, name, description, category, PolicyValueKind.Choice, path, value, Choices: new Dictionary<string,string>{{a.Item1,a.Item2},{b.Item1,b.Item2},{c.Item1,c.Item2},{d.Item1,d.Item2}}, Risk: risk, DocumentationUrl: category == "Delivery Optimization" ? DoDocs : UpdateDocs);
}
