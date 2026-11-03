# FreeAll --- Developer Decisions Register

**Status:** Approved baseline plus explicitly identified pending
validations.

## Approved decisions

  -----------------------------------------------------------------------
  ID                      Decision                Status
  ----------------------- ----------------------- -----------------------
  DEC-001                 Windows 11 only;        APPROVED
                          Windows 10 excluded     
                          completely              

  DEC-002                 WPF on modern .NET 10   APPROVED
                          LTS                     

  DEC-003                 Support all currently   APPROVED
                          serviced Windows 11     
                          releases, subject to    
                          compatibility testing   

  DEC-004                 x64 is the primary      APPROVED
                          build target            

  DEC-005                 ARM64 support is        APPROVED
                          conditional on          
                          validation              

  DEC-006                 No x86 build            APPROVED

  DEC-007                 Standard-user main      APPROVED
                          application with        
                          separate on-demand      
                          elevated helper         

  DEC-008                 Per-user installation   APPROVED
                          without admin rights    
                          for ordinary            
                          installation            

  DEC-009                 Self-contained          APPROVED
                          deployment              

  DEC-010                 Recycle Bin is default  APPROVED
                          deletion destination    

  DEC-011                 Optional quarantine;    APPROVED
                          user chooses            
                          same-volume or central  
                          location                

  DEC-012                 Permanent deletion is a APPROVED
                          separate explicitly     
                          confirmed mode          

  DEC-013                 Exact duplicate         APPROVED
                          detection in v1, within 
                          each volume only        

  DEC-014                 Full hash plus byte     APPROVED
                          verification; hard-link 
                          awareness               

  DEC-015                 Default scan all local  APPROVED
                          fixed drives; removable 
                          drives on demand        

  DEC-016                 Network drives excluded APPROVED
                          by default              

  DEC-017                 User-selected scan      APPROVED
                          exclusions              

  DEC-018                 Local processing; no    APPROVED
                          telemetry or metadata   
                          upload by default       

  DEC-019                 Update checks are       APPROVED
                          offered during initial  
                          setup and remain opt-in 

  DEC-020                 New Windows 11 releases APPROVED
                          added after             
                          compatibility testing;  
                          releases retired when   
                          servicing ends          

  DEC-021                 No automatic cleanup or APPROVED
                          residue deletion        

  DEC-022                 Removable volumes       APPROVED
                          without a Recycle Bin:  (2026-11-02,
                          show that it is         BLK-010)
                          unavailable, preselect  
                          nothing, user chooses   
                          quarantine or permanent 
                          deletion                

  DEC-023                 Elevated scanning       APPROVED
                          (protected locations    (2026-11-02,
                          and USN Journal) is in  BLK-011)
                          v1 scope as an explicit 
                          opt-in via the helper   

  DEC-024                 FreeAll is free to      APPROVED
                          download, use and       (2026-11-02,
                          redistribute; only      BLK-012)
                          dependencies with       
                          permissive,             
                          royalty-free licences   
                          are allowed             

  DEC-025                 Unsigned builds are     APPROVED
                          accepted until a        (2026-11-02,
                          code-signing            BLK-013)
                          certificate is          
                          available               

  DEC-026                 Scans support cancel    APPROVED
                          only in v1; no pause    (2026-11-02,
                                                  BLK-014)

  DEC-027                 Source code is public   APPROVED
                          so others can benefit   (2026-11-03,
                          from it; licensed MIT   BLK-015)

  DEC-028                 Fixed volumes without   APPROVED
                          a drive letter or mount (2026-11-03,
                          point (EFI, recovery)   BLK-016)
                          are left out of default
                          scans (TD-012)
  -----------------------------------------------------------------------

### Notes on DEC-022 to DEC-028

-   **DEC-022:** The cleanup review shows the destination as
    unavailable for that volume with a plain-language reason. Neither
    quarantine nor permanent deletion is preselected. Permanent deletion
    keeps its separate warning and confirmation (DEC-012).
-   **DEC-023:** The default scan stays standard-user. The user may opt
    in to an elevated scan, which launches the helper through UAC for
    that session only. Helper commands remain allowlisted
    (`08-security-privacy.md` §4). Results must state which scope was
    scanned elevated.
-   **DEC-024:** Allowed licence families are MIT, Apache-2.0,
    BSD-2/3-Clause, MS-PL and public domain. Not allowed: copyleft
    licences that would constrain redistribution, and
    revenue-capped, "community", commercial or non-commercial-only
    licences. Each dependency is checked against this in
    `23-dependency-register.md`. The source code is published under the
    MIT licence (DEC-027).
