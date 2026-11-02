# FreeAll --- Feature Catalogue

Each feature is a product capability. Implementation status is tracked
separately in `17-feature-checklist.md`.

## A. Dashboard and drive intelligence

-   Drive cards showing volume label, mount points, filesystem,
    capacity, used/free space and scan freshness.
-   Storage overview by category.
-   Recent scans, scan health and outstanding errors.
-   Potential reclaimable space shown as an estimate with methodology
    and confidence.
-   Clear distinction between total volume usage and indexed file
    totals.

## B. Drive discovery and selection

-   Automatic local fixed-volume discovery.
-   Removable-volume scan on demand.
-   Network volumes excluded by default.
-   Volume GUID/stable identity tracking.
-   Mount-point alias deduplication.
-   User exclusions by volume, directory or pattern.
-   Drive availability and scan permission status.

## C. Scanner and index

-   Recursive streaming enumeration.
-   Hidden, system and read-only entry discovery.
-   File metadata collection.
-   Directory aggregation.
-   Access-denied and I/O error reporting.
-   Reparse-point detection and safe non-following default.
-   Cancellation and interruption recovery.
-   Full and incremental scans.
-   USN Journal support where available and validated.
-   Scan progress and activity reporting.

## D. Storage explorer

-   Expandable directory tree.
-   Breadcrumb navigation.
-   File and folder list with sortable columns.
-   Recursive folder size and counts.
-   Logical size and allocated size.
-   Hidden/system/read-only badges.
-   File type, modified time, path and attributes.
-   Open in Explorer, copy path and copy details.
-   Search and filters.
-   Contextual inspect actions.
-   Selection for review; no immediate deletion from an unreviewed
    context menu.

## E. File categories

Initial categories: - Images - Videos - Audio - Documents -
Spreadsheets - Presentations - Archives - Executables and installers -
Development files - Databases - System and driver files - Temporary
files - Application data - Other / unknown

Classification must be explainable and must not imply that a file is
disposable.

## F. Large files and folders

-   Configurable size threshold.
-   Top-N lists.
-   Filters by volume, category, age and path.
-   Folder recursive totals.
-   Drill-down and ownership/path context.
-   Risk labels based on known protected/system/application locations,
    not file size alone.

## G. Application residue

-   Installed desktop application inventory.
-   Uninstall registry inventory.
-   MSIX/Store package inventory.
-   AppData Local, Roaming and LocalLow discovery.
-   ProgramData and known application directories.
-   Evidence graph linking candidate paths to application identity.
-   Confidence: High, Medium, Low, Unknown.
-   Keep, ignore, review, mark ownership and selectively remove.
-   No automatic removal.

## H. Temporary files and caches

-   Known temporary path rules.
-   Application cache candidates.
-   Browser and application caches only where rules and ownership are
    reliable.
-   Age and activity evidence.
-   Per-rule explanation and risk.
-   Explicit user selection and cleanup preview.

## I. Duplicate detection

-   Per-volume size grouping.
-   Full-file SHA-256 streaming hash.
-   Byte-for-byte confirmation before declaring exact duplicates.
-   Hard-link identity awareness.
-   Hash cache invalidation on file changes.
-   Revalidation before cleanup.
-   Duplicate group details and space accounting.
-   No automatic selection or deletion.
-   No cross-volume duplicate groups in v1.

## J. Windows storage

-   Storage breakdown and supported system-cleanup integration.
-   Temporary update files only through documented supported mechanisms.
-   Servicing component cleanup delegated to Windows-supported
    APIs/tools.
-   Explain protected or non-reclaimable system usage.

## K. Treemap and charts

-   Directory treemap sized by allocated bytes where available.
-   Category charts and trend/history views.
-   Zoom, drill-down, hover details and keyboard-accessible
    alternatives.
-   Avoid rendering every file node at once; aggregate and progressively
    load.

## L. Search and scan history

-   Indexed file/path search.
-   Filters by size, category, time, attributes and volume.
-   Scan history and comparison.
-   Full/incremental scan status.
-   Error history and retry.
-   Export CSV, JSON, HTML and PDF.

## M. Cleanup, quarantine and restore

-   Recycle Bin default.
-   Optional same-volume quarantine.
-   Optional central quarantine.
-   Permanent deletion as separate explicit mode.
-   Exact target preview and risk assessment.
-   Pre-operation revalidation.
-   Per-item result and audit.
-   Restore and conflict handling.
-   Quarantine excluded from cleanup candidates and never automatically
    purged.

## N. Settings and governance

-   Scan defaults and exclusions.
-   Removable-drive policy.
-   Update-check opt-in.
-   Theme and accessibility preferences.
-   Database and cache diagnostics.
-   Export settings.
-   Privacy controls.
-   Feature checklist and AI agent governance documents for development.
