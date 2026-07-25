# WuPilot — Windows Update Workbench

WuPilot is an elevated WinUI 3 desktop application for support technicians who need to inspect Windows Update behavior on one device, compare update sources, test a selected update, and hand useful driver evidence to an Intune administrator.

The application uses the supported Windows Update Agent (WUA) COM API. Microsoft’s published script is a demonstration rather than production code; WuPilot wraps the same object model with serialized operations, error isolation per provider, explicit confirmations, normalized evidence, diagnostics, and recoverable repair actions.

## Current capabilities

- Scan one or several sources in sequence:
  - policy default
  - managed WSUS
  - public Windows Update
  - Microsoft Update
  - Microsoft Store service
  - a custom registered WUA service GUID
  - a Microsoft-signed offline `Wsusscn2.cab` security catalog
- Use safe presets for missing drivers, missing software, installed, hidden, all applicable, or advanced WUA criteria.
- Compare/deduplicate results by WUA UpdateID and revision while retaining every source that offered the update.
- Inspect title, description, KB/CVE IDs, categories, sizes, install state, severity, reboot behavior, and detailed driver metadata.
- Correlate an offered driver to the currently installed signed PnP driver using exact/family hardware identifiers, including current version/date, INF, signer, and match confidence.
- Download, install, hide, or show one revalidated update with a confirmation. WuPilot never restarts the device automatically.
- Diagnose update services, registered WUA sources, Windows Update/MDM policy registry values, WinHTTP proxy, Microsoft content DNS, WUA version/history, BITS jobs, disk space, Entra join identity, and pending restart state.
- Start required services, run DISM health operations, generate `WindowsUpdate.log`, or reset update caches. Cache reset renames existing stores to timestamped recovery paths rather than deleting them.
- Export JSON, CSV, and a human-readable HTML review bundle plus Windows Update events, CBS errors, SetupAPI driver-install evidence, BITS state, and existing SetupDiag results.

## Build and run

The WinUI XAML compiler and WUA are Windows-only. Build on Windows 10 1809 or newer with the .NET 10 SDK and the Windows application development tooling installed.

```powershell
dotnet restore WuPilot.slnx
dotnet test tests/WuPilot.Core.Tests/WuPilot.Core.Tests.csproj
dotnet build src/WuPilot.App/WuPilot.App.csproj -p:Platform=x64
dotnet run --project src/WuPilot.App/WuPilot.App.csproj -p:Platform=x64
```

Use `-p:Platform=ARM64` for native ARM64. The app is unpackaged and self-contained, and its manifest requests administrator rights because WUA secured methods and the repair tools require elevation.

For a tested self-contained output, run `./scripts/Build-WuPilot.ps1 -Platform x64`; the result is written under `artifacts/`.
The full release gate is documented in `docs/WINDOWS-VALIDATION.md`; `scripts/Test-WuaReadOnly.ps1` provides a non-mutating WUA smoke test independent of the UI.

## Technician workflow

1. Run diagnostics before changing anything. Save policy-related findings rather than immediately “fixing” intentional management settings.
2. Scan **Policy default** first to capture the managed experience.
3. For comparison, add **Windows Update**, **Microsoft Update**, or **Microsoft Store service**. Use the missing-driver preset with Windows Update to query applicable driver metadata. A direct-source scan does not defeat policy, endpoint restrictions, or Microsoft applicability rules.
4. Inspect driver manufacturer, provider, model, class, hardware ID, date, inferred version, and source.
   Compare the installed driver match and confidence before concluding the offered package is an upgrade for the intended device.
5. Optionally download or install on this single test device. Re-scan afterward.
6. Export evidence and send the bundle to the Intune administrator.
7. In Intune, match on name, manufacturer, version/date, class, and applicable devices. Use a deployment ring before broad approval.

## Intune identity caveat

WUA UpdateID/revision values are excellent local trace identifiers, but they are not guaranteed to equal the Intune `windowsDriverUpdateInventory.id`. WuPilot therefore retains WUA identity and also emits the visible fields used to find and validate the same driver in Intune. Driver version is inferred from the catalog title because the WUA driver interface exposes version date but no standalone version-string property; confirm it in Intune.

## Safety boundaries

- All WUA operations are serialized. A synchronous provider search may take time and cannot always be interrupted immediately.
- Custom criteria are length-checked and reject control characters/statement separators; WUA still performs final syntax validation.
- Installation is one update at a time, applicability is rechecked, possible interactive installers are refused, and license acceptance is explicit in the confirmation.
- No automatic restart, policy mutation, driver approval, tenant write, or silent mass deployment occurs.
- Evidence can contain device serial number, Entra device ID, tenant ID, policy data, and hardware IDs. Handle it as support data.
- Offline `Wsusscn2.cab` scans cover Microsoft security applicability metadata; Microsoft documents that the catalog contains no driver metadata or update binaries.

## Project layout

- `src/WuPilot.Core` — models, criteria, merge rules, HRESULT guidance, abstractions.
- `src/WuPilot.Infrastructure.Windows` — WUA COM adapter, device diagnostics, repairs, evidence export.
- `src/WuPilot.App` — elevated WinUI 3 user interface.
- `tests/WuPilot.Core.Tests` — portable tests for provider, criteria, merging, versions, and errors.
- `tests/WuPilot.Infrastructure.Tests` — evidence-bundle integration coverage.
- `tests/WuPilot.App.CodeChecks` — compiles the real WinUI code-behind against generated-field stubs when the native XAML compiler is unavailable.
- `scripts` — Windows build and read-only WUA validation entry points.
- `docs` — architecture, Intune handoff, and troubleshooting notes.

## Microsoft references

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
- [Using the Windows Update Agent API](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/using-the-windows-update-agent-api)
- [Searching, downloading, and installing updates](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/searching--downloading--and-installing-updates)
- [IUpdateSearcher search criteria](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search)
- [IWindowsDriverUpdate](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iwindowsdriverupdate)
- [Using WUA to scan for updates offline](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/using-wua-to-scan-for-updates-offline)
- [Windows Update log files](https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-logs)
- [Manage Windows driver updates in Intune](https://learn.microsoft.com/en-us/intune/device-updates/windows/manage-driver-updates)
