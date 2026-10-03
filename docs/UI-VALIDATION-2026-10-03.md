# Command center and navigation validation — October 3, 2026

The sidebar now contains six workspaces. Compare scans, Watchlist, and Registered sources are grouped under Scan and review; Update history and Session activity are grouped under Records. Everyday update switches, pause/resume, and pending changes lead the Command center. Advanced policies and audit details are collapsible. Review and apply remains visible while scrolling.

## Automated checks

- Core tests: 74 passed, including switch reversal, default-value reversal, initial-state preservation after refresh, management warnings, and invalid/noneditable policy rejection.
- Windows infrastructure tests: 39 passed.
- WinUI code checks and native x64 Release publish: succeeded with zero warnings or errors.
- Published EXE and DLL manifest resources: both verified as `requireAdministrator`.
- `git diff --check`: passed.

## Native UI checks

Used separate standard-user preview builds for reversible UI testing. Checked wide and compact windows, including 1041 × 821 pixels.

- Everyday switch cards wrap descriptions and effective/requested/pending state without clipping. Dark-theme accent text remains readable.
- Switch staging updates the pending count; flipping back removes the pending request. The review dialog locks editing; cancellation leaves device state unchanged.
- The sticky review bar remains available when the advanced policy editor scrolls.
- Ctrl+F expands advanced policies, waits for expansion, scrolls search into view, and focuses it. Entering `metered` narrows the list to the matching policy.
- Scan setup fields and result filters wrap at compact widths; update details stack below results.
- Compare scans and Watchlist tabs open correctly. The empty watchlist explains how to add an update.
- Registered sources loads automatically on first opening; the test device returned four existing services.
- Records loads history automatically; the test device returned 170 history events. Session activity shows the history/source reads and startup events.
- Startup application-update checks report availability without opening a modal prompt.

No policy batch, repair, download, installation, or source-registration action was executed on the device. Real privileged write behavior is covered by the existing infrastructure tests; this pass exercised staging and canceled review only. ARM64 and high-contrast runtime layouts were not tested in this pass. Existing documentation screenshots predate this redesign.
