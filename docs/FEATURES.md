# WuPilot feature guide

WuPilot is a Windows Update Agent (WUA) workbench for support technicians. It is designed to answer three questions on one Windows device:

1. What does each configured update source currently offer?
2. What changed between scans, and how does an offered driver relate to the installed driver?
3. What evidence should be handed to an Intune administrator or another support tier?

The application is intentionally local and policy-aware. It does not approve drivers in Intune, register Microsoft Update services during a scan, bypass update policy, perform a mass deployment, or restart the device automatically.

## Scan and review

### Update sources

WuPilot can query one or more sources sequentially in a single scan:

- **Policy default** follows the source selected by the device's current Windows Update policy.
- **Managed WSUS** explicitly queries the configured managed update server.
- **Windows Update** queries the public Windows Update service when that service is registered and allowed.
- **Microsoft Update** includes Microsoft products when the Microsoft Update service is already registered.
- **Microsoft Store service** queries the registered Store update service.
- **Custom service ID** uses an existing WUA service GUID. The **Registered sources** page can populate this value.
- **Offline catalog** uses a Microsoft-signed `Wsusscn2.cab`. This is an applicability catalog for Microsoft security updates; it contains neither driver metadata nor update binaries.

The **Policy only**, **Microsoft sources**, **All**, and **Clear** shortcuts make repeatable source selection faster. A scan never registers a missing service. Registration and policy failures remain visible as source-specific results so a successful source does not hide another source's error.

### Search profiles and criteria

Built-in search profiles cover the common support cases:

- missing drivers;
- missing software;
- installed updates;
- hidden updates;
- all applicable updates; and
- advanced WUA criteria.

Advanced criteria are validated before WUA receives them. Length is bounded, control characters and statement separators are rejected, and WUA remains the final authority on syntax. **Include potentially superseded updates** widens the query and should be used deliberately because it can make result sets much larger.

### Saved scan profiles

**Save current** persists a named combination of:

- selected provider IDs;
- search profile and custom criteria;
- the supersedence option;
- custom service ID; and
- offline catalog path.

Profiles are applied only when the technician chooses **Apply**. Saving a profile with an existing name replaces that profile, and deletion requires confirmation. Profiles are stored per Windows user in:

```text
%LOCALAPPDATA%\WuPilot\scan-profiles.json
```

The file is written through a temporary file and atomic replacement. A profile stores scan configuration, not scan results or credentials.

### Scan execution and cancellation

WUA operations are serialized to prevent overlapping COM operations. Providers run in sequence and are isolated from one another, so one provider can fail while other results remain usable.

**Cancel** records cancellation intent. A synchronous WUA provider call cannot always be interrupted immediately, so the application finishes control after the active call returns. The status bar identifies the active work and records the outcome in **Activity**.

### Scan insights and selective retry

After a scan, the insight card summarizes:

- total, driver, and software update counts;
- installed, downloaded, hidden, mandatory, and restart-related state;
- known maximum download size and updates whose size is unavailable;
- successful and failed provider counts; and
- scan duration.

If a source failed, **Retry failed sources** re-queries only those providers. Results from providers that already succeeded are retained and merged with the retry results. This avoids repeating slow successful searches and preserves the original evidence trail.

## Finding and reviewing results

### Search, filter, and sort

Free-text search matches the title, source, KB identifiers, and CVE identifiers. The state filter can show:

- all updates;
- drivers;
- software;
- installed;
- downloaded;
- hidden; or
- restart-required updates.

Results can be sorted by the default WUA order, title, descending size, descending date, or severity. **Reset filters** restores the full result set. **Select visible** and **Clear selection** support evidence collection across a filtered set.

Multi-selection affects export only. Download, install, hide, and show remain single-update actions so the user must review and confirm one target at a time.

### Deduplication and source attribution

WuPilot deduplicates results using WUA UpdateID and revision. When more than one source offers the same revision, the combined record retains every provider ID and display name. This makes cross-source comparison possible without presenting the same update as unrelated duplicates.

### Update details

The details pane exposes the evidence used in a support decision:

- UpdateID and revision;
- every offering source;
- description, KBs, CVEs, categories, severity, and size;
- installed, downloaded, hidden, mandatory, and restart state;
- manufacturer, model, class, hardware ID, offered date, and inferred driver version;
- installed signed PnP driver version, date, INF, and signer; and
- driver match confidence.

Driver matching prefers exact and family hardware identifiers. WuPilot does not infer a device match from manufacturer alone. The offered driver version is inferred from the catalog title because WUA exposes the driver date but not a separate driver-version string.

**Copy details** places a text summary on the clipboard. **Support page** opens the update's support URL when WUA supplies one.

