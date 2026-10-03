# Upgrade log collection and standalone release

In **Performance → Upgrade logs and reports**, enter a local folder or UNC path such as `\\server\support\WuPilot`, then select **Collect and ZIP**. No scan is needed. Optional **Analyze existing logs** accepts a folder of logs from another device; it does not collect this device's Windows events or mix in its operation metrics. Extract a received bundle first to analyze its logs offline.

Collection runs independently of scans and update actions, with four file workers by default. WUA servicing calls retain their serialization. **Cancel** stops collection at an I/O boundary. Closing the desktop app is blocked while collection runs. Current account credentials are used for network access; elevated sessions may not have mapped drives, so prefer UNC paths.

Each run has a unique local folder and ZIP in `%LocalAppData%\WuPilot\LogBundles`. The collector stages locally, then copies a `.partial` file to the destination and renames it only after copying finishes. If delivery fails or is cancelled, the local ZIP remains available for retry. An abandoned destination `.partial` is not a completed bundle. SMB calls can remain pending until Windows' network timeout; cancellation is cooperative and is not a hard deadline for kernel/network I/O. Retained bundles are not automatically removed.

Sources include Panther, `$WINDOWS.~BT\Sources\Panther`, rollback, MoSetup, existing SetupDiag output, CBS, DISM, SetupAPI, Windows Update ETLs and retained WuPilot operation metrics. System, Setup and WindowsUpdateClient event channels are exported for their full retained history, with no 30-day cutoff. Current and Windows.old Windows directories are checked, including retained event files. Both the drive-root and Windows.old copies of `$WINDOWS.~BT\Sources\Panther` and `Rollback` are probed. CBS/DISM archives, `.bak` logs, `.cab` archives and rollback `.dmp` files are retained; binary archives/dumps are not decoded. Raw ETLs/EVTX are preserved for specialist tools; the collector does not decode ETLs or execute/download SetupDiag. Microsoft documents the [setup log locations](https://learn.microsoft.com/en-us/windows/deployment/upgrade/log-files).

The manifest records missing, denied and limited sources, file-copy time, bytes and hashes for copied files. Files are copied with sharing enabled and bounded to their initial length; live logs are not transactionally consistent. Defaults cap files at 512 MiB each and 1,000 files, skip directory links and retain up to 1,000 findings per file. Text analysis streams the full collected files by default. Event exports are bounded by a 45-second command timeout rather than the source-file size cap. Inspect the manifest before treating any collection as complete.

## Reports

The HTML report now leads with the latest recognizable OS update attempt and three phase cards: **Download**, **Install → pending reboot**, and **Reboot → update finished**. Waiting between pending reboot and the restart is a separate row. Durations use minutes/seconds, and all displayed event times use the selected source time zone. Other operations, raw log activity, keyword findings and the collection manifest are expandable supporting evidence.

UpdateAgent download requests and DownloadComplete markers define the download/preparation estimate; this includes preparation and retries. Install begins at `Install: Enter` and ends at the first live `Reboot required: [TRUE]` transition. Deserialized reboot flags and later post-reboot result queries are not treated as new phase boundaries. The reboot phase spans restart initiation through a successful matching cumulative package's Installed event in Setup.evtx, including intermediate boots. A plain kernel startup, another package's completion, or normal power-off does not complete this phase. The association between an update and a system restart is chronological and labeled estimated. A cumulative package completion must not be treated as proof that an entire feature upgrade finished.

Recognized markers are derived from the inspected UpdateAgent log format. Unknown formats or missing boundaries stay unconfirmed. The implementation also preserves the older setup logs as supporting evidence; it does not attach them to an unrelated newer update merely because both appear in one bundle. The latest attempt is not necessarily the latest update on the device if newer evidence is missing.

Regenerate an existing bundle without recollecting or zipping raw logs:

```powershell
WuPilot-Collector.exe --report-only --input 'C:\Support\Bundle' --destination 'C:\Support\ImprovedReport' --time-zone 'Central Standard Time'
```

This writes `report.html` and `phase-summary.json`. Specify the source device's Windows time zone ID when analyzing another device. With no override, report-only mode reuses the bundle's phase-summary time zone when available; otherwise the analysis machine's local zone is used and shown in the report. Ambiguous or invalid daylight-saving text timestamps are not correlated. Existing raw source files are unchanged.

- `report.html`: offline human-readable timing tables, setup activity windows, findings and collection status. All source text is HTML-escaped.
- `timings.csv`: operation download, install, total and shutdown-to-boot seconds, confidence and result codes.
- `operation-metrics.json`: structured timing records including evidence sources.
- `analysis.json`: parsed timestamp windows and error/rollback keyword findings with source line numbers.
- `manifest.json` and `logs/`: original source paths, collection outcomes and raw snapshots.

WuPilot's monotonic timings measure its WUA calls, including revalidation in the total. They are not the complete duration of offline feature upgrade servicing. Event correlations require matching identities and are estimates. Reboot evidence measures shutdown to kernel boot, not desktop readiness, and does not prove update completion. Setup log windows show observed activity per file and recognizable phase marker; unknown phases remain unclassified. They must not be summed into an upgrade duration. Missing boundaries remain unavailable. Setup timestamps are local wall-clock times; regressions and gaps over six hours split windows. Keyword errors may be benign and are not automatic root-cause diagnoses. Review SetupDiag and raw logs for confirmation.

## Standalone collector

### Standalone PowerShell report generator

Use [`scripts/New-WuPilotUpdateReport.ps1`](../scripts/New-WuPilotUpdateReport.ps1) to collect evidence and generate reports without WuPilot, the collector executable, or the .NET SDK. Copy the single script to another Windows machine and run it with Windows PowerShell 5.1 or PowerShell 7. For live collection, open an elevated PowerShell console and omit `InputPath`:

```powershell
.\New-WuPilotUpdateReport.ps1 -Destination 'C:\Support\Reports' -OpenReport
.\New-WuPilotUpdateReport.ps1 -Destination '\\server\support\WuPilot' -Update KB5124010 -MaxFiles 3000 -MaxFileMB 1024
```

Live collection probes Panther, rollback, MoSetup (including UpdateAgent), CBS, DISM, SetupAPI, Windows Update logs, existing SetupDiag output, and retained Windows.old locations. It exports full retained System, Setup, and WindowsUpdateClient Operational event history with a 45-second timeout per channel. WuPilot operation metrics are included when present; WuPilot does not need to be installed. ETLs, CABs and dumps are retained without decoding, and SetupDiag is not executed or downloaded.

Each collection creates a unique folder and ZIP under `%LocalAppData%\WuPilot\LogBundles`. Override that location with `StagingDirectory`. The script copies files with read/write/delete sharing, bounds each copy to its initial length, skips file/directory links, and records source paths, outcomes, bytes, copy time and SHA-256 hashes in `manifest.json`. Defaults are 1,000 files and 512 MiB per source file; live event exports are separate from these limits. Supporting text analysis scans collected `.log` files, records observed activity windows and retains up to 1,000 review findings per file. Those windows are evidence coverage, not update durations.

The script writes the reports into the staged bundle, creates the local ZIP, then copies it to `Destination` using a `.partial` file. It verifies the copied ZIP's SHA-256 before renaming it. Delivery failures issue a warning and retain the complete local ZIP; destination `.partial` files are incomplete. Ctrl+C stops work at a PowerShell boundary; partial staging files may remain. Network I/O can wait for the operating system's timeout. Review manifest warnings for missing, denied or limited sources before treating collection as complete.

To package logs from another device without collecting this device's events, use `Collect` with `InputPath`:

```powershell
.\New-WuPilotUpdateReport.ps1 -Collect -InputPath 'C:\Support\ReceivedLogs' -Destination 'C:\Support\Bundles' -TimeZoneId 'Central Standard Time'
```

For report-only regeneration, supply `InputPath` without `Collect`:

```powershell
.\New-WuPilotUpdateReport.ps1 -InputPath 'C:\Support\Bundle' -Destination 'C:\Support\Report' -TimeZoneId 'Central Standard Time' -Update KB5124010 -OpenReport
```

`InputPath` accepts an extracted bundle or a directory containing UpdateAgent logs and exported EVTX files. In report-only mode, `Destination` must be a different directory. The script writes `report.html`, `phase-summary.json`, and `update-timings.csv`. Omit `Update` to include all recognized attempts; supply a KB, bare KB number, or full update GUID to filter. `OpenReport` opens the completed HTML in the default browser.

Raw UpdateAgent and EVTX snapshots are reanalyzed, including rotated UpdateAgent logs. The HTML includes supporting analysis, filtered operation metrics, and the collection manifest. Report-only mode displays supporting analysis as saved; collection rebuilds it from available `.log` snapshots. If no raw UpdateAgent files are listed or found, a saved phase summary can supply timings, with an explicit warning. Missing snapshots and unreadable event files produce warnings; missing boundaries remain unavailable. The script reuses the saved source time zone unless overridden, otherwise uses the local zone. Offline analysis normally needs no elevation.

Run `tests/Test-StandaloneUpdateReport.ps1` for synthetic collection/report regression checks on either supported PowerShell version.

The next release workflow publishes self-contained, single-file x64 and ARM64 collectors, full portable desktop ZIPs, and SHA-256 sidecars. The collector requires neither the SDK nor a WuPilot installation. Use an elevated console for protected live logs; offline analysis usually needs no elevation.

```powershell
.\WuPilot-Collector-0.4.0-win-x64.exe --destination '\\server\support\WuPilot'
.\WuPilot-Collector-0.4.0-win-x64.exe --input 'C:\Support\UpgradeLogs' --destination 'C:\Support\Reports' --parallel 4
```

Exit codes: `0` delivered, `2` invalid arguments or failure, `3` local ZIP retained but delivery failed, `4` delivered with missing/limited sources, `130` cancelled before delivery. Ctrl+C requests cancellation. Logs contain device identifiers and potentially user paths; choose an appropriately restricted destination.

Build locally with `./scripts/Build-WuPilotStandalone.ps1 -Platform x64` (or `arm64`). Full desktop portable packages must retain all published resources beside WuPilot.exe; the collector is the single-file binary.

## Concurrency review

Metric writers also acquire a cross-process file lock with cancellable retries for up to ten seconds. Locks are released before event queries. A read-only collector does not acquire a writer lock or modify the store.

Performance refresh requests share one in-flight refresh, preventing out-of-order refresh results and repeated Windows event queries. Metric file access shares a per-path gate across service instances in a process; Windows event queries run outside that gate, and reboot updates merge into the latest records. The standalone collector reads metrics without writing to the app's store. Export folders and temporary files are unique per run. Independent file collection uses bounded asynchronous workers and does not acquire WUA gates. External-process stdout and stderr are drained concurrently with linked cancellation and timeouts. Diagnostics cancellation is handled without escaping an async UI event handler. Background application update checks no longer clear another operation's busy state. These changes reduce lock contention and known hang paths; they do not guarantee arbitrary Windows servicing or network calls will always return promptly.

## Validation for 0.4.0

Validated locally: 49 core tests and 19 infrastructure tests; WinUI XAML/application build; self-contained x64 and ARM64 collector publish; x64 collector end-to-end collection, analysis, ZIP creation and delivery using synthetic logs. Regression tests cover identity-safe event correlation, duplicate completion boundaries, parallel collections, concurrent metric writes, contended lock cancellation, subprocess pipe saturation/timeouts, destination failures, HTML escaping and parser limits.

Not validated here: real SMB credentials/connectivity, a live feature upgrade across reboot, ARM64 execution, and interactive visual review of the updated desktop UI. No release tag has been pushed. Perform those deployment checks before publishing.

## Selecting an earlier update

Enter a KB (with or without `KB`) or a full update GUID in the Performance filter. This searches all retained operation metrics, overriding the date range while a selection is entered. Clear it to restore the date range. Delivery Optimization cards remain device-wide statistics. KB matching uses whole KB identifiers in recorded titles; GUID matching uses update identity. Unknown identities or missing evidence produce no matching timing, never the latest unrelated update.

The same selection applies to the collected HTML report, phase-summary JSON and timings CSV. Raw logs and operation-metrics.json retain all evidence so another update can be investigated later. Reports show all matching attempts with dates; each attempt keeps its own boundaries. The standalone collector accepts `--update KB5129195` for collection or `--report-only`. Omit `--update` to show all recognized attempts. CSV includes update IDs.

```powershell
WuPilot-Collector.exe --report-only --input 'C:\Support\Bundle' --destination 'C:\Support\EarlierUpdate' --update KB5129195
WuPilot-Collector.exe --destination 'C:\Support\Logs' --update KB5129195 --max-files 3000 --max-file-mb 1024
```

## Post-upgrade location research

Microsoft's [Windows upgrade log reference](https://learn.microsoft.com/en-us/windows/deployment/upgrade/log-files) documents phase-dependent locations: downlevel and rollback under `$WINDOWS.~BT`, and post-OOBE logs under `Windows\Panther`. It also identifies rollback dumps and event files as diagnostic evidence. This does **not** establish that the entire `$WINDOWS.~BT` directory always moves into Windows.old. WuPilot therefore probes both locations and preserves original source paths in the manifest instead of assuming relocation. Retained Windows.old logs are supporting historical evidence, not automatically proof of the selected update's timing.

Deleted/rotated logs cannot be recovered by collection. Missing locations, access failures and size/count limits are recorded in the manifest; inspect coverage before calling a bundle complete. Use the standalone limit options for larger collections. No validation here establishes end-to-end timing across a real feature upgrade; unrecognized phase boundaries remain unavailable.

## Rotated logs and historical Performance

Timing analysis recognizes current, `.Old.log`, dated `.log`, `.log.N`, and `.bak` UpdateAgent logs. Collection includes these names even when the final extension is numeric. Timing and supporting-log analysis stream full files by default. Report regeneration refreshes supporting analysis from available `.log` snapshots, recovering evidence omitted by older 500,000-line analysis limits. Warnings identify saved supporting evidence that remains truncated; they do not imply a collection failure. Duplicate attempts prefer the record with more boundary evidence. Separate files without enough matching boundaries remain partial rather than inventing durations across them.

WindowsUpdateClient installation-start (event 41) and successful installation (event 19) evidence supplies update titles and GUIDs. After a correlated shutdown and boot, a success event for the same update GUID completes the reboot phase. When the update identity is known, a constituent package's completion cannot substitute for update success. Without an identity event, the existing cumulative-package correlation remains available. Completion measures Windows Update's reported success, not desktop readiness.

After collection, Performance displays historical phase timings separately from WuPilot call metrics. To use an existing extracted bundle, enter its folder in **Analyze existing logs** and select **Load bundle timings and regenerate report**. This reads all retained attempts for filtering and writes a new report under LocalAppData/WuPilot/Reports without changing the original bundle. The KB/update-ID filter searches all retained attempts; otherwise the selected date range applies to the Performance list. Bundle timestamps retain the source zone offset. Report filtering uses the KB/update ID.

Both collection and report-only regeneration write `update-timings.csv` containing per-attempt phase durations and update IDs. Blank duration cells indicate missing boundaries. `timings.csv` remains the individual WUA-call export. Collection status distinguishes missing locations, collection limits, and failures; inspect the manifest details for each source.
