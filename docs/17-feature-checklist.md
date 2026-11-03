# FreeAll --- Feature Checklist

Use the exact status vocabulary defined in `15-ai-agent-rules.md`.

  -------------------------------------------------------------------------------------------
  ID                Feature                   Status            Acceptance evidence
  ----------------- ------------------------- ----------------- -----------------------------
  F-001             WPF .NET 10 solution      IN PROGRESS       Build and CI
                    foundation                                  

  F-002             Per-user self-contained   NOT STARTED       Install/upgrade/uninstall
                    x64 package                                 tests

  F-003             Windows 11 servicing      NOT STARTED       Tested release/edition matrix
                    compatibility matrix                        

  F-004             ARM64 feasibility and     NOT STARTED       Native dependency and
                    validation                                  end-to-end tests

  F-005             WPF modern design system  IN PROGRESS       Theme and accessibility
                                                                review

  F-006             Dashboard                 NOT STARTED       UI acceptance

  F-007             Drive discovery           IMPLEMENTED       Volume integration tests

  F-008             Volume identity and mount IMPLEMENTED       Mount-point tests
                    alias handling                              

  F-009             Streaming filesystem      IMPLEMENTED       Large tree and memory tests
                    scanner                                     

  F-010             Hidden/system/read-only   IMPLEMENTED       Attribute tests
                    discovery                                   

  F-011             Error and completeness    IMPLEMENTED       Permission/error tests
                    accounting                                  

  F-012             Reparse-point safe        IMPLEMENTED       Junction/symlink loop tests
                    handling                                    

  F-013             Directory aggregation     IMPLEMENTED       Size/count correctness tests

  F-014             Storage explorer          IMPLEMENTED       Virtualization tests

  F-015             File classification       IMPLEMENTED       Classification corpus

  F-016             Large files and folders   IMPLEMENTED       Query and UX tests

  F-017             Search index              IMPLEMENTED       Search
                                                                correctness/performance

  F-018             Full scan history         IMPLEMENTED       Persistence/recovery tests

  F-019             Incremental scan          IMPLEMENTED       Change tracking tests

  F-020             USN Journal acceleration  IMPLEMENTED       Journal reset/wrap fallback
                                                                tests

  F-021             Storage treemap           IMPLEMENTED       Large-data visualization
                                                                tests

  F-022             Application inventory     NOT STARTED       Inventory tests

  F-023             Residue                   NOT STARTED       False-positive review
                    evidence/confidence                         

  F-024             Temporary/cache rules     NOT STARTED       Rule and safety review

  F-025             Duplicate candidate       NOT STARTED       Same-volume scope tests
                    grouping                                    

  F-026             SHA-256 streaming hash    NOT STARTED       Hash correctness tests

  F-027             Byte verification         NOT STARTED       Mismatch/collision-boundary
                                                                tests

  F-028             Hard-link awareness       IMPLEMENTED       File identity tests

  F-029             Recycle Bin workflow      NOT STARTED       Restore and failure tests

  F-030             Quarantine same-volume    NOT STARTED       Integrity/restore tests

  F-031             Quarantine cross-volume   NOT STARTED       Copy-verify-remove tests

  F-032             Restore/conflict handling NOT STARTED       Conflict tests

  F-033             Elevated helper and       NOT STARTED       Security test suite
                    secure IPC                                  

  F-034             Windows storage           NOT STARTED       Supported API validation
                    integration                                 

  F-035             CSV/JSON/HTML/PDF export  IMPLEMENTED       Format and injection tests

  F-036             Update opt-in and         NOT STARTED       Update security tests
                    signature verification                      

  F-037             Audit and diagnostics     IN PROGRESS       Audit completeness tests

  F-038             Accessibility and high    IN PROGRESS       Manual accessibility review
                    DPI                                         

  F-039             Persistence foundation    IMPLEMENTED       Migration, recovery and
                    (SQLite, migrations,                        backup tests
                    backup)                                     

  F-040             Logging, redaction and    IMPLEMENTED       Logging and redaction tests
                    error presentation                          

  F-041             Windows 11 / x64 runtime  IMPLEMENTED       Platform gate tests
                    gate                                        
  -------------------------------------------------------------------------------------------