## Comparing scans

WuPilot keeps the two most recent scan reports in the current application session. **Compare scans** groups differences into:

- **New** — an UpdateID appears only in the latest scan;
- **No longer offered** — an UpdateID appeared previously but not in the latest scan;
- **Revision changed** — the latest revision for an UpdateID changed; and
- **State changed** — installed, downloaded, hidden, mandatory, restart-required, or offering-source state changed.

The comparison also reports unchanged updates. A caution appears when scan criteria or selected sources differ, because those differences can explain new or removed results. Comparisons are session-only and do not change update state.

## Watchlist

**Add to watchlist** stores the selected UpdateID, revision, title, update kind, sources, state, and driver version/date. After each later scan, WuPilot marks a watched update as offered or not offered and refreshes its last-checked time.

The watchlist persists per Windows user at:

```text
%LOCALAPPDATA%\WuPilot\watchlist.json
```

Tracking is read-only: it does not download, install, hide, approve, or deploy an update. **Copy watchlist** creates a clipboard summary, and individual entries can be removed without changing Windows Update state.

## Registered sources

The **Registered sources** page inventories WUA services already present on the device. For each service it shows:

- service name and GUID;
- managed or unmanaged role;
- whether it is the Automatic Updates default;
- whether it is an offline scan-package service; and
- whether it offers Windows updates.

**Use for custom scan** copies the selected GUID into the scan page. The inventory does not call WUA service-registration methods, change the default service, or opt the device into Microsoft Update.

## Diagnostics and repair

**Refresh checks** collects read-only evidence about:

- Windows Update services;
- registered WUA services and recent update history;
- Windows Update and MDM policy registry values;
- WinHTTP proxy and Microsoft content DNS;
- WUA version and recent HRESULT guidance;
- BITS jobs;
- free disk space;
- Entra join identity; and
- pending restart indicators.

Findings have severity labels and recommendations. They can be filtered by severity and copied as a support summary.

Repair actions are deliberately separate from diagnostics:

- **Start required services** starts the bounded set of services WuPilot needs.
- **Reset update cache** stops the required services and renames existing cache stores to timestamped recovery paths instead of deleting them.
- **DISM ScanHealth** checks the component store.
- **DISM RestoreHealth** performs the corresponding repair operation.
- **Generate WindowsUpdate.log** asks Windows to produce a readable update log.

Every repair action presents a confirmation first and is recorded in **Activity**. WuPilot never restarts the device automatically.

## Update history

The dedicated **Update history** page loads up to 500 recent local WUA history records without starting a new scan. Search covers title, HRESULT, source, and UpdateID. **Failures and partial results only** narrows the view to failed, aborted, or partially successful operations.

Each row includes date, operation, result, HRESULT, UpdateID, and source/client context. **Copy visible** copies only the currently filtered records. Reading history does not alter it.

## Update controls

The **Update controls** page is an elevated, device-local policy workbench. It reads more than 45 generally available Windows Update and Delivery Optimization controls, including offerings, driver inclusion, feature and quality deferrals, target release, WSUS and scan sources, automatic-update schedules, active hours, deadlines, notifications, peer selection, bandwidth, cache, upload, and Connected Cache configuration.

Each row distinguishes:

- the requested local value from the effective value;
- Windows default, local/Group Policy, MDM, private Windows UX, or runtime ownership;
- whether the current Windows build supports the setting;
- whether a supported local write path exists; and
- normal, elevated, or high-risk changes.

Quick controls mirror commonly used Windows Settings choices. Private UX values are labeled and available only on known Windows builds. MDM-only CSP values are read/export only because an elevated desktop app is not an MDM enrollment provider.

### Policy workbench quality of life

- Favorites persist per Windows user and can be isolated with the state filter.
- Category, ownership, risk, editability, difference, and legacy filters combine with text search.
- Each descriptor selects a boolean toggle, documented-choice picker, bounded number editor, date picker, or text editor.
- Quick controls and selected-policy edits stage into a session-only cart; staging never writes Windows state.
- The cart summarizes original/requested values, owner, risk, restart behavior, and management-policy warnings.
- Apply checks that the requested value has not drifted since staging, then uses the existing all-or-rollback verified transaction.
- Audit details are copyable; restoration keeps its current-value conflict protection.

![Policy favorites, typed editor, and change cart](images/windows-validation-2026-07-25/qol-policy-cart.png)

## Saved workflow state and navigation

WuPilot atomically retains workflow preferences in `%LocalAppData%\WuPilot\app-preferences.json`. A corrupt file is moved aside and safe defaults are used. Writes are debounced, the schema is versioned, and reset is available from About.

