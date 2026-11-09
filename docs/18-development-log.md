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

## 2026-11-03 --- Phase 4: application residue and temporary data

### Completed

-   **Application inventory (F-022, TD-033):** uninstall registry (64-
    and 32-bit machine views, current user) and the current user's Store
    packages from the AppModel repository. Nothing is run. History is
    kept in `Applications`, so an application seen before and missing
    now gives the strongest evidence.
-   **Residue discovery (F-023, TD-034):** AppData Local, Roaming,
    LocalLow, Local\Programs, Local\Packages, ProgramData and both
    Program Files folders; vendor folders one level deeper. Evidence for
    and against each folder, a confidence, and a separate risk. Package
    data is matched by package family and version.
-   **Temporary and cache rules (F-024, TD-035):** 12 documented rules
    with purpose, consequence, risk and age; Windows-managed locations
    are reported but can't be added to the review queue.
-   **Decisions:** keep, ignore and "in use by …" per folder; keep or
    ignore per application group; keep per temporary rule
    (`UserDecisions`). "Add to review queue" is the only route towards
    removal; nothing is preselected or removed.
-   **Pages:** Application Residue (warning banner, evidence inspector,
    confidence filter, installed-applications list, export) and
    Temporary Files (rules, details, matched folders, export).
-   **Scanner version 3 and schema version 4 (TD-036):** each folder
    records the newest file inside and the space taken by documents and
    media, rolled up at completion; `Applications` and `UserDecisions`
    tables (`0004_residue_temporary.sql`).
-   **Read-only guard:** `ReadOnlyAnalysisTests` fails if the Residue
    project contains file, registry-write or process calls.

### False-positive review (exit criterion)

The analysis was run against a full scan of C: on the development PC
(1,005,737 files) and every candidate was checked by hand against the
inventory, the PATH and the disk.