Update status only with evidence. Add feature-specific acceptance
criteria before implementation begins.

## Feature acceptance criteria

### F-001 --- WPF .NET 10 solution foundation

Acceptance criteria:

1.  Solution contains every project in `05-system-architecture.md` §3
    with dependencies matching TD-001.
2.  Dependency, UI-framework, privilege-manifest and x64 rules are
    enforced by automated tests.
3.  `build.ps1 -Configuration Release -Locked -Publish` restores in
    locked mode, builds with zero warnings, runs tests, checks for
    vulnerable packages and publishes self-contained win-x64 output.
4.  The published Desktop app launches as a standard user and closes
    cleanly.
5.  CI runs `build.ps1` on every push and pull request and passes.

Evidence (2026-11-02):

-   Criteria 1--4 verified locally: 0 warnings, 0 errors; 40/40
    architecture tests pass; injected violations (UI framework in
    Scanner, Scanner → Cleanup reference) were detected by 3 failing
    tests; published `FreeAll.exe` opened its window and exited with
    code 0.
-   Criterion 5 pending: the GitHub repository is not yet pushed, so
    CI has not run. Builds are unsigned by decision (DEC-025).

### F-005 --- WPF modern design system (IN PROGRESS)

Acceptance criteria: colour tokens in light, dark and high contrast, with
no hard-coded colours in views; shape, spacing and typography tokens;
the Windows accent colour as the accent source; light/dark coverage of
every screen; documented component styles; accessibility review.

Evidence (2026-11-02): token dictionaries and `Metrics.xaml`/`Controls.xaml`
exist; screenshots of all 12 pages in light and dark were reviewed
(`FreeAll.exe --capture-screens <folder>`, Debug builds only).
Outstanding: high-contrast and Narrator validation and the
decorative-icon automation fix (BLK-006).

### F-007 / F-008 --- Drive discovery, volume identity

Status: F-007 IMPLEMENTED, F-008 IMPLEMENTED.

Acceptance criteria: fixed volumes discovered automatically; removable
volumes on demand; network drives excluded and shown as excluded;
volumes keyed by volume GUID; one entry per volume whatever its mount
points; volumes that are not ready handled; drive-letter changes and
disconnects handled across sessions.

Evidence (2026-11-03): `WindowsVolumeDiscovery` integration tests and
`VolumeScanPolicy` unit tests (Phase 1). Volume identity is now
persisted in `Drives` by GUID, with mount points, availability and last
seen time (`SqliteScanStoreTests.Discovery_records_volumes_by_guid…`).
Sessions, exclusions and the index refer to the GUID; display paths use
the current mount point. A volume that stops responding during a scan
ends the session as partial. TD-012 confirmed (DEC-028).
Phase 3 (2026-11-03): the main window listens for volume arrival and
removal broadcasts (also sent for letter changes) and the Drives page
refreshes itself, coalesced over one second (TD-032;
`DrivesViewModelTests.A_drive_change_refreshes…`). Outstanding before
COMPLETED: the manual tests in `12-quality-assurance.md` §3.1 (drive
letter changed, USB unplugged during a scan). They need administrator
rights and a physical USB drive, which the development agent doesn't
have; the scanner side (GUID paths, volume lost ends the session as
partial) is covered by automated tests.

### F-009 --- Streaming filesystem scanner (IMPLEMENTED)

Acceptance criteria: streams directory entries without materializing
whole directories; bounded memory independent of file count; batched
persistence; configurable workers; cancellation persists a partial
session; scans never modify anything.

Evidence (2026-11-03): `WindowsVolumeScannerTests` (21 tests on real
temporary trees): totals, metadata, a 3,000-file folder flushed in
bounded batches while it is listed, identical inventories with 1 and 8
workers, cancellation consistency, a snapshot before and after scanning
showing nothing changed. `ScanPipelineTests` (scanner → coordinator →
SQLite). A baseline full scan of the development PC's C: drive is in
`18-development-log.md`. Outstanding: benchmarks on other storage types
(BLK-009).

### F-010 --- Hidden/system/read-only discovery (IMPLEMENTED)

Evidence: hidden, system and read-only files and a hidden folder are
listed with their attributes (`Hidden_system_and_read_only…`). The
explorer shows attribute letters and the inspector shows names.