-   **DEC-025:** Known consequences: Windows SmartScreen warns on first
    run, and the UAC prompt for the helper shows "Unknown publisher". The
    helper publisher-signature check (`08-security-privacy.md` §4) is
    replaced for now by a SHA-256 check of the helper binary against a
    value embedded in the main app at build time. This detects
    corruption and mismatched versions but does not protect against an
    attacker who can already modify the user's install folder. Update
    manifests (PR-018) will be signed with a project-owned key, which
    needs no certificate. The signing step in `build.ps1` stays in place
    so it can be enabled later.
-   **DEC-026:** Supersedes proposal TD-009.
-   **DEC-027:** The product owner chose public source "so others can
    benefit"; no licence was named. MIT was applied as the most permissive
    licence family already allowed by DEC-024 (`LICENSE`, copyright
    "FreeAll contributors"). The owner can change the licence or the
    copyright holder before the first public release. Open-source status
    also makes free code-signing programmes possible later (DEC-025).
-   **DEC-028:** Confirms TD-012 and refines DEC-015.

## Superseded decisions

  Earlier decision                     Superseded by
  ------------------------------------ ----------------------------------------
  Windows 10 21H2+ and Windows 11      Windows 11 only
  x86 and x64 builds                   x64 primary; ARM64 conditional; no x86
  WPF undecided                        WPF on .NET 10 approved
  WinUI 3 provisional recommendation   Rejected; WPF selected

## Pending technical validations

These are not product-scope decisions unless otherwise noted: - ARM64
native dependency and helper validation. - SQLite provider and native
packaging selection. - Installer technology and signing pipeline. - WPF
data-grid/tree control selection. - Charting library and treemap
rendering approach. - Built-in WPF Fluent theme suitability and fallback
styling. - Exact Windows cleanup APIs for each supported cleanup
category. - Alternate data stream policy beyond the v1 baseline. -
Quantitative performance targets after benchmark phase.

## Technical decisions

Engineering choices made under the delivery-governance rule in
`14-roadmap.md` (they do not alter approved product behaviour). Each can
be revisited through the change procedure below.

### TD-001 --- Dependency inversion between layers (2026-11-02)

`05-system-architecture.md` §2 draws `Application → Scanner` and
`Scanner → Infrastructure`, while §4 says feature projects *implement*
application-facing abstractions. Taking the arrows as compile-time
references would create a cycle. Resolution: the arrows describe runtime
use. Abstractions live in `FreeAll.Application`; feature projects and
`FreeAll.Infrastructure` reference only `Core` and `Application`;
feature projects do not reference each other; `FreeAll.Desktop` is the
composition root; `FreeAll.Helper` references only `Core`. Enforced by
`tests/FreeAll.Architecture.Tests`.

### TD-002 --- Test project layout (2026-11-02)

`05-system-architecture.md` §3 lists a single `FreeAll.Tests`;
`06-technical-design.md` §3 lists per-area test projects. The more
detailed §3 layout of document 06 is followed. Each test project is
created when its target project gains code, so no empty suites exist.
`FreeAll.Architecture.Tests` is added for dependency, privilege-manifest
and x64 rules.

### TD-003 --- Solution file format (2026-11-02)

`FreeAll.slnx` (the XML solution format that is the .NET 10 SDK default)
replaces the suggested `FreeAll.sln`.

### TD-004 --- Test framework (2026-11-02)

xUnit v3 on Microsoft.Testing.Platform, opted in through `global.json`.
See `23-dependency-register.md`.

### TD-005 --- Build baseline (2026-11-02)

Nullable reference types, warnings as errors, `Recommended` .NET
analyzers, code style enforced in build, `PlatformTarget` x64, central
package management, committed `packages.lock.json` files restored in
locked mode by CI, and NuGet audit at `low` severity across transitive
packages. `RuntimeIdentifiers` is `win-x64` for every project so that a
`win-x64` publish does not rewrite lock files and break later locked
restores (found during Phase 0 verification). `build.ps1` is the single build entry point for developers and
CI.

### TD-006 --- Windows 11 enforcement (2026-11-02)

