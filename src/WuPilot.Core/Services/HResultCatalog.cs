namespace WuPilot.Core.Services;

public sealed record HResultExplanation(string Code, string Name, string Explanation, string Recommendation);

public static class HResultCatalog
{
    private static readonly IReadOnlyDictionary<uint, HResultExplanation> Known =
        new Dictionary<uint, HResultExplanation>
        {
            [0x8024000C] = new("0x8024000C", "WU_E_NOOP", "No operation was required.", "Refresh the scan and verify the update is still applicable."),
            [0x80240009] = new("0x80240009", "WU_E_OPERATIONINPROGRESS", "Another conflicting update operation is already in progress.", "Wait for the active scan, download, installation, or servicing operation to finish, then retry."),
            [0x80240016] = new("0x80240016", "WU_E_INSTALL_NOT_ALLOWED", "Installation is not allowed at this time.", "Check for another install, restart requirements, and Windows Update policy."),
            [0x80240017] = new("0x80240017", "WU_E_NOT_APPLICABLE", "The update is not applicable to this device.", "Re-scan the same provider and compare hardware IDs or prerequisites."),
            [0x8024001E] = new("0x8024001E", "WU_E_SERVICE_STOP", "The Windows Update service stopped.", "Start wuauserv and dependent services, then scan again."),
            [0x80240020] = new("0x80240020", "WU_E_NO_INTERACTIVE_USER", "The operation requires an interactive user.", "Run WuPilot interactively as administrator."),
            [0x8024001F] = new("0x8024001F", "WU_E_NO_CONNECTION", "The network connection was unavailable.", "Check network state, proxy, DNS, and the selected update source."),
            [0x80240021] = new("0x80240021", "WU_E_TIME_OUT", "The update operation timed out.", "Check source reachability and retry after other servicing activity completes."),
            [0x80240022] = new("0x80240022", "WU_E_ALL_UPDATES_FAILED", "Every update in the operation failed.", "Review the per-update result, Windows Update history, CBS errors, and the generated support bundle."),
            [0x80240025] = new("0x80240025", "WU_E_USER_ACCESS_DISABLED", "Group Policy prevented Windows Update access.", "Confirm the intended Windows Update policy with the Intune or Group Policy owner."),
            [0x8024002E] = new("0x8024002E", "WU_E_WU_DISABLED", "Access to an unmanaged update server is not allowed.", "Use the managed/policy-default source or confirm whether direct Microsoft scans are intentionally blocked."),
            [0x8024200D] = new("0x8024200D", "WU_E_UH_NEEDANOTHERDOWNLOAD", "The update handler needs the payload to be downloaded again.", "Re-download the update; if it recurs, inspect BITS state and cache evidence before resetting the cache."),
            [0x80242017] = new("0x80242017", "WU_E_UH_NEW_SERVICING_STACK_REQUIRED", "A newer servicing stack is required.", "Install current servicing stack/prerequisite updates and restart if required before retrying."),
            [0x8024401C] = new("0x8024401C", "WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT", "The update endpoint timed out.", "Check proxy, firewall, DNS, and Microsoft update endpoint reachability."),
            [0x80244011] = new("0x80244011", "WU_E_PT_SUS_SERVER_NOT_SET", "Managed WSUS is selected but the WUServer policy value is missing.", "Repair the WSUS policy assignment rather than substituting an unmanaged source without approval."),
            [0x8024401B] = new("0x8024401B", "WU_E_PT_HTTP_STATUS_PROXY_AUTH_REQ", "The proxy requires authentication.", "Review WinHTTP proxy configuration and the service-account authentication path."),
            [0x80244022] = new("0x80244022", "WU_E_PT_HTTP_STATUS_SERVICE_UNAVAIL", "The update service returned HTTP 503.", "Retry later and verify whether a proxy or WSUS upstream is overloaded."),
            [0x8024402C] = new("0x8024402C", "WU_E_PT_WINHTTP_NAME_NOT_RESOLVED", "The update service name could not be resolved.", "Check DNS and WinHTTP proxy configuration."),
            [0x80246005] = new("0x80246005", "WU_E_DM_NONETWORK", "The download manager has no network connection.", "Confirm network profile, proxy, firewall, and update endpoint access."),
            [0x80246008] = new("0x80246008", "WU_E_DM_FAILTOCONNECTTOBITS", "Windows Update could not connect to BITS.", "Check the BITS service and review bits-jobs.json before changing jobs or cache state."),
            [0x80246009] = new("0x80246009", "WU_E_DM_BITSTRANSFERERROR", "BITS reported a transfer error.", "Inspect BITS job error details, proxy, and content endpoint access."),
            [0x80247001] = new("0x80247001", "WU_E_OL_INVALID_SCANFILE", "The offline scan package is invalid.", "Use the latest Microsoft-signed Wsusscn2.cab and verify it was transferred without corruption."),
            [0x80247002] = new("0x80247002", "WU_E_OL_NEWCLIENT_REQUIRED", "The offline scan package requires a newer Windows Update Agent.", "Bring the device servicing stack and Windows Update Agent current before retrying the offline scan."),
            [0x8024800F] = new("0x8024800F", "WU_E_DS_STOREFILELOCKED", "The Windows Update data store is locked by another process.", "Wait for servicing activity to finish and identify the locking process before considering a recoverable cache reset."),
            [0x80248014] = new("0x80248014", "WU_E_DS_UNKNOWNSERVICE", "The selected update service is not registered with Windows Update Agent.", "Enable or register the intended service through approved Windows Update settings or policy. WuPilot does not change service registration during a read-only scan."),
            [0x8024801C] = new("0x8024801C", "WU_E_DS_RESETREQUIRED", "The Windows Update data-store session must be reset.", "Start a new scan session; if it persists, preserve evidence before a recoverable cache reset."),
            [0x8024C006] = new("0x8024C006", "WU_E_DRV_SYNC_FAILED", "Windows Update driver synchronization failed.", "Review SetupAPI, WUA events, PnP state, and Windows Update driver-source reachability."),
            [0x8024A105] = new("0x8024A105", "WU_E_AU_NO_REGISTERED_SERVICE", "No unmanaged update service is registered.", "Use the policy-default source or register/enable Microsoft Update."),
            [0x80070005] = new("0x80070005", "E_ACCESSDENIED", "The operation was denied.", "Restart WuPilot as administrator and check update policy restrictions."),
            [0x80072EE7] = new("0x80072EE7", "WININET_E_NAME_NOT_RESOLVED", "A server name could not be resolved.", "Check DNS, proxy bypass, and required Windows Update endpoints."),
            [0x800F081F] = new("0x800F081F", "CBS_E_SOURCE_MISSING", "Component repair source files were not found.", "Configure a repair source or allow Windows Update as the repair source.")
        };

    public static HResultExplanation Explain(int hResult)
    {
        var unsigned = unchecked((uint)hResult);
        return Known.TryGetValue(unsigned, out var explanation)
            ? explanation
            : new HResultExplanation($"0x{unsigned:X8}", "Unknown", "Windows Update returned an unrecognized HRESULT.", "Export the support bundle and look up the HRESULT in Microsoft Windows Update error documentation.");
    }
}