### F-011 --- Error and completeness accounting (IMPLEMENTED)

Acceptance criteria: errors recorded with path, operation, error code,
category and time; the scan continues after recoverable errors; a scan
is complete only when its defined scope was traversed without
unresolved errors (TD-020); totals that are lower bounds are marked.

Evidence: access-denied folder test (recorded, scan continues, partial);
depth-limit test; `ScanRulesTests` for the completeness rules;
`ScanCoordinatorTests` for cancellation, store failure, scanner defect,
unreadable root and the 10,000 stored-error cap; incomplete counts
rolled up per folder (`SqliteScanStoreTests`); Scan History lists the
errors. Outstanding: sharing-violation and volume-removal cases are
classified but not exercised by an automated test.

### F-012 --- Reparse-point safe handling (IMPLEMENTED)

Evidence: junctions, including one that points back at the scan root,
are recorded and never followed; a scan root that is a junction is
refused; symbolic links are recorded and not followed (test skipped on
machines without Developer Mode or elevation, which is the case on the
development PC); reparse tags are classified (`ScanRulesTests`). A
directory replaced by a link between listing and opening is skipped
(TD-018; not covered by an automated race test). Cloud placeholder
folders (OneDrive) are listed and links are still never followed
(DEC-029; `CloudAndReviewRulesTests`; checked on the development PC's
OneDrive: its contents now appear, 46 cloud-only files labelled, nothing
downloaded). Outstanding: folder mount point test (creating one needs
elevation); an automated test with a registered cloud sync root.

### F-013 --- Directory aggregation (IMPLEMENTED)

Evidence: recursive file, folder, logical and allocated totals and
incomplete counts computed bottom-up in SQL and checked against a known
tree (`SqliteScanStoreTests`) and a real one (`ScanPipelineTests`).
Since Phase 3, hard-linked files count once (F-028, TD-026), and totals are rolled up in memory.

### F-014 --- Storage explorer (IMPLEMENTED)

Index-backed (TD-023): drive scan selector, lazy tree, breadcrumbs,
sortable list paged 500 at a time with sorting in SQL, folder rows with
recursive totals and plain-language status, inspector with full path,
volume, file ID, timestamps, attributes, category with its reason and
scan provenance, Open in File Explorer and Copy path. Evidence:
`ExplorerViewModelTests` (10), screenshots in light and dark, explorer
timings in `18-development-log.md`. Phase 3: extended selection, copy of
several paths, context menu, "Add to review queue" (TD-029), export of
the folder's contents, the tree follows list navigation, and a treemap
view (F-021) (`ExplorerPhase3Tests`). Cleanup from the review queue is
Phase 6.

### F-015 --- File classification (IMPLEMENTED)

Evidence: `ClassificationTests` (extension, location fallback, unknown,
the ambiguous `ts`, uniqueness of extension lists, location rules);
classification and its reason stored per file and explained in the
inspector; File Categories page with totals per scan. Versioned
(TD-021).

### F-016 --- Large files and folders (IMPLEMENTED)

Done: top 500 files or folders across the latest scans, filtered by
drive, category and minimum size; location labels; drill-down to
Storage Explorer; File Explorer and copy actions (`LargeFilesViewModelTests`).
Phase 3: not-modified-for filter (30 days to 2 years), folder-path
filter, custom minimum size in MB and more presets, multi-select, review
queue, export (`LargeFilesFilterTests`).

### F-018 --- Full scan history (IMPLEMENTED)

Done: every session is kept with its scope, exclusions, reparse policy,
counts, state, explanation and errors; interrupted sessions are
recovered at start-up; Scan History page (`ScanHistoryViewModelTests`,
`SqliteScanStoreTests`). Phase 3: retention setting (TD-030,
`RetentionSettingTests`, `SqliteIndexFeatureTests.Retention…`),
comparison of two scans by category and folder, size-over-time chart,
scan kind, export (`ScanHistoryPhase3Tests`).

### F-017 --- Search index (IMPLEMENTED)