**Rules version 1: 76 candidates; at least 40 wrong.** The causes:

  Cause                                        Examples                                 Fix (version 2)
  -------------------------------------------- ---------------------------------------- ------------------------------
  Tool runs from the PATH, not registered      Roaming\npm, ProgramData\chocolatey,     PATH programs count as
                                               ChocolateyHttpCache, JetBrains           installed
  Recent activity, no evidence of removal      com.baruchiel.auren (app under           Changed within 30 days and no
                                               development), ms-playwright, pnpm-cache, other evidence: in use, not
                                               tauri, Prisma, JetBrains (Local)         listed (18 folders)
  Installed, name not recognized               PCHealthCheck ("Windows PC Health        Name inside the product name;
                                               Check"), WSL ("Windows Subsystem for     initials
                                               Linux")
  Node.js tool data                            nextjs-nodejs, prisma-nodejs,            "<tool>-nodejs" belongs to
                                               claude-cli-nodejs                        Node.js
  Reverse domain names                         com.adobe.dunamis (Adobe installed)      Product and vendor parts
  Driver and hardware vendors                  Realtek, Dell, HP, SoundResearch         Left out
  Shared runtime folders                       CEF, boost_interprocess                  Left out
  FreeAll's own data                           AppData\Local\FreeAll                    Left out

**Rules version 2: 33 candidates** (13 Medium, 19 Low, 1 Unknown; none
High, because FreeAll hasn't yet seen anything uninstalled on this PC).
Of these:

-   **About 17 plausible leftovers**, none confirmed by the owner yet:
    obsidian and obsidian-updater, MobaXterm, riot-client-ux and two
    Riot Games folders, two Godot folders, UIU, WireSock Foundation,
    VMAILMBOXCONVERTER, TempM, Tool Infomation, SolidDocuments,
    update-informer-rs, and two GIMP package folders of older versions
    (right by construction: a newer version is installed).
-   **About 15 probably still in use:** Dart and Flutter data (Pub,
    .dartServer, .dart-tool, Dart; Flutter is on the PATH under its own
    name), shimgen (Chocolatey), pnpm, kotlin, turborepo, vitest, astro,
    Movavi\srecorder24 (Movavi Screen Recorder is installed), and the
    data of two apps under development (four folders). All are Low
    confidence except shimgen (0 bytes) and astro (160 bytes).
-   **1 Unknown:** ProgramData\Whesvc can't be read.

None of the remaining false positives is High confidence, all carry
their evidence, and every folder with documents or media is High risk.
Portable programs and apps under development are the limit of this
method: they leave no trace in the inventory. The owner's sign-off and a
review on a PC not used for development are recorded as BLK-018.

**Temporary Files:** 20 locations, 20.0 GB. The age estimate is right
but coarse: `npm-cache` (8.16 GB) shows 0 bytes unchanged because its
one child folder `_cacache` changes daily; `.gradle\caches` shows
1.96 GB of 5.17 GB unchanged for 90 days. Your temporary files: 4.55 GB,
of which 4.24 MB is older than 7 days (today's build output).

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0
errors; 462 tests, 461 passed, 1 skipped (symbolic links need Developer
Mode or elevation); no vulnerable packages; self-contained win-x64
publish succeeded; signing skipped (DEC-025). The published app opened,
migrated the local database to schema version 4 and closed cleanly.
New: `ApplicationMatcherTests` (every matching rule, including the
version 2 additions), `RegistryInventoryTests` (family names, icon
folders, a live read of this PC), `ResidueAnalysisTests` (a synthetic
profile in a real SQLite index: every confidence and risk rule, every
skip found in the review, PATH programs, in-use counting, decision
precedence, temporary rules, age query, roll-up, stores),
`ResidueViewModelTests` and `TemporaryFilesViewModelTests`,
`ReadOnlyAnalysisTests`, and scanner tests for the new folder figures.

**Measurements** (development PC, C:, Debug build):

  Measure                                         Value
  ----------------------------------------------- ------------------------
  Full scan (scanner version 3)                   151--160 s
  Peak working set during the full scan           376--392 MB
  Application inventory (347 applications)        under 0.1 s
  Residue analysis (365 folders)                  under 0.1 s
  Temporary analysis (20 locations)               0.1 s (was 39--45 s)

Peak memory rose from 311 MB in Phase 3, probably because of the new
per-folder figures; to be watched under BLK-009.

### Defects found and fixed during Phase 4

-   Matching: a vendor's product name matched its vendor folder
    ("Adobe" as "Adobe Acrobat"), hiding leftover products inside it.
    Exact names come first, then the vendor, then loose matches.
-   Temporary analysis took 39--45 s: the age estimate paged through
    every child, and paths compared without case scanned every folder
    row of the scan. Now one aggregate query per folder and an indexed
    walk from the scan root.
-   The review findings above (rules version 2).
-   Visual review: clipped columns on both pages, "at least 0 bytes" for
    unreadable locations, and a checkbox label ("in-use folders") that
    could be confused with the new automatic in-use count.

### Open

-   BLK-018: owner sign-off on the false-positive review; a review on
    another PC.
-   In the capture's dark pass, the selected row of the Temporary Files
    rules grid has white text on the light accent; selected rows in
    other grids have dark text. The capture switches theme while running,
    so this may not happen in normal use; to check with the app started
    in dark mode (BLK-006).
-   High confidence appears only once FreeAll has seen an application
    removed; the history starts with this version.
-   Other users' profiles aren't examined.
-   Carried over: manual F-007 tests, Narrator and high-contrast review,
    elevated scanning, GitHub push and first CI run.

### Next

Phase 5: duplicate detection.

## 2026-11-04 --- Phase 5: duplicate detection

### Delivered

-   **Duplicate Files page** (F-025 to F-027): one drive at a time, a
    smallest-size choice, an option for system folders, progress and
    Stop, groups by space that could be freed with a category filter, each
    group's copies with location, hard-link notes and a warning in
    program folders, "Check again", review queue, export.
-   **Engine** (`FreeAll.Duplicates`): size groups from the index, largest
    first; streaming SHA-256; byte-for-byte comparison behind every group;
    identity and change metadata checked before and after each read; two
    size groups at once, never more than two files open each.
-   **File access:** a metadata-only handle decides whether contents may
    be read; contents come through `ReOpenFile`. A path that now leads
    elsewhere is refused (final path, volume serial, file ID). Other
    programs are never blocked. TD-038 records the measurements behind
    this.
-   **Saved hashes** (`FileHashes`, schema version 5), invalidated by
    every condition in `11-data-model.md` §5. They only choose what to
    compare, so they can't cause a false claim.
-   **Hard links** (F-028): names of one file are one copy; a file with
    names elsewhere counts as freeing nothing. Reclaim uses each file's
    real allocation.
-   **Review queue guard:** every name of a group can't be queued.
    Phase 6 must repeat this against the disk (TD-041).
-   **Fixed along the way:** items from Application Residue and Temporary
    Files showed "Unknown" as their origin in the review queue
    (`ScanText.Origin` had no case for them).
-   **Fixed along the way:** `ScanCoordinator` closed the channel before
    cancelling after a store failure, so a scanner could find its writes
    failing with no cancellation visible yet. This made
    `A_store_failure_stops_the_scanner…` fail now and then, and it failed
    the first full build of this phase. It now cancels first; the suite
    then passed 25 times out of 25.

### Real-data review (development PC, C:, Debug build)

Scan: 969,365 files, 332 GB, partial (118 errors, as in earlier phases).
Timings include writing results; the second run reuses saved hashes.

  Run                                 Time     Hashed     Compared  Groups  Could free
  ----------------------------------- -------- ---------- --------- ------- ----------
  Default, before the tool-folder     61.9 s   5.0 GB     4.8 GB    192     2.38 GB
  rule
  Same, second run                    14.4 s   none       4.8 GB    192     2.38 GB
  Default, with the tool-folder rule  9.9 s    1.5 GB     1.4 GB    11      699 MB
  Same, second run                    4.0 s    none       1.4 GB    11      699 MB
  Including system folders            156.1 s  17.9 GB    14.9 GB   928     7.39 GB

-   7,568 files shared a size at 1 MB or more. No hash ever matched
    without the bytes matching (0 mismatches in any run).
-   Peak working set 409 MB, the same as the scan on its own; the analysis
    added nothing measurable to that peak. In an earlier run with a lower
    scan peak (343 MB) the analysis reached 371 MB. UI delay at most 22 ms.
-   With system folders included: 137 files unreadable, 5 in use, 25
    changed since the scan, all counted, and the run said so. Nothing
    failed on WinSxS, locked files or denied folders.
-   **The review changed the rules.** Most of the 192 groups in the first
    run were `.vscode\extensions`, `.gradle` and `.maestro` files: real
    duplicates, but removing one breaks a tool, and they carried no
    warning. Tool folders at the top of a profile are now treated like
    application data (TD-037). All 11 groups that remain are real
    duplicates by content, but most are still not obvious savings: two
    `freeall.db` test databases, the same `node.exe` in two tool folders,
    and files under `node_modules`. Which of those are worth removing
    needs a person, as the page says.
-   No cloud-only file shared a size with another file at 1 MB or more on
    this PC, so the run itself didn't exercise that rule. It was checked
    directly on four real OneDrive placeholders instead.
-   A copy of this PC's local database upgraded from schema version 4 to 5
    and the app opened it. That database is small, so this is a light
    check; the migration only adds tables. The published build was not
    launched, because that would migrate the real database; the Debug
    build was.

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0
errors; 656 tests, 654 passed, 2 skipped (symbolic links need Developer
Mode or elevation, one in the scanner tests and one here); no vulnerable
packages; self-contained win-x64 publish succeeded; signing skipped
(DEC-025). New: 167 tests in `FreeAll.Duplicates.Tests`, and
`DuplicatesViewModelTests` and a read-only guard for the project.

### Defects found and fixed during Phase 5

-   A database failure at the final save left a run marked as running
    until the next start (found by a test written for it).
-   Pressing Stop while groups were being saved could lose them while
    the counts still included them (found by reviewing the code; the fake
    store now honours cancellation, so a test covers it).
-   A file whose final path couldn't be read was counted as "reached
    through a link". It is still refused, but as unreadable.
-   "Check again" said "still identical" without checking that neither
    file moved during its own comparison.
-   Selected rows: the path line used a fixed secondary colour and was
    nearly unreadable on the accent. It now inherits the row's colour.
-   The real-data review above (tool folders).

### Open

-   F-025 to F-027 stay IMPLEMENTED: see the list in
    `17-feature-checklist.md`. The `security-review` tool couldn't run
    (it needs a remote, which this repository doesn't have yet), so the
    focused review `15-ai-agent-rules.md` §7 asks for was the
    implementing agent's own.
-   The default scope (system, application data and tool folders left
    out) is an engineering decision within DEC-013 and PR-010, recorded
    as TD-037. The product owner may want a different default.
-   A write through a memory-mapped view moves no time until flushed, so
    it can't be told from one made just after the comparison (TD-038).
-   Windows may update last-access times when contents are read, on
    volumes where that is enabled (TD-038).
-   The same secondary-colour problem exists on other pages' selected
    rows, for example Application Residue (BLK-006).
-   Carried over: BLK-018 (residue sign-off), manual F-007 tests, Narrator
    and high-contrast review, elevated scanning, GitHub push and first CI
    run.

### Next

Phase 6: cleanup, quarantine and helper. It must keep the rule that a copy
outside the selection still exists and matches at removal time (TD-041).

## 2026-11-04 --- Phase 6: cleanup, quarantine and helper

### Delivered

-   **Cleanup review** (from the review queue): the exact items, a fresh
    check of each with its risk and reasons, the destination, a final
    confirmation, then what really happened to each item. Permanent
    deletion has its own acknowledgement and a distinct button.
-   **Recycle Bin** (F-029) with a gate against silent permanent deletion
    and proof of arrival (TD-042).
-   **Quarantine** (F-030, F-031): an atomic move on one drive; copy,
    verify, then remove across drives (TD-044).
-   **Restore and conflicts** (F-032): integrity first, nothing overwritten
    without a choice (TD-045).
-   **Protected-path policy** applied in the app and the helper, handle-based
    changes, and revalidation immediately before each item (TD-043).
-   **The last copy of a duplicate can't go:** Phase 5's rule, now checked
    against the disk with a byte comparison.
-   **Elevated helper** (F-033): allowlisted commands, a per-session pipe,
    both ends checked, independent validation (TD-047).
-   **Audit** (F-037) written ahead of every step, and the **Activity** page
    with operations, per-item results and quarantine (TD-046).

### What the spike changed

Before designing, the shell's delete was tested on a location with no
Recycle Bin. With the usual flags it destroyed the file and reported
success, with no error of any kind. That is the behaviour the rules
forbid, and it is invisible from the API's result. The design followed
from that measurement: refuse in `PreDeleteItem`, and require proof in
`PostDeleteItem`. The test repeats the scenario with the real code on a
`subst` drive.

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0
errors; 871 tests, 869 passed, 2 skipped (symbolic links need Developer
Mode or elevation); no vulnerable packages; self-contained win-x64
publish succeeded with the helper digest embedded and checked against the
published helper; signing skipped (DEC-025). New: 73 tests in
`FreeAll.Cleanup.Tests`, 128 in `FreeAll.Security.Tests`, and
`CleanupViewModelTests`. Both new suites were then run five more times
without a failure.

No real user data was touched by any test. Tests that use the Recycle Bin
remove their own items by the path Windows returned; the one test that
creates `FreeAll Quarantine` at the top of a drive removes it again.

### Defects found and fixed during Phase 6

Found by reviewing the code after the tests passed:

-   Stopping while quarantine was hashing an item threw out of the run,
    leaving the operation unfinished and the item in progress.
-   An unexpected error on one item could abort the batch. It is now that
    item's result.
-   The helper's UAC launch path wasn't tested, because the tests turned
    shell-execute off. They now use the same launch, without `runas`.
-   A damaged quarantine record could have pointed deletion at any file.
    Records that don't point into quarantine are now refused.
-   Start-up reconciliation would have marked an item abandoned because
    its drive wasn't connected.
-   The quarantine folder was created and then given its access list,
    rather than created with it.

Found by looking at the screens: the button for permanent deletion looked
like any other action; "0 bytes measured" after a move within one drive;
destinations shown as volume GUID paths.

### Open

-   BLK-019: the helper has never run elevated. BLK-020: an independent
    security review. Both need a person.
-   Elevated scanning (DEC-023) isn't built; cleanup doesn't depend on it.
-   Manual checks in `12-quality-assurance.md` §3.2: a real removable
    drive, an item too large for the Recycle Bin, a crash during a copy.
-   Folder items often show as changed unless the scan is recent, because
    their contents must still match. That errs on the safe side but may
    frustrate; worth watching in real use.
-   Files added to a folder between its final check and its removal go with
    it (TD-043).
-   An operation that fails to write its audit stops there; the item in
    progress shows as interrupted at the next start.
-   Carried over: BLK-018, manual F-007 tests, Narrator and high-contrast
    review, GitHub push and first CI run, and Phase 5's own list.

### Next

Phase 7: Windows storage, installer and release hardening. Before that:
commit, push, and arrange the independent review.

## 2026-11-08 --- Phase 7: Windows storage, installer and release hardening

### Delivered

-   **Windows Storage page** (F-034, TD-048): fifteen areas Windows manages,
    measured from the scan of the Windows drive, each with what it is, the
    supported way to reduce it and the Windows tool for it. Windows' own
    Recycle Bin figures for every drive. Tools open from a fixed allowlist of
    documented Settings pages and Windows programs; FreeAll removes nothing
    there. BLK-007 resolved for v1.
-   **Installer** (F-002, TD-049): a project-owned per-user installer compiled
    with Native AOT (2.3 MB) with the app appended as a verified payload.
    Install, upgrade with a way back, repair, refusal to downgrade, an
    uninstaller that asks about data and removes only what it installed.
    BLK-003 resolved.
-   **Update checks** (F-036, TD-050): asked once at first start, a signed
    manifest (ECDSA P-256, project-owned key), strict parsing, replay and
    expiry protection, HTTPS only, a verified download and a second check
    under a locked handle before the installer starts. Release scripts for
    the key and manifests. Waiting on BLK-021 for the real address and key.
-   **Accessibility and localization readiness** (F-038, TD-051): decorative
    glyphs hidden from screen readers, an automation audit of every page in
    the capture run, static markup tests, Windows Text size followed live,
    regional formats in bindings.
-   **Compatibility matrix** (F-003): `24-compatibility-matrix.md` with
    Microsoft's servicing dates and the first tested row;
    `tools/Test-Compatibility.ps1`.
-   **Performance and soak** (TD-052): `FreeAll.Performance.Tests` (short soak
    and cancellation latency on every build, long soak on request).
-   **Release pipeline** (TD-052): setup file, installer smoke test, SHA-256
    checksums and an SPDX SBOM in `artifacts/release`; CI uploads them.
    `25-release-notes.md` with known limitations and the release gates.

### Measurements (development PC: Windows 11 Pro 26H2, NVMe)

-   **Windows Storage on C:** (scan of 964,541 files): component store
    11.9 GB with hard links counted once, Windows folder at least 20.5 GB,
    page and swap files 11.0 GB, hibernation file 6.34 GB, installer cache
    1.27 GB, update downloads 223 MB, Recycle Bin 418 MB (Windows' figure).
    Restore points, Windows temp, memory dumps and Delivery Optimization:
    not readable as a standard user, and shown that way.
-   **Installer:** the published app (443 files, 150 MB) packed into a 66 MB
    setup file in 31 s; silent install 3.5 s; the installed uninstaller
    0.5 s, including moving itself out of the folder.
-   **Long soak** (40 cycles: full scan of 20,000 files, five changes and a
    renamed folder, update, search, retention; Release build): full scans
    0.4 to 1.0 s, updates 0.3 to 0.6 s (6 or 7 folders listed again), search
    33 to 81 ms. Handles 463 → 612 and threads 21 → 30 during the first
    cycles (thread-pool warm-up), then flat to cycle 40; managed memory
    1.1 → 1.2 MB after collection; database 11.1 → 11.4 MB. Peak working set
    109 MB.
-   **Cancellation:** the run ended 109 ms after Cancel, with a partial
    session saved.
-   **C: scan in the capture run:** 169 s, 5,695 files/s, peak working set
    423 MB, UI idle delay at most 23 ms, as in earlier phases.

### Tests

`build.ps1 -Configuration Release -Locked -Publish`: 0 warnings, 0 errors;
1,043 tests, 1,040 passed, 3 skipped (the two symbolic-link tests, which
need Developer Mode or elevation, and the explicit long soak, run separately
with `tools/Run-Soak.ps1` and passed); no vulnerable packages; helper,
desktop and installer published; setup file built and installed and
uninstalled by the smoke test; SBOM (20 packages) and checksums written;
signing skipped (DEC-025). New: 43 in `FreeAll.Installer.Tests`, 57 update
and Recycle Bin tests in `FreeAll.Integration.Tests`, 14 in
`Phase7ViewModelTests`, 3 in `FreeAll.Performance.Tests`, and installer and
accessibility rules in `FreeAll.Architecture.Tests`.

Tests and smoke runs never used the real install locations: every location
was redirected, and registry entries were written only under
`HKCU\Software\FreeAll.Tests\` and removed.

### Defects found and fixed during Phase 7

-   The setup file's zip was written after the installer stub in the same
    stream, so its internal offsets counted from the start of the file and it
    couldn't be read back (found by the installer tests). The zip is now
    built on its own and appended.
-   An error in a scripted run (`/pack`, `/silent`) showed a dialog and waited
    for a person (found in the first manual run). Scripted runs now report on
    standard error and exit.
-   With the install folder on E: and `%TEMP%` on C:, the uninstaller
    "moved" itself by copying, and Windows left the original in the folder,
    so the folder stayed (found by the `build.ps1` smoke test, whose folders
    are on E:). It now moves only within the same drive and checks the
    original is gone.
-   `build.ps1` started the installer with `Start-Process`, which doesn't
    quote arguments; the repository path contains a space. Arguments now go
    through `ArgumentList`.
-   Seven controls had no accessible name (found by the new automation
    audit): buttons with an icon and a label, which WPF doesn't name from the
    label, and two lists. The static test had assumed the label was enough;
    it now requires a name.
-   An area under a folder the scan couldn't list showed "Not on this drive"
    (Delivery Optimization). It now shows "Not readable".
-   The Windows tools card left room for six areas; the tools moved into a
    menu button and all areas fit.
-   The capture tool cut 150 % screenshots to the untransformed size.

### Open

-   **Product owner:** BLK-021 (update address and key), BLK-022 (privileged
    Windows cleanup; recommendation: keep opening Windows' tools), BLK-023
    (the Overview page is still a placeholder).
-   **People:** an interactive install, upgrade and uninstall on a clean
    account (`12-quality-assurance.md` §3.3); the other serviced Windows 11
    releases (`24-compatibility-matrix.md`); pressing each Windows tool
    button; Narrator, high contrast and keyboard-only review (BLK-006); the
    elevated helper through UAC (BLK-019); an independent security review
    (BLK-020), which should now also cover the installer and the update
    verification.
-   At 150 % text on a window that isn't maximised, list-and-details pages
    crop their details pane; the panes should stack on narrow windows.
-   The uninstaller leaves a 2 MB copy of itself in `%TEMP%`.
-   Code signing (DEC-025): SmartScreen and "Unknown publisher" remain.
-   Carried over: BLK-018 (residue sign-off), manual F-007 tests, benchmarks
    on other storage types (BLK-009), GitHub push and the first CI run.

### Next

The release can't ship until the gates in `25-release-notes.md` close. Most
are now people's tasks; the product-owner answers (BLK-021, BLK-023) decide
the remaining build work. Recommended order: push to GitHub and get a green
CI run; the independent security review; answer BLK-021 and BLK-023; the
manual installer, helper and Narrator passes on a clean test PC.
