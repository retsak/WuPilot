# Windows validation gate

The portable core, Windows-targeted infrastructure, serializers, and XML/PowerShell syntax can be verified from the current development host. The WinUI XAML compiler and Windows Update Agent require Windows. Do not call a release validated until the following matrix passes on representative Windows devices.

## Automated build

Run from an elevated PowerShell terminal:

```powershell
./scripts/Build-WuPilot.ps1 -Platform x64 -Configuration Release
```

Expected result: all tests pass and the self-contained output appears under `artifacts/WuPilot-win-x64`.

Before launching the UI, the read-only WUA adapter assumptions can be checked independently:

```powershell
./scripts/Test-WuaReadOnly.ps1 -Provider Default
./scripts/Test-WuaReadOnly.ps1 -Provider DriverCatalog
./scripts/Test-WuaReadOnly.ps1 -OfflineCab C:\Support\Wsusscn2.cab -Criteria "IsInstalled=0"
```

The offline catalog contains security metadata only and is not a driver-source test.

## UI and WUA matrix

- Launch requests UAC elevation and opens the WinUI shell without activation errors.
- Policy-default missing-driver scan completes on an Intune-managed device.
- Direct Windows Update, Microsoft Update, and driver-catalog scans either complete or retain an actionable per-source policy/network error.
- A multi-source scan deduplicates the same UpdateID/revision and lists every source that offered it.
- Cancelling a long scan reports cancellation intent and exits after the active synchronous WUA call returns.
- A driver result displays offered metadata and a matching installed device/version/date/INF/signature when the PnP identifier is present.
- A driver with no confident identifier match is labeled as such; it must not guess by manufacturer alone.
- Diagnostics display update history, services, policy, proxy, DNS, pending reboot, disk space, BITS errors, and recent HRESULT guidance.
- Evidence export produces JSON, driver CSV, history CSV, HTML, event data, registered WUA services, BITS state, CBS errors, SetupAPI tail, and SetupDiag results when present.
- Download on a disposable test VM revalidates the selected UpdateID/revision and does not install.
- Install on a disposable test VM requires confirmation, refuses interactive installers, records HRESULT/result/reboot state, and never restarts automatically.
- Hide/show changes local WUA visibility only and is corrected by a rescan.
- Cache reset on a disposable VM creates timestamped recovery paths and restarts required services without deleting the old stores.
- x64 and ARM64 publish outputs launch on their corresponding architecture.

## Intune acceptance

Give the exported HTML/CSV bundle to an Intune administrator and verify that the same driver can be found using name, manufacturer, offered version/date, class, and applicable devices. Confirm that WUA UpdateID is treated as local trace evidence rather than assumed to be the Intune inventory ID. Approval and broad deployment must remain outside WuPilot and use an Intune test ring.