Acceptance criteria: indexed name search over the latest scans; words
match by prefix, case and accents ignored; no user text reaches query
syntax; filters for drive, files or folders, category; results bounded
and largest first; scans without an index are named. Evidence:
`SqliteIndexFeatureTests.Names_are_searched…`, `SearchTextTests`,
`SearchViewModelTests`; 1M-name index built in about 6 s and searched in
47–55 ms (`18-development-log.md`). TD-027.

### F-019 / F-020 --- Incremental scan and USN Journal (IMPLEMENTED)

Acceptance criteria: the full scan stays the baseline; a checkpoint
(journal ID and USN) is taken before listing; an update reads changes
since it as a standard user; reset, wrap, invalid checkpoint, inactive
journal, unsupported file system, changed exclusions or versions, and
too many changes fall back to a full scan with a stated reason; an
interrupted update marks what it didn't check; the result equals a fresh
full scan. Evidence: `IncrementalScanTests` (real NTFS folder: deletes,
renames, moves, appends, new subtree; update inventory equals a fresh
full scan; 40 unchanged folders not listed), `UsnChangeJournalTests`
(checkpoint, changed folders, recreated and invalid checkpoints, too
many changes, V2 and V3 record parsing, malformed records),
`ScanCoordinatorTests` (plan, copy, targets, fallbacks with notes),
`SqliteIndexFeatureTests` (remove, clear, replace, stale). On C: an
update read 2,184 journal records and listed 199 folders again. Before
COMPLETED: the manual journal-reset check in `12-quality-assurance.md`
§3.1 C (needs administrator rights), and a faster update path (TD-025:
the copy and finishing steps still grow with the number of files).

### F-021 --- Storage treemap (IMPLEMENTED)

Acceptance criteria: interactive treemap of a folder, tile area by size
on disk, progressive bounded loading, keyboard access, text on tiles,
the list as the alternative, theme tokens validated in light and dark.
Evidence: `TreemapLayoutTests` (areas proportional, no overlap),
`ExplorerPhase3Tests.The_treemap…`, screenshots in light and dark.
Before COMPLETED: Narrator review (BLK-006). TD-031.

### F-028 --- Hard-link awareness (IMPLEMENTED for totals)

Acceptance criteria: a file with several names counts once in every
folder total, session total and category total; each name's link count
is stored and shown. Evidence: `HardLinkTotalsTests`,
`SqliteIndexFeatureTests.Hard_linked_files…`,
`IncrementalScanTests.Hard_links_found_by_a_real_scan…` (CreateHardLink
on NTFS); on C:, 94,057 extra names and 15.9 GB that were counted twice
before. Duplicate detection (Phase 5) will use the same identities.
TD-026.

### F-035 --- CSV/JSON/HTML/PDF export (IMPLEMENTED)

Acceptance criteria: export of the list a page shows and of a full scan,
saying which; provenance in every format; CSV escaping and formula
neutralization; versioned JSON; HTML that can't run names as markup or
script; readable PDF with provenance. Evidence: `ReportExporterTests`
(formula prefixes, quoting, provenance, JSON schema and types, encoded
`<script>` name and script-blocking policy, row limit, PDF read back with
page break and accented text), `LargeFilesFilterTests` (export writes a
complete file and no partial file remains). TD-028.

### F-039 --- Persistence foundation (IMPLEMENTED)

Acceptance criteria: forward-only checksummed migrations; downgrade and
tamper detection that fails closed; serialized writes; refusal to write
while unavailable; online backup; integrity check on start. Evidence:
`SqliteAppDatabaseTests` (11 tests) and `SqliteUserPreferencesTests` (3).
Moves to COMPLETED after the security review of persistence and backup
(`15-ai-agent-rules.md` §7).

### F-040 --- Logging, redaction and error presentation (IMPLEMENTED)

Acceptance criteria: local rolling logs only; paths redacted by default;
plain-language errors for startup, UI and background failures; a
protected data folder. Evidence: `FileLoggingTests`, `PathRedactorTests`,
`DataDirectorySecurityTests`. Outstanding before COMPLETED:
exception-message redaction (TD-014 known gap).

### F-041 --- Windows 11 / x64 runtime gate (IMPLEMENTED)

Acceptance criteria: on Windows builds before 22000, or in non-x64
processes, the app shows a plain-language message and exits. Evidence:
`PlatformRequirementsTests`. Manual check on a Windows 10 machine still
outstanding.