The application manifest cannot distinguish Windows 10 from Windows 11
(they share a `supportedOS` ID), and raising `SupportedOSPlatformVersion`
would require a Windows SDK target framework and its projection
assemblies. Projects therefore target `net10.0-windows`, and a runtime
OS-build check (build 22000 or later) is added to the Desktop startup in
Phase 1.

### TD-007 --- Documentation location (2026-11-02)

The numbered documents in `/docs` are canonical. The working documents
named in `15-ai-agent-rules.md` §3 map to them: `FEATURE_CHECKLIST.md` →
`17-feature-checklist.md`, `AI_AGENT_RULES.md` → `15-ai-agent-rules.md`,
`DEVELOPER_DECISIONS.md` → `16-developer-decisions.md`,
`DEVELOPMENT_LOG.md` → `18-development-log.md`, `BLOCKED_FEATURES.md` →
`19-blocked-features.md`. No uppercase duplicates are kept.

### TD-008 --- CI host (2026-11-02)

The repository is hosted on GitHub and CI uses GitHub Actions
(`.github/workflows/ci.yml`), as confirmed by the product owner. The
workflow only calls `build.ps1`.

### TD-009 --- Scan control: cancel only in v1 (2026-11-02) --- APPROVED as DEC-026

`04-functional-specification.md` §2 mentions "pause/cancel where
supported", but the state model in `05-system-architecture.md` §8 and
`09-scanning-indexing.md` §9 has no `Paused` state. v1 supports
cancellation only; a cancelled or interrupted session is persisted as
`Partial`/`Interrupted` and the user starts a new scan.

### TD-010 --- MVVM and composition (2026-11-02)

CommunityToolkit.Mvvm source generators (`[ObservableProperty]` partial
properties, `[RelayCommand]`) and Microsoft.Extensions.DependencyInjection
with an explicit composition root
(`src/FreeAll.Desktop/Composition/ServiceConfiguration.cs`). The Generic
Host is not used. View models are `internal` and tested through
`InternalsVisibleTo`. Views map to view models through typed
`DataTemplate`s.

### TD-011 --- Theming: WPF Fluent theme plus project tokens (2026-11-02)

Standard controls use the built-in WPF Fluent theme
(`Application.ThemeMode`). Project-owned surfaces use colour tokens from
`Themes/Tokens.{Light|Dark|HighContrast}.xaml`, swapped at runtime by
`ThemeService`. Only `DynamicResource` is used for colours. The accent
token follows the Windows accent colour, as the Fluent controls do. A
Windows contrast theme always wins and uses the classic theme
(`ThemeMode.None`) with system colours. `ThemeMode` is marked
experimental (`WPF0001`, suppressed in the Desktop project); if the API
changes, the fallback is the project-owned styles in `Themes/`. Audit
findings are recorded under BLK-006.

### TD-012 --- Unmounted fixed volumes are on demand (2026-11-02) --- APPROVED as DEC-028

Fixed volumes with no drive letter or folder mount point (typically EFI
system and recovery partitions) are listed on the Drives page but are
not selected for the default scan. The user can still choose them, as
with removable volumes. Reason: they are not reachable through ordinary
paths, normally need elevation, and contain only OS-managed data, so
including them would make every default scan partial. This refines
DEC-015 ("default scan all local fixed drives"). Confirmed by the
product owner on 2026-11-03.

### TD-013 --- SQLite provider and operation (2026-11-02)

`Microsoft.Data.Sqlite` (with `SQLitePCLRaw.bundle_e_sqlite3`) is
selected (BLK-002). Operation:

-   WAL journal mode, enabled after a test showed that the online backup
    API produces an `integrity_check`-clean copy that includes
    uncheckpointed WAL content.
-   `synchronous = FULL` and `foreign_keys = ON` on every connection.
-   Forward-only embedded SQL migrations
    (`Persistence/Migrations/Scripts/NNNN_name.sql`), each in its own
    transaction, recorded with a SHA-256 checksum that ignores line
    endings. Initialization fails closed (`Unavailable`) if a script
    changed after it was applied, if a migration is missing, or if the
    database was created by a newer FreeAll (downgrade protection).
-   `PRAGMA quick_check` runs on every start.
-   All writes go through one serialized gate (`WriteAsync`). Writes are
    refused while the database is unavailable. SQLite errors surface as
    `PersistenceException`.
-   STRICT tables.

### TD-014 --- Logging (2026-11-02)

