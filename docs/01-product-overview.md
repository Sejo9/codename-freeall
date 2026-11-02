# FreeAll --- Product Overview

## 1. Product vision

FreeAll is a Windows 11 desktop application that explains how storage is
being used, discovers files and application residue that ordinary
cleanup tools may overlook, identifies exact duplicate files, and helps
users reclaim storage through deliberate, reviewable, reversible
operations.

FreeAll is an analysis and decision-support tool first and a cleanup
tool second. It must explain what it found, why an item is being
presented, what could happen if it is removed, and what recovery options
exist.

## 2. Product principles

1.  **Visibility:** make hidden, system, deeply nested and otherwise
    overlooked storage visible where access permits.
2.  **Explainability:** every cleanup candidate has a reason, evidence,
    risk and confidence level.
3.  **User control:** no automatic deletion; the user chooses each
    cleanup operation.
4.  **Reversibility:** Recycle Bin is the default deletion destination;
    quarantine is an optional managed alternative.
5.  **Conservative safety:** never infer that a file is safe merely
    because it is large, old, temporary-looking or associated with an
    uninstalled application.
6.  **Local-first:** filesystem analysis and metadata remain local. No
    telemetry or metadata upload by default.
7.  **Honest accounting:** distinguish logical size, allocated size and
    reclaimable size; disclose inaccessible and unaccounted locations.
8.  **Maintainability:** keep scanning, policy, persistence and cleanup
    independent from the WPF presentation layer.

## 3. Intended users

-   Windows users who need to understand storage consumption.
-   Technical users who want detailed file and directory analysis.
-   Users investigating leftovers after uninstalling applications.
-   Users looking for exact duplicates and reclaimable space.
-   Support and IT personnel performing local storage diagnostics.

FreeAll is not an enterprise fleet-management platform, remote
management agent, antivirus product, registry cleaner, defragmenter, or
general-purpose system optimizer.

## 4. Product scope

### Included

-   Windows 11 storage inventory and drive analysis.
-   Deep file and directory scanning within accessible scope.
-   File categorization and large-file discovery.
-   Application residue discovery with evidence and confidence.
-   Temporary/cache candidate discovery with conservative policies.
-   Exact duplicate detection within each volume.
-   Storage visualization and indexed search.
-   Scan history and export.
-   Recycle Bin deletion and optional quarantine/restore.
-   Supported Windows storage cleanup integrations where safe and
    documented.
-   On-demand elevated helper for narrowly scoped privileged operations.

### Excluded

-   Windows 10.
-   x86 Windows builds.
-   Cross-drive duplicate grouping as a single duplicate set.
-   Automatic deletion or scheduled unattended cleanup in the initial
    release.
-   Cloud scanning or uploading user file metadata.
-   Arbitrary registry cleaning.
-   Forced deletion, ACL bypass, ownership takeover or security-control
    circumvention.
-   Following directory links/reparse points outside the defined scan
    scope.
-   A persistent privileged Windows service.

## 5. Success measures

The product should measure: - Scan completeness and explicit error
accounting. - Correctness of logical and allocated size reporting. -
Precision of duplicate detection. - Accuracy and explainability of
residue classifications. - Successful restore of quarantined items. -
Destructive-operation safety and audit completeness. - Responsiveness of
large result views. - Crash recovery and database integrity. -
Compatibility across supported, serviced Windows 11 releases.

Quantitative thresholds must be established by the performance baseline
phase; do not invent production claims before benchmarking
representative machines and storage devices.

## 6. Product promise

FreeAll reports what it can inspect, identifies what it cannot inspect,
and never represents an estimate as guaranteed reclaimable space. It
does not promise that every byte on every volume can be attributed to an
ordinary file.
