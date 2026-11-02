# FreeAll --- Functional Specification

## 1. Global behaviour

Every workflow must expose current state, progress, cancellation where
meaningful, errors and final results. Long-running work runs off the UI
thread. UI commands call application services; they do not directly
perform filesystem traversal or destructive operations.

## 2. Scan workflow

1.  User selects a drive or accepts default fixed-drive selection.
2.  App resolves volume identity and checks availability.
3.  App displays scope, exclusions, reparse-point policy and estimated
    impact.
4.  Scanner creates a scan session and begins streaming enumeration.
5.  Scanner records discovered metadata in bounded batches.
6.  Directory aggregates are calculated or updated.
7.  Errors are recorded with path, operation, error code and timestamp.
8.  User may pause/cancel where supported.
9.  Finalization records completeness, counts, elapsed time and
    unaccounted scope.
10. Dashboard reflects the latest successful/partial scan without hiding
    prior errors.

A scan is `Complete` only if its defined scope was traversed without
unresolved scan errors. Otherwise it is `Partial` or `Failed`, with an
explanation.

## 3. Drive discovery

-   Enumerate local fixed volumes on startup and refresh.
-   Offer removable drives as explicit on-demand targets.
-   Exclude network drives by default.
-   Use volume GUID or equivalent stable identity as the primary
    identity.
-   Drive letter is a display/mount attribute, not the persistent key.
-   Detect multiple mount paths referring to the same volume.
-   Handle volumes becoming unavailable during a scan.

## 4. Explorer workflow

-   Display a navigable tree and corresponding content list.
-   Folder rows show recursive size and item counts.
-   File rows show logical size, allocated size if known, type, modified
    time and attributes.
-   Sort and filter must operate on indexed data or bounded result sets.
-   Selecting an item opens an inspection panel with full path, volume,
    file identity, timestamps, attributes, category and scan provenance.
-   Open in Explorer and copy path are non-destructive actions.
-   Cleanup selection enters a review queue; it does not delete
    immediately.

## 5. Residue workflow

1.  Build application inventory from supported sources.
2.  Discover paths using known location patterns and evidence rules.
3.  Associate candidate paths with applications.
4.  Assign confidence and risk using explainable rules.
5.  Display evidence, ownership, last-modified details and uncertainty.
6.  User chooses keep, ignore, review, mark ownership or select items
    for cleanup.
7.  Cleanup proceeds only through the standard cleanup workflow.

High confidence is not equivalent to safe deletion. A path may be owned
by an application but contain user documents, shared data, licences or
settings.

## 6. Duplicate workflow

1.  Enumerate eligible files within one volume.
2.  Group candidates by size.
3.  Hash candidate files with streaming SHA-256.
4.  Revalidate identity and change metadata before and after hashing.
5.  Byte-compare same-hash candidates before asserting exact equality.
6.  Detect hard links using file identity where supported.
7.  Present groups with one or more copies; do not preselect a deletion
    candidate automatically.
8.  Calculate potential reclaim only after accounting for hard links and
    allocation behaviour.
9.  Revalidate selected items before cleanup.

## 7. Cleanup workflow

1.  User selects candidate items.
2.  App shows exact paths, sizes, volume, origin feature and
    classification.
3.  App explains risk and confidence; unknown risk is not silently
    treated as low.
4.  User chooses Recycle Bin, quarantine or permanent deletion where
    eligible.
5.  App checks protected-path policy and current file identity.
6.  App revalidates that the selected file still matches the reviewed
    item.
7.  User confirms the final operation.
8.  Operation executes through the appropriate standard or elevated
    path.
9.  Results are recorded item by item.
10. App reports actual reclaimed space only when it can measure it;
    otherwise reports estimated or pending reclamation.

## 8. Quarantine workflow

-   User chooses same-volume or central quarantine.
-   Store original path, volume identity, metadata, hash, operation ID
    and restore information.
-   Same-volume move may use an atomic rename when filesystem semantics
    permit.
-   Cross-volume quarantine must copy, verify content and required
    metadata, then remove source only after successful verification.
-   If source removal fails, keep verified quarantine copy and report
    that original storage was not reclaimed.
-   Quarantine data is excluded from scans' cleanup candidates.
-   No automatic purge.
-   Restore checks original volume/path, collisions and current
    permissions.

## 9. Windows storage workflow

-   Use documented Windows-supported cleanup interfaces for protected
    OS-managed components.
-   Do not manually delete WinSxS content, servicing packages, restore
    points or other protected OS stores.
-   Present Windows-managed cleanup options with clear descriptions and
    launch/consent boundaries.

## 10. Export

-   Export current filtered view or full selected scan, clearly
    indicating which.
-   Include scan ID, timestamp, volume identity, scope and
    error/completeness status.
-   Escape CSV fields safely.
-   Encode JSON using a versioned schema.
-   HTML export must not execute file names or metadata as
    markup/script.
-   PDF export must be readable and include report provenance.

## 11. Failure handling

-   Access denied: record and continue where safe.
-   Sharing violation/locked file: record and continue.
-   Volume removed: stop work for that volume, persist partial state and
    report.
-   Database unavailable/full: stop safely; do not continue with
    untracked destructive operations.
-   App crash: recover scan as interrupted; do not assume completion.
-   Helper disconnect: fail closed; do not retry destructive operations
    blindly.