Serilog rolling files at `%LocalAppData%\FreeAll\logs\freeall-YYYYMMDD.log`.
Files roll daily or at 10 MB, and the newest 14 are kept. The default
level is Information. Paths must pass through `PathRedactor`, which keeps
only a drive-letter root plus a 12-hex-digit SHA-256 fingerprint, unless
the user enables full paths for local troubleshooting (not yet exposed in
the UI). Known gap: exception messages from the OS can contain paths;
exception-message redaction is a follow-up for Phase 2, when scanning
starts producing path-bearing errors.

### TD-015 --- Data folder access list (2026-11-02)

`%LocalAppData%\FreeAll` is created, or corrected on start, with a
protected (non-inherited) access list granting full control to the
current user, SYSTEM and Administrators only. The database, logs and
future quarantine metadata inherit it.

### TD-016 --- Unhandled-exception policy (2026-11-02)

UI-thread exceptions are logged, shown to the user in plain language,
and the app keeps running. Background-task exceptions are logged and
observed. Process-level exceptions are logged and the logs are flushed.
This must be revisited when destructive operations arrive (Phase 6). Those
operations must fail closed and persist their audit state themselves
rather than relying on this handler.

### TD-017 --- Explorer controls (2026-11-02)

The built-in WPF `DataGrid` and `TreeView` with UI virtualization
(recycling mode) are selected for the explorer (BLK-004). No third-party
grid is needed. Rules from the prototype:

-   Sorting runs off the UI thread, and the result replaces the items
    source; the DataGrid's own sort is disabled.
-   Fixed-width columns must set `MinWidth` equal to `Width`.
-   Tree children load lazily on expansion.
-   The production explorer will page from the index (`11-data-model.md`)
    instead of holding all rows in memory (done in Phase 2, TD-023).

### TD-018 --- Scanner traversal and enumeration (2026-11-03)

-   **API:** each directory is opened with `CreateFileW` using
    `FILE_FLAG_BACKUP_SEMANTICS` (required for directory handles; no
    privilege is enabled, so access checks apply) and
    `FILE_FLAG_OPEN_REPARSE_POINT`, then listed in 64 KB chunks with
    `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)`. This returns
    logical size, allocation, four timestamps, attributes, the reparse tag
    and the 128-bit file ID for every entry without opening any file.
    File systems that reject that class (FAT, exFAT) fall back to
    `FileIdBothDirectoryInfo` (64-bit IDs; the reparse tag is in
    `EaSize`). The probe runs once per scan, on the root.
