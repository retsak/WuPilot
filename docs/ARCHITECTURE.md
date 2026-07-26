# Architecture

## Design goals

WuPilot keeps the technician experience responsive and testable without pretending WUA is a modern asynchronous API. The UI never retains COM update objects. A requested action re-queries the exact UpdateID and revision from the original provider, which revalidates applicability immediately before a download, install, hide, or show operation.

```mermaid
flowchart LR
    UI["WinUI 3 shell"] --> CORE["Core requests and evidence models"]
    CORE --> GATE["Serialized operation gate"]
    GATE --> WUA["WUA COM adapter"]
    WUA --> DEF["Policy default / WSUS"]
    WUA --> WU["Windows Update / Microsoft Update"]
    WUA --> OTHER["Microsoft Update / Store / custom service"]
    WUA --> OFFLINE["Signed offline security catalog"]
    WUA --> PNP["Installed signed PnP inventory"]
    CORE --> DIAG["Read-only diagnostics"]
    DIAG --> OS["Registry · services · proxy · DNS · join state"]
    CORE --> EXPORT["Evidence exporter"]
    EXPORT --> BUNDLE["JSON · CSV · HTML"]
    BUNDLE --> INTUNE["Human-reviewed Intune approval"]
    CORE --> POLICY["Policy catalog and change journal"]
    POLICY --> REG["Local policy / Windows UX registry"]
    POLICY --> CSP["Effective MDM evidence (read-only)"]
    CORE --> PERF["Operation metrics and event correlation"]
    PERF --> DO["Delivery Optimization PowerShell module"]
    PERF --> EVT["WindowsUpdateClient / System events"]
    CORE --> UPDATE["Stable GitHub release updater"]
```

## Project boundaries

### WuPilot.Core

This project has no Windows dependency. It defines immutable scan/action/diagnostic records, provider definitions, WUA criteria presets, identity-based deduplication, HRESULT explanations, and service interfaces. Portable tests exercise the decisions most likely to corrupt evidence.

### WuPilot.Infrastructure.Windows

This project owns every Windows-only behavior:

- late-bound WUA COM creation and compatibility-safe optional-property reads;
- sequential provider searches and exact-identity action revalidation;
- installed-driver correlation via `Win32_PnPSignedDriver`, using exact or family PnP identifiers rather than manufacturer guesses;
- registry, service, proxy, DNS, WUA version/history, registered-source, BITS, disk, join-state, event, servicing-log, and pending-reboot diagnostics;
- bounded repair commands with timeouts and captured output;
- evidence serialization and the Intune review page.

Late binding avoids requiring a machine-registered WUA type library at build time. Required members fail the provider or action with an HRESULT; optional members return null so older Windows builds still produce useful evidence.

### WuPilot.App

The unpackaged, self-contained WinUI 3 process requests elevation in its manifest. It controls confirmations, cancellation intent, selection, filtering, progress, and the session activity list. The UI never makes a management-tenant change.

### Policy, metrics, and updater boundaries

The portable policy catalog defines types, ranges, build applicability, risk, and local mappings. Windows infrastructure owns registry access, ownership detection, transactional writes, atomic audit/metric JSON stores, PowerShell telemetry, event correlation, GitHub HTTP, hashing, signature inspection, and installer launch.

Settings batches are all-or-rollback and always verify readback. MDM-only CSP state is never written locally. Private Windows Settings state is isolated and build-gated. Exact metrics come only from WuPilot's monotonic action timer; event-derived durations and cross-restart correlations carry lower confidence and their evidence source.

### Workflow preferences and operation feedback

Portable immutable models describe application preferences, window placement, operation state, completion notices, staged policy changes, and shell-progress state. Infrastructure owns atomic/debounced preference storage, completion retention, corrupt-file recovery, and Windows taskbar/attention interop. WinUI captures/restores view state, coordinates keyboard commands, renders global progress, and intercepts close requests.

Preferences contain workflow configuration only. Scan evidence, technician notes, selected updates, and pending policy changes are excluded by design. The policy cart adds an expected-requested-value precondition so drift is rejected before a transactional registry batch begins.

The updater has no background process. When the machine preference is enabled, every application launch starts a nonblocking conditional request using the cached ETag and release response. Manual checks bypass the cache. The updater chooses the current architecture and fails closed unless GitHub's asset digest and sidecar checksum agree.

## Concurrency and cancellation

WUA’s synchronous `Search`, `Download`, and `Install` calls run away from the UI thread and are protected by one semaphore. Cancellation is checked before providers, while materializing results, and before action phases. It cannot forcibly abort every active COM call, so the UI accurately reports “cancellation requested” until the current call returns.

## Provider behavior

`ServerSelection` and `ServiceID` follow Microsoft’s WUA sample:

| WuPilot source | ServerSelection | Service ID |
|---|---:|---|
| Policy default | 0 | — |
| Managed WSUS | 1 | — |
| Windows Update | 2 | — |
| Microsoft Update | 3 | `7971f918-a847-4430-9279-4a52d1efe18d` |
| Store | 3 | `855e8a7c-ecb4-4ca3-b045-1dfa50104289` |
| Offline security catalog | 3 | Volatile ID returned by `AddScanPackageService` |

A service can be unavailable or prohibited on a particular machine. That failure is retained beside successful provider results rather than discarding the full scan.

An offline scan registers the Microsoft-signed CAB with flags `0`, searches through the returned volatile service ID with `Online=false`, and removes/releases the registration afterward. It is deliberately non-actionable because `Wsusscn2.cab` contains security applicability metadata but no update payloads or driver inventory.

## Driver correlation

The WUA offer and the installed driver come from different inventories. WuPilot compares the offered `DriverHardwareID` and entry identifiers against the signed PnP driver’s hardware ID, compatible ID, and device-instance prefix. The exported score records how the match was made. Manufacturer/model similarity alone never creates a match because it is too weak for an approval packet.

## Future seams

The abstractions deliberately leave room for support-bundle compression/redaction profiles, inventory comparison across imported bundles, fleet-level aggregation, and an optional read-only Microsoft Graph correlation module. Tenant approval writes should remain a separate, strongly authenticated workflow.
