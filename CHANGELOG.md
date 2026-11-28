# Changelog

Release tags and published assets remain immutable. Detailed evidence and guides
are in `docs/`; installation downloads are on GitHub Releases.

## [Unreleased]

- Establish a documented branch/PR workflow, conventional subjects, local hooks,
  formatting/file/secret guards, protected main and immutable release tags.

## [0.2.23] - 2026-11-28

- Show readable BACnet choices, bit flags, object types, priorities and errors.
- Add readable labels to CSV and JSON exports and saved observations while
  retaining numeric selectors, raw observations and workspace compatibility.
- Label APDU types, range flags, device segmentation, and schedule and calendar
  selections. Keep actual readings, addresses, indexes and raw values intact.
- Carry forward the Windows disconnect and discovery recovery in 0.2.22.

## [0.2.22] - 2026-11-25

- Windows returns to Devices on disconnect and shows the last discovery results
  as offline. Connect and discover reconnects and starts a fresh scan.
- Offline Windows checks cover returning to the live device list, disconnecting
  from a device, and reconnecting from the Devices view.

## [0.2.21] - 2026-11-23

- Save priority arrays and relinquish defaults in commissioning baselines.
- Compare individual priority slots, NULL releases and default changes.
- Preserve legacy baselines, read errors, cancellation and device binding identity.
- Reuse the compiled 0.2.20 engine; Mac remains an untested preview.

## [0.2.20] - 2026-11-23

- Publish Apache-2.0 application source with the separately licensed compiled engine.
- Provide the Windows installer and untested Apple Silicon/Intel Mac desktop previews.
- Publish checksums, Windows screenshots and the product specification.
