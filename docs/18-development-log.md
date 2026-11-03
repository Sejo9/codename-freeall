# FreeAll --- Development Log

## 2026-11-02 --- Product and architecture baseline

### Approved

-   Windows 11 only; Windows 10 excluded.
-   WPF on .NET 10 LTS.
-   Support all currently serviced Windows 11 releases, adding releases
    after compatibility testing and retiring them when servicing ends.
-   x64 primary target.
-   ARM64 conditional on validation.
-   No x86.
-   Self-contained per-user installation.
-   Separate on-demand elevated helper.
-   Recycle Bin default; optional quarantine; permanent deletion
    separately confirmed.
-   Exact duplicate detection in v1, per volume only.
-   Fixed drives scanned by default; removable drives on demand; network
    drives excluded by default.
-   Local processing and no telemetry/metadata upload by default.
-   Update checks opt-in during setup.

### Architecture

-   Modular layered solution.
-   WPF/MVVM presentation.
-   Application and Core independent of WPF.
-   Dedicated scanner, residue, duplicate and cleanup modules.
-   SQLite proposed for local metadata.
-   Secure allowlisted named-pipe IPC for elevated helper.

### Outstanding validation

-   ARM64 native dependencies.
-   SQLite provider and packaging.
-   Installer technology.
-   WPF data grid/tree controls.
-   Chart/treemap technology.
-   Fluent theme coverage and design-system fallback.
-   Specific Windows storage cleanup APIs.
-   Performance thresholds and benchmark hardware.

### No implementation claim

This entry records decisions and planned work only. It does not assert
that code has been written, tested or released.

## 2026-11-02 --- Phase 0: solution foundation (F-001)

### Completed

-   Git repository initialized on `main`; documents moved to `/docs`.
-   `FreeAll.slnx` with all nine `src` projects and
    `FreeAll.Architecture.Tests`.
-   Shared build baseline: `global.json` (SDK 10.0.100, latest feature
    roll-forward), `Directory.Build.props`, `Directory.Packages.props`,
    `.editorconfig`, lock files.
-   `asInvoker` manifests for Desktop and Helper; Desktop is per-monitor
    V2 DPI aware and long-path aware.
-   Minimal WPF window; Helper stub exits with a failure code (fail
    closed).
-   `build.ps1` and provisional GitHub Actions workflow.
-   Dependency register (`23-dependency-register.md`).

### Tests

-   `build.ps1 -Configuration Release -Locked -Publish`: 0 warnings,
    0 errors, 40/40 tests passed, no vulnerable packages, publish
    succeeded.
-   Negative check: injected dependency violations produced 3 test
    failures.
-   Smoke test: published app opened its main window and exited with
    code 0.
-   Defect found and fixed: a `win-x64` publish rewrote library lock
    files and broke the next locked restore. `RuntimeIdentifiers` is now
    set for all projects (TD-005). A publish run followed by a locked run
    passes, with identical lock files.

### Decisions

TD-001 to TD-009 recorded in `16-developer-decisions.md`.

### Open

-   BLK-010 to BLK-014 raised; all resolved the same day as DEC-022 to
    DEC-026. BLK-015 (source licence) opened.
-   CI has not run on a remote host.
-   The self-contained win-x64 publish is about 140 MB (Desktop and
    Helper each bundle the runtime). Revisit with installer selection
    (BLK-003).

### Next

Phase 1: shell and navigation, MVVM and dependency injection, design
tokens and light/dark theme (BLK-006 Fluent audit), Windows 11 runtime
check, SQLite provider validation (BLK-002), logging.

## 2026-11-02 --- Product-owner decisions and Phase 1: UI and data foundation

### Decisions received

BLK-010 to BLK-014 resolved as DEC-022 to DEC-026: removable volumes
without a Recycle Bin, elevated scanning in v1, a free and redistributable
product with permissive dependencies only, unsigned builds for now, and
cancel-only scans. The repository is hosted on GitHub with GitHub
Actions.