Restored state includes visible window placement, maximized state, page, navigation pane, theme, scan setup, filters/sort, performance range, policy filters, favorites, and taskbar-attention choice. Window placement is clamped to a current display. Cached scan results, update selections, technician notes, and pending policy-cart entries never survive a restart.

Keyboard commands cover primary navigation, contextual search/refresh, starting a read-only scan, cancellation, and evidence export. A shortcut never performs an update action, policy write, or repair without the same confirmation used by its button.

## Global operation progress

The footer presents the active operation, originating page, stage, percent when known, elapsed time, and cancellation availability. Navigation remains available while serialized work runs. The taskbar reflects indeterminate, normal, paused, and failure states through an injectable Windows adapter.

The completion center retains up to 50 notices for 30 days. Notices contain only operation summaries and link back to the originating page. When enabled, WuPilot flashes the taskbar only while unfocused; no service, tray process, startup agent, or notification helper is installed.

Closing while busy never tears down a WUA or servicing call. WuPilot offers to keep running or request cancellation, and closes only after the operation returns to a safe boundary.

![Global operation progress](images/windows-validation-2026-07-25/qol-operation-progress.png)

![Completion center](images/windows-validation-2026-07-25/qol-completion-center.png)

An apply operation validates the complete batch, records original value types and existence, writes each value, and verifies readback. Any failure restores all values already written by that batch. The durable audit contains OS build, user SID, before/after/verified values, ownership, and outcome. Restoration refuses to overwrite later drift unless conflict restoration is explicitly requested.

## Performance analytics

The **Performance** page shows supported Delivery Optimization performance snapshots and up to 365 days or 5,000 retained WuPilot action records. Available Delivery Optimization evidence includes HTTP/CDN, Connected Cache, LAN and internet peer bytes, uploaded bytes, cache size, mode, active downloads, peer savings, and foreground/background limits.

WuPilot download and install actions record an exact monotonic total duration, result, HRESULT, update identity, known bytes, and restart requirement. WindowsUpdateClient operational-event pairs can supplement the list when a matching start and completion boundary exists; those durations are labeled estimated with low confidence and their event-log source. Missing boundaries remain unavailable.

When a WuPilot action requires a restart, the pending record survives app closure. A later launch can match normal Kernel-General shutdown and boot events after the action, recording restart wait and downtime evidence with medium confidence. WuPilot does not install a background service, start automatically, or restart the computer.

## WuPilot updates

Automatic stable-release checks run at most daily while WuPilot is open when enabled by the installer. **About > Check for updates** always forces a fresh check. WuPilot ignores draft/prerelease versions, selects the current architecture, downloads only from HTTPS GitHub release URLs, and verifies SHA-256 against both the GitHub digest and checksum asset. Authenticode signatures are required to be valid when present; unsigned releases require an additional confirmation.

## Evidence export

An export can include the full scan or only explicitly selected updates. Technician notes are carried into the review bundle so ticket context stays with the technical evidence.

The bundle contains machine-readable and human-readable artifacts, including:

- normalized scan JSON;
- update/driver CSV;
- update-history CSV;
- an HTML review report;
- Windows Update event data;
- registered WUA services;
- BITS state;
- CBS errors;
- SetupAPI driver-install evidence;
- existing SetupDiag results, when present; and
- export metadata and technician notes.

Exports can contain serial numbers, Entra device and tenant IDs, policy values, and hardware identifiers. Treat the bundle as support data and store or transmit it according to the organization's data-handling rules.

## Single-update actions

WuPilot can download, install, hide, or show one selected update. Before acting, it revalidates the UpdateID and revision against the original source. Each action has a separate confirmation that defaults to cancellation.

- **Download** obtains content but does not install it.
- **Install** refuses updates that may require interactive input, handles license acceptance explicitly, and records result/HRESULT/restart state.
- **Hide** and **Show** change only the device's local WUA visibility and are verified by a later scan.

These actions are intended for controlled testing on one device. They do not approve an update in Intune or create a deployment. WuPilot never initiates an automatic restart.

## Activity, appearance, and operating boundaries

**Activity** is an in-memory timeline for the current session. It records scans, retries, diagnostics, repairs, exports, watchlist changes, and action outcomes. **Copy activity** is useful for a quick ticket note; the evidence export is the durable record.

The **About** page includes system, light, and dark theme controls plus Microsoft reference links. It also restates the application's safety model.

WuPilot requires elevation because WUA actions, service control, DISM, and repair evidence may require administrator rights. Read-only scans are still subject to Windows policy, service registration, endpoint access, and Microsoft applicability rules. A direct-source comparison is diagnostic evidence, not a policy bypass.
