# Upgrade log collection and standalone release

In **Performance → Upgrade logs and reports**, enter a local folder or UNC path such as `\\server\support\WuPilot`, then select **Collect and ZIP**. No scan is needed. Optional **Analyze existing logs** accepts a folder of logs from another device; it does not collect this device's Windows events or mix in its operation metrics. Extract a received bundle first to analyze its logs offline.

Collection runs independently of scans and update actions, with four file workers by default. WUA servicing calls retain their serialization. **Cancel** stops collection at an I/O boundary. Closing the desktop app is blocked while collection runs. Current account credentials are used for network access; elevated sessions may not have mapped drives, so prefer UNC paths.

Each run has a unique local folder and ZIP in `%LocalAppData%\WuPilot\LogBundles`. The collector stages locally, then copies a `.partial` file to the destination and renames it only after copying finishes. If delivery fails or is cancelled, the local ZIP remains available for retry. An abandoned destination `.partial` is not a completed bundle. SMB calls can remain pending until Windows' network timeout; cancellation is cooperative and is not a hard deadline for kernel/network I/O. Retained bundles are not automatically removed.

Sources include Panther, `$WINDOWS.~BT\Sources\Panther`, rollback, MoSetup, existing SetupDiag output, CBS, DISM, SetupAPI, Windows Update ETLs and retained WuPilot operation metrics. System, Setup and WindowsUpdateClient event channels are exported for the last 30 days. Raw ETLs/EVTX are preserved for specialist tools; the collector does not decode ETLs or execute/download SetupDiag. Microsoft documents the [setup log locations](https://learn.microsoft.com/en-us/windows/deployment/upgrade/log-files).

The manifest records missing, denied and limited sources, file-copy time, bytes and hashes for copied files. Files are copied with sharing enabled and bounded to their initial length; live logs are not transactionally consistent. Defaults cap files at 64 MiB each, 300 files, directory depth five, skip directory links and cap parsing at 500,000 lines and 1,000 findings per file. Event exports are bounded by their time window and 45-second command timeout rather than the source-file size cap. Inspect the manifest before treating any collection as complete.

## Reports

- `report.html`: offline human-readable timing tables, setup activity windows, findings and collection status. All source text is HTML-escaped.
- `timings.csv`: operation download, install, total and shutdown-to-boot seconds, confidence and result codes.
- `operation-metrics.json`: structured timing records including evidence sources.
- `analysis.json`: parsed timestamp windows and error/rollback keyword findings with source line numbers.
- `manifest.json` and `logs/`: original source paths, collection outcomes and raw snapshots.

WuPilot's monotonic timings measure its WUA calls, including revalidation in the total. They are not the complete duration of offline feature upgrade servicing. Event correlations require matching identities and are estimates. Reboot evidence measures shutdown to kernel boot, not desktop readiness, and does not prove update completion. Setup log windows show observed activity per file and recognizable phase marker; unknown phases remain unclassified. They must not be summed into an upgrade duration. Missing boundaries remain unavailable. Setup timestamps are local wall-clock times; regressions and gaps over six hours split windows. Keyword errors may be benign and are not automatic root-cause diagnoses. Review SetupDiag and raw logs for confirmation.

## Standalone collector

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