### Completed

-   **Shell:** navigation rail with all 12 sections, collapsible and
    automatically compact below 1000 px; page title region; status region
    with scan and database state; placeholder pages that say plainly
    the feature is not available.
-   **MVVM and DI:** CommunityToolkit.Mvvm, explicit composition root
    (TD-010).
-   **Theming:** Fluent theme with light, dark and Windows-setting modes,
    live follow of Windows changes, a high-contrast path, the Windows
    accent colour, and project tokens (TD-011). The theme choice is
    persisted.
-   **Startup:** Windows 11 / x64 gate; protected data folder (TD-015);
    logging (TD-014); database initialized off the UI thread; global
    exception handling (TD-016).
-   **Persistence:** SQLite provider selected and validated (TD-013,
    BLK-002 resolved for x64); first migration (`UserPreferences`).
-   **Drives page:** real volume discovery through the Win32 volume
    APIs, GUID identity, mount-point deduplication, network drives shown
    as excluded, capacity meters with text equivalents.
-   **Explorer prototype:** 1M-row virtualized grid, lazy tree,
    off-thread sorting, inspector panel; BLK-004 resolved (TD-017).
-   **Settings:** theme, privacy statement, data and logs folders,
    database status, version.
-   **Visual review:** Debug-only `--capture-screens` mode. All pages
    were reviewed in light and dark, and the defects found were fixed:
    accent mismatch, DataGrid column collapse, a stretched placeholder
    card, low-contrast toggle text, and EFI/recovery partitions shown as
    "included in default scans" (now on demand, TD-012).

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0
errors, 148/148 tests passed across six suites:

-   Architecture: 40
-   Core: 38
-   Application: 14
-   Scanner: 5
-   Integration: 20
-   Desktop: 31

No vulnerable packages were found. The published Release app starts,
migrates the database, shows its window and exits with code 0. The data
folder's access list is protected and limited to the user, SYSTEM and
Administrators.

### Open

-   BLK-015 (source licence) and BLK-016 (TD-012 confirmation) need
    product-owner answers.
-   BLK-006: high-contrast and Narrator validation; decorative-icon
    automation.
-   TD-014 gap: redaction of OS exception messages.
-   CI has not yet run on GitHub.

### Next

Phase 2: persisted volume identity (`Drives` table), streaming scanner
with bounded channels, scan sessions and completeness accounting,
reparse-point handling, directory aggregation, exclusions and
cancellation, and the index-backed explorer.

## 2026-11-03 --- Product-owner decisions and Phase 2: scanner and storage explorer

### Decisions received

-   BLK-015 → DEC-027: the source code is public so others can benefit.
    MIT was applied as the recommended permissive licence (`LICENSE`).
-   BLK-016 → DEC-028: unmounted fixed volumes (EFI, recovery) stay out of
    default scans (TD-012 confirmed).

### Completed

-   **Scanner (F-009 to F-013):** read-only streaming scanner over volume
    GUID paths using bulk directory information
    (`GetFileInformationByHandleEx`), with a FAT/exFAT fallback. Records
    sizes, allocation, timestamps, attributes, reparse tags and file IDs
    without opening files. Reparse points are never followed, including a
    folder replaced by a link mid-scan. Parallel depth-first workers,
    bounded batches and a bounded channel; large folders are flushed while
    they are listed. Cancellation keeps a consistent partial result
    (TD-018).
-   **Index (schema version 2):** drives by GUID, scan sessions with scope,
    exclusions, reparse policy, versions, counts, state and a
    plain-language note; folders, files, errors (first 10,000) and
    category totals per session. Recursive folder totals and incomplete
    counts computed bottom-up in SQL at completion. Older indexes pruned
    per volume; interrupted sessions recovered at start-up (TD-019).
-   **Completeness (F-011):** complete only when the whole defined scope
    was read and saved; links and exclusions are part of the scope and
    disclosed; items removed during the scan don't count against it
    (TD-020).
