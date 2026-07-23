# Intune driver handoff

## What the bundle answers

The HTML and CSV exports are designed to let an Intune administrator answer:

- Which exact device found the driver?
- Which scan source offered it?
- What catalog title, manufacturer/provider, model, class, hardware ID, version date, and inferred version were reported?
- Which signed PnP driver is installed now, and how confidently did its hardware identifier match the offered catalog entry?
- What WUA UpdateID/revision can a technician use to reproduce the local result?
- Was policy, service, connectivity, or pending-restart state suspicious during the test?

## Review procedure

1. Open `intune-review.html` and note the device, scan time, driver title, manufacturer, class, version/date, and hardware ID.
2. In Intune, open **Devices > Manage updates > Windows updates > Driver updates**, select the applicable policy, and review its driver inventory.
3. Compare the locally installed version/date/INF against the offered version/date. Treat a missing or low-confidence PnP match as a reason for more investigation, not proof that no driver is installed.
4. Match the visible Intune record on name, manufacturer, version or release date, and class. WUA UpdateID is supporting evidence, not the Intune inventory key.
5. Check Intune’s applicable-device count and category (recommended versus other driver). A driver applicable to one test device may not be broadly applicable.
6. Validate OEM release notes and known issues when the update affects firmware, storage, networking, display, or security hardware.
7. Approve first to a small deployment ring with an intentional availability date. Monitor failures and rollback options before expanding.

## Evidence limitations

- A WUA scan proves applicability at one moment on one device. Hardware/firmware state and supersedence can change.
- Direct catalog visibility does not mean a driver should be approved. Intune and Windows Update deployment services have their own inventory, policy, and recommendation state.
- WUA exposes a driver version date but not a standalone version string. WuPilot labels its title-derived version as inferred.
- Hidden state is local WUA state. It is not an Intune decline or pause.
- A successful local install is test evidence, not fleet compatibility evidence.
