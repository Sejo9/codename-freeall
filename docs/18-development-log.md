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

## 2026-11-03 --- Phase 2 follow-ups and Phase 3: advanced storage analysis

### Decisions received

-   BLK-017 → DEC-029: list cloud placeholder folders (OneDrive), never
    follow links, label cloud-only files with their real space on disk.
-   DEC-030: deliver the review queue with multi-select, a retention
    setting and hard-link-aware totals now, then Phase 3.

### Completed

-   **Cloud folders (DEC-029):** only cloud-family reparse tags that are
    not links are listed; scanner version 2; cloud-only files labelled;
    "Cloud folder unavailable" errors when the sync app isn't running.
-   **Device notifications (F-007):** the Drives page refreshes itself
    when a drive is added, removed or given another letter (TD-032).
    Manual tests are documented for a person to run
    (`12-quality-assurance.md` §3.1).
-   **Large Files (F-016):** not-modified-for, folder-path and custom
    size filters, more presets, multi-select, review queue, export.
-   **Review queue and multi-select (TD-029):** extended selection, copy
    of several paths and context menus in Storage Explorer, Large Files
    and Search; a review queue opened from the title region; nothing on
    disk changes.
-   **Retention (TD-030):** Settings → Scan history; stricter choices are
    confirmed and applied at once.
-   **Hard links (F-028, TD-026):** totals count each hard-linked file
    once; link counts shown; categories corrected.
-   **Incremental updates (F-019, F-020, TD-025):** full scans record a
    change journal checkpoint; Update reads the NTFS change journal as a
    standard user (unprivileged read, no UAC), lists again only changed
    and previously unreadable folders, and falls back to a full scan with
    a stated reason whenever the history can't be trusted.
-   **Search (F-017, TD-027):** per-scan FTS5 name index; header search
    box (Ctrl+F) and Search page with filters.
-   **Treemap and charts (F-021, TD-031):** treemap view in Storage
    Explorer, category share bar, size-over-time chart in Scan History;
    custom controls, validated palette, text on every mark (resolves
    BLK-005).
-   **Scan history and export (F-018, F-035, TD-028):** comparison of two
    scans by category and folder; CSV, JSON, HTML and PDF export of every
    list, full scans and scan reports.
-   **Schema version 3** (`0003_incremental_search_review.sql`): session
    kind, base session, journal checkpoint, totals version, hard-link and
    cloud counters, link corrections and counts, path and identity
    indexes, `SearchIndexes`, `ReviewItems`.
-   **Dependency:** PdfPig 0.1.16 (Apache-2.0). PDFsharp was rejected:
    version 6.5.0 is AGPL-3.0-only (DEC-024).

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0
errors; 412 tests, 411 passed, 1 skipped (creating symbolic links needs
Developer Mode or elevation); no vulnerable packages; self-contained
win-x64 publish succeeded; signing skipped (DEC-025). The published app
opened its window, migrated the local database to schema version 3 and
closed cleanly. One unit test for the folder roll-up was added after the
pipeline run (413 tests; the Application suite was run again and
passes). New suites and highlights:

-   `IncrementalScanTests` (Phase 3 exit criterion): on a real NTFS
    folder, an update built from the change journal after deletes,
    renames, moves, appends and a new subtree equals a fresh full scan
    entry for entry, without listing the 40 unchanged folders; real hard
    links counted once.
-   `UsnChangeJournalTests`: checkpoint, changed folders, recreated and
    invalid checkpoints, too many changes, record parsing.
-   `SqliteIndexFeatureTests`: hard links, incremental batch operations
    (remove, clear, replace, stale), copy, baseline lookups, search,
    comparison, retention, export streaming, review queue.
-   `ReportExporterTests`: CSV formula neutralization and quoting, JSON
    schema, HTML encoding and script policy, row limit, PDF read back.
-   Coordinator: update planning and every fallback; Desktop view models
    for Drives, Large Files, Explorer (treemap, tree sync), Search,
    Review queue, Scan History (comparison, trend), Settings (retention);
    treemap layout; hard-link and roll-up algorithms; search text.

**Performance (BLK-009):** development PC, C: (NVMe, about 1M files),
standard user, Debug build, separate data folder:

  Measure                                   Before fix   After fix
  ----------------------------------------- ------------ ------------
  Full scan, total                          231 s        175 s
  ... of which listing                      126 s        130 s
  ... of which finishing (totals, links,    104 s        44 s
  search index, retention)
  Incremental update, total                 131 s        61 s
  ... journal read and planning             1.3 s        1.1 s
  ... copy and relisting (199 folders)      32 s         32 s
  ... finishing                             99 s         29 s
  Name search ("setup", 1,554 matches)      55 ms        47 ms
  Peak working set during the full scan     228 MB       311 MB

The fix replaced five passes over the files through an index (about
11 s each) with sequential row-ID range passes, and the per-depth SQL
roll-up (18--33 s) with an in-memory roll-up (under 3 s) (TD-026).
Listing took longer than the Phase 2 baseline (90--132 s in total); the
cause isn't established. Likely contributors are OneDrive's folders
being listed now and normal cache variation; to be measured again under
BLK-009. The update is about three times faster than a full scan on this
drive, but its copy and finishing steps still grow with the number of
files (TD-025). Hard links on C:: 94,057 extra names; 15.9 GB on disk is
no longer counted twice.

### Defects found and fixed during Phase 3

-   PDFsharp's licence (AGPL) found on restore; replaced with PdfPig.
-   FTS5 `detail=none` rejects column filters; queries match the table.
-   Session finishing took about 100 s on 1M files (see above).
-   Visual review: comparison title read "C::"; a clipped change column;
    muted chart bars showed a darker band and a near-zero bar dipped
    below the baseline; the category bar was centred instead of
    left-aligned; System and Application data shared a colour; text boxes
    had no visible hint; the treemap caption called zero-byte items
    hidden; empty comparison tables showed only headers.
-   A custom size that wasn't a number also blocked the folder query.

### Open

-   Manual F-007 tests and the journal-reset check
    (`12-quality-assurance.md` §3.1): need a person with administrator
    rights and a USB drive.
-   Faster incremental updates (avoid copying unchanged rows); listing
    time to re-measure (BLK-009).
-   Narrator and high-contrast review of the new pages (BLK-006).
-   Elevated scanning (DEC-023, Phase 6 helper). TD-014 redaction gap.
-   CI has not yet run on GitHub (the repository has not been pushed).

### Next

Phase 4: application residue and temporary data (installed-app
inventory, registry and MSIX inventory, known paths, evidence and
confidence, temporary and cache rules, keep/ignore/review workflows).