-   **Paths:** directories are opened through the volume GUID path
    (`\\?\Volume{…}\…`), so a drive-letter change during a scan has no
    effect and long paths need no special handling. The index stores
    volume-relative paths (`\` is the root).
-   **Reparse points:** never followed (PR-003). A directory that is a
    reparse point in its parent's listing is recorded as such and not
    opened. A directory that has become one since then is opened as the
    link itself (because of `FILE_FLAG_OPEN_REPARSE_POINT`), detected
    from its handle, and skipped. A handle whose volume serial number
    differs from the root's is refused. Because no link is ever
    followed, and NTFS has no directory hard links, traversal cannot
    loop, so no visited-identity set is kept
    (`09-scanning-indexing.md` §3 "where needed"). A depth limit (1024)
    is kept as a safety net.
-   **Concurrency and memory:** workers (default 4, BLK-009) share a
    last-in-first-out frontier, so traversal is depth-first and the
    frontier holds roughly the siblings along the current path. Each
    worker owns a 64 KB buffer and a batch (default 2,000 records),
    flushed while a large folder is still being listed. Batches go
    through a bounded channel (2 × workers) to a single persistence
    consumer that writes up to 20,000 rows per transaction. Memory is
    bounded by these sizes plus the frontier; it does not grow with
    the number of files.
-   **Cancellation:** stops at the next listing chunk. The folder being
    listed is recorded as `Incomplete`, folders still in the frontier as
    `NotScanned`, and everything already produced is written, so the
    partial session is consistent.

### TD-019 --- Index storage and retention (2026-11-03)

-   Schema version 2 (`0002_scan_index.sql`): `Drives`, `ScanSessions`,
    `Directories`, `Files`, `ScanErrors`, `SessionCategoryTotals`,
    `Exclusions`. `FileIdentities`, hashes and the other tables in
    `11-data-model.md` §3 arrive with the features that need them.
-   Folder and file rows are session-scoped (scan provenance). Folders are
    keyed by `(SessionId, DirKey)`, where the scanner assigns `DirKey`.
    Files reference their folder by key without a foreign key, because a
    folder's own row is written after its listing ends (it carries the
    folder's totals). Consistency is guaranteed by the scanner and
    covered by tests.
-   Recursive totals are computed once, when the session completes,
    bottom-up one depth level per SQL statement. Category totals are
    stored per session.
-   **Retention (provisional, F-018):** for each volume, FreeAll keeps
    the detailed index of the newest finished scan and, if that one is
    not complete, of the newest complete scan as well. Older indexes are
    deleted in bounded transactions. Session summaries and their error
    details are kept. A user-configurable retention setting is still to
    be designed (`11-data-model.md` §7).
-   Error details are capped at 10,000 per session; the counters include
    every error.
-   Sessions left running by a crash are marked `Interrupted` at the next
    start, before any scan can begin, and their detailed rows are
    deleted because they may be inconsistent.

### TD-020 --- Scan completeness rules (2026-11-03)

-   `Completed`: the whole scope was traversed and saved, and no error
    left part of it unread.
-   Not followed reparse points and user exclusions are part of the
    defined scope. They are counted and shown with the scope
    (`04-functional-specification.md` §2 step 3), and they do not make a
    scan partial.
-   Items removed while the scan ran (not found) are recorded but do not
    make a scan partial: nothing that exists was missed.
-   `Partial`: any other error (access denied, sharing violation, depth
    limit and so on), cancellation, or a volume that stopped responding.
    Folder rows count incomplete descendants, so the UI can say which
    totals are lower bounds.
-   `Failed`: the root could not be opened, results could not be saved,
    or the scanner failed unexpectedly.
-   Consequence: a standard-user scan of the Windows drive is normally
    `Partial`, because folders such as `System Volume Information` and
    other users' profiles can't be read without elevation. That is
    reported honestly. Elevated scanning (DEC-023) arrives with the
    helper in Phase 6.

### TD-021 --- File classification v1 (2026-11-03)

Deterministic and versioned (`FileClassifier.Version` = 1). The
lowercase extension decides first, from fixed lists per category.
Otherwise the folder location decides: Windows → System and driver
files; Windows\Temp and AppData\Local\Temp → Temporary files;
ProgramData and AppData → Application data. Anything else is Other /
unknown. The rule that matched is stored with each file, so the UI can
explain it, and the version is stored with each session. `ts` is
deliberately unclassified (video and TypeScript). Location labels
(Windows, Program Files, AppData and so on) are context only and never
decide whether anything can be removed.

### TD-022 --- Size accounting caveats (2026-11-03)

-   Folder and session totals sum every directory entry, so a file with
    several hard links counts once per link (WinSxS is the common case).
    File IDs are stored, and hard-link-aware totals arrive with F-028.
-   Allocated size is the value the file system reports in the directory
    listing. It is smaller than the logical size for sparse, compressed
    and cloud placeholder files, and it may be zero for very small files
    stored inside the MFT.
-   NTFS refreshes the metadata copy in the parent's index lazily, so a
    file that is open for writing can show a stale size or time (seen in
    testing: a folder's last-write time changed in the listing after a
    handle closed).
-   Alternate data streams are not counted (BLK-008).
-   Volume used space comes from Windows and is shown separately from the
    sum of indexed files.

### TD-023 --- Index-backed explorer and analysis pages (2026-11-03)

Storage Explorer, Large Files, File Categories and Scan History read
from the index through `IScanIndex`. Folder listings are sorted in SQL
and loaded 500 rows at a time ("Show more"). Large Files runs one
indexed top-N query per drive (500 results) and merges them. The tree
loads subfolders when expanded; the list, breadcrumbs and tree are not
kept in sync when navigating from the list. The Phase 1 synthetic
prototype was removed. Opening an item in File Explorer passes the
quoted path to `explorer.exe /select,`; Windows file names can't contain
quotes, and a path containing one is refused.

### TD-024 --- Debug capture options (2026-11-03)

Debug builds also accept `--capture-scan <path>` (scan that path before
capturing, and write metrics to `metrics.txt`) and `--data-dir <folder>`
(use another data folder, so review runs don't touch the real database).
Neither exists in Release builds.

## Decision change procedure

Any change must include: - Decision ID. - Current decision. - Proposed
change. - Reason and impact. - Security/data/UX/compatibility
consequences. - Migration and testing impact. - Product-owner approval
where scope or user-visible behaviour changes. - Date and author.
