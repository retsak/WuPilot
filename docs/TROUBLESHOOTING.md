# Troubleshooting guide

## Start with evidence

Run **Refresh checks**, scan the **Policy default** source, and export a bundle before a cache reset or component repair. The bundle includes WUA history, operational events, registered update services, BITS state, recent CBS errors, the SetupAPI device-install tail, and an existing SetupDiag result. Intentional management policy can look like a local blocker; preserve it for the policy owner.

## Common comparisons

| Observation | Likely interpretation | Next check |
|---|---|---|
| Policy default works; direct source fails | Public service is blocked or unregistered by design | `DoNotConnectToWindowsUpdateInternetLocations`, proxy, endpoint policy |
| Direct source works; policy default fails | WSUS/source policy or intranet reachability issue | `UseWUServer`, `WUServer`, DNS/TLS to WSUS |
| Software scan works; driver scan is empty | No applicable driver, drivers excluded, or inventory not synchronized | hardware ID, `ExcludeWUDriversInQualityUpdate`, Intune policy inventory |
| Search succeeds; install returns not applicable | Applicability changed or prerequisite/supersedence changed | re-scan exact source, pending restart, update revision |
| `0x8024402C` | Name resolution/proxy failure | WinHTTP proxy, DNS, firewall and Microsoft endpoints |
| `0x80070005` | Access denied | elevation and WUA policy restrictions |
| `0x80240016` | Installation not allowed now | another install, servicing state, pending restart |
| Offered driver appears older/newer than Device Manager | Catalog title and installed PnP inventory disagree | hardware-ID match confidence, INF, version/date, OEM guidance |
| Security compliance must be checked without network access | Online sources unavailable | current Microsoft-signed `Wsusscn2.cab`; it does not contain drivers or payloads |

## Repair escalation order

1. Start required services.
2. Resolve DNS/proxy/source policy findings.
3. Restart when a reboot is pending and operationally safe.
4. Generate `WindowsUpdate.log` and preserve the evidence bundle.
5. Run DISM `ScanHealth`.
6. Reset the update cache only when evidence supports datastore/cache corruption. WuPilot records the timestamped recovery folders.
7. Run DISM `RestoreHealth` when component-store health requires repair and the repair source is understood.

WuPilot never clears policy, deletes recovery folders, or restarts automatically. If a reset worsens behavior, stop update services and restore the timestamped folders using your normal change-control process.