-   **Classification (F-015):** versioned extension-then-location rules
    with the reason stored per file; location labels (TD-021).
-   **Coordinator:** one run at a time, volumes in order, throttled
    progress (4 per second), cancellation, store failure stops the scanner
    and fails the session.
-   **UI:** Drives page scans default or single drives, shows the scan
    scope before starting, live progress with cancel, last result per
    drive and excluded folders (add with a folder picker, remove). Storage
    Explorer, Large Files, File Categories and Scan History read from the
    index (TD-023). The status bar shows scan state. The synthetic
    explorer prototype was removed.
-   **Docs:** DEC-027, DEC-028, TD-018 to TD-024, BLK-017 (new question),
    BLK-009 baseline, checklist evidence.

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: see the result
below. Automated tests by suite:

-   Architecture: 40
-   Core: 141
-   Application: 29
-   Scanner: 29 (1 skipped: creating symbolic links needs Developer Mode
    or elevation on the development PC)
-   Integration: 37
-   Desktop: 51

**Performance baseline (BLK-009):** development PC, NVMe system drive
(C:, 343 GB, 332 GB used), standard user, Debug build, 4 workers,
results written to a separate data folder (`--capture-scan C:\
--data-dir …`):

  Measure                               Run 1                Run 2
  ------------------------------------- -------------------- --------------------
  Files / folders indexed               1,000,564 / 291,681  1,000,698 / 291,701
  Total time (scan, save, aggregate)    132 s                90 s
  Throughput                            7,600 files/s        11,100 files/s
  Peak working set                      248 MB               233 MB
  UI idle delay during scan (max)       546 ms               156 ms
  Database size (+ WAL)                 212 MB (+64 MB)      216 MB (+70 MB)
  Open the largest folder in Explorer   547 ms               34 ms

Run 2 includes the query-plan fix and probably a warmer file cache. The
scan was correctly `Partial`: 116 folders were access-denied for a
standard user (for example `Windows\Temp`, `SysWOW64\Tasks`, other
users' Recycle Bin folders), and 2,420 reparse points were not followed.
Peak memory stayed under 250 MB for a million files, which matches the
bounded-memory design (TD-018).

### Defects found and fixed during Phase 2

-   Test helper hang: .NET's recursive enumeration follows junctions, so
    cleanup of a tree with a junction loop never finished (test code
    only; the scanner was not affected).
-   NTFS refreshes a folder's metadata copy in its parent's index lazily;
    the "scan changes nothing" test now reads each item directly
    (TD-022 caveat).
-   Query plans: without table statistics SQLite scanned a session's whole
    folder range for folder listings and sorted all folders for "largest
    folders". Index use is now fixed in the SQL and an index on folder
    size was added (listing 117 ms → 0.2 ms, largest folders 393 ms →
    4 ms on a 1M-file index). Migration 0002 was amended before its
    first commit; no database outside test folders had applied it.
-   Visual review: the file grids stayed light in dark mode (a style based
    on the Fluent DataGrid style through StaticResource keeps its original
    theme); status icons kept their default glyph because a local value
    overrode the trigger; the last-scan line was clipped; numbers touched
    the next column; the category explanation lower-cased "AppData";
    virtual disk images (.vhd, .vhdx, .vmdk) were not classified.

### Open

-   BLK-017: cloud placeholder folders (OneDrive) are not listed today;
    product-owner decision needed.
-   F-007: device arrival/removal notifications; manual drive-letter
    change and disconnect-during-scan tests.
-   F-014/F-016: review queue and multi-select (Phase 6), search and
    filters (F-017), age and path filters for Large Files.
-   F-018: retention setting, scan comparison, export.
-   Hard-link-aware totals (F-028). Elevated scanning (DEC-023, with the
    helper in Phase 6). TD-014 exception-message redaction gap.
-   CI has not yet run on GitHub (the repository has not been pushed).

### Next

Phase 3: incremental scan foundation, USN Journal where validated,
search, treemap and charts, scan history comparison and exports.
