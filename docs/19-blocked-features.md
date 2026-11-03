# FreeAll --- Blocked Features and Open Decisions

## Current blockers

No product question is open. BLK-017 was resolved on 2026-11-03
(DEC-029).

Phase 4's exit criterion needs a person: the product owner's sign-off on
the residue false-positive review (BLK-018, below).

Two manual checks need hands on a physical PC and can't be done by the
development agent: changing a drive letter (needs administrator rights)
and unplugging a USB drive during a scan. The procedure is in
`12-quality-assurance.md` §3.1; the automated parts are covered (see
F-007 in `17-feature-checklist.md`).

## Technical validation queue

  ----------------------------------------------------------------------------------------------
  ID             Item           Why it matters        Required evidence           Status
  -------------- -------------- --------------------- --------------------------- --------------
  BLK-001        ARM64 support  Native                Build, install, scan,       BLOCKED
                                API/provider/helper   cleanup and helper tests on pending
                                dependencies may      ARM64 Windows 11            validation
                                differ                                            

  BLK-002        SQLite         Native assets,        Provider comparison and     RESOLVED x64
                 provider       packaging,            x64/ARM64 prototype         
                                concurrency and                                   
                                licensing                                         

  BLK-003        Installer      Per-user install,     Install/upgrade/uninstall   OPEN
                 technology     signing, upgrades and prototype                   
                                helper packaging                                  

  BLK-004        WPF data       Large file/directory  Virtualized prototype with  RESOLVED
                 controls       result performance    representative data         

  BLK-005        Chart and      Rendering large       Prototype, accessibility    RESOLVED
                 treemap        datasets              and performance review      (TD-031)

  BLK-006        WPF Fluent     Theme completeness    .NET 10 theme audit and     IN PROGRESS
                 theme          and stability         fallback styles             

  BLK-007        Windows        Must use supported    API-by-category security    OPEN
                 cleanup APIs   mechanisms for        and compatibility review    
                                protected storage                                 

  BLK-008        Alternate data Scope and duplicate   Product decision before     DEFERRED
                 streams        semantics             implementing ADS inventory  

  BLK-009        Performance    Must be based on real Benchmark baseline and      IN PROGRESS
                 targets        hardware              proposed SLOs               

  BLK-018        Residue        A wrong "leftover"    Owner sign-off on the       OPEN
                 false-positive could cost data       development-PC review; a    
                 sign-off                             review on another PC        
  ----------------------------------------------------------------------------------------------

## Validation evidence (Phase 4, 2026-11-03)

### BLK-018 --- Residue false-positive sign-off: OPEN

The development agent reviewed every candidate on the development PC
(`18-development-log.md`, Phase 4) and changed the rules where they were
wrong (TD-034, rules version 2). What it can't do is confirm which of
the remaining folders really are leftovers: only the PC's owner knows
whether MobaXterm, Godot or Obsidian were removed or are used as
portable programs. Needed: (1) the owner marks each of the 33 remaining
candidates as right or wrong (the Keep, Ignore and "In use by" buttons
record exactly that), and (2) the same review on a PC that isn't used
for development. Until then F-023 stays IMPLEMENTED.

## Validation evidence (Phase 3, 2026-11-03)

### BLK-005 --- Chart and treemap: RESOLVED (TD-031)

Custom WPF controls, no third-party library (so no licence question
under DEC-024). The treemap draws at most 150 + 24 × 30 tiles, all from
bounded queries, so drawing cost does not depend on folder size; on the
1M-file C: index the first level appears from one indexed query. Every
tile carries text, the list view is the table alternative, and the
colours passed the data-visualization palette checker in light and dark.
Narrator review is still part of BLK-006.

### BLK-009 --- Performance targets: IN PROGRESS (second baseline)

Phase 3 measurements (incremental update, search, finishing step) are in
`18-development-log.md`. Still one machine and one drive; no target set.

## Validation evidence (Phase 2, 2026-11-03)

### BLK-009 --- Performance targets: IN PROGRESS (baseline only)

A first baseline was recorded on the development PC (NVMe system drive,
standard user, Debug build, default 4 workers), in `18-development-log.md`.
It is one machine and one drive, so no target is set yet. HDD, SATA SSD,
USB and large-fan-out runs are still needed.

## Validation evidence (Phase 1, 2026-11-02)

### BLK-002 --- SQLite provider: RESOLVED for x64 (TD-013)

-   `Microsoft.Data.Sqlite` 10.0.14 with `SQLitePCLRaw.bundle_e_sqlite3`
    2.1.13 (MIT / Apache-2.0 / public domain). `e_sqlite3.dll` (1.9 MB)
    is present in the self-contained win-x64 publish, and the published
    app opens and migrates the database.
-   Integration tests cover: migrations on a new database; idempotent
    re-initialization; WAL mode; downgrade protection; detection of an
    edited migration; corrupt-file handling; write refusal while
    unavailable; rollback on a failed write; 50 concurrent serialized
    writes without loss; online backup including WAL content with a clean
    `integrity_check`.
-   ARM64: the bundle contains a `win-arm64` build, but it has not been
    tested. This is tracked under BLK-001.

### BLK-004 --- WPF data controls: RESOLVED (TD-017)

Built-in `DataGrid` and `TreeView` with recycling virtualization,
measured on the development machine (Debug build):

  Rows        Generate   Display after load   Sort by name   Sort by size   Redraw   Working set
  ----------- ---------- -------------------- -------------- -------------- -------- -------------
  10,000      41 ms      79 ms                8 ms           4 ms           ≤260 ms  ~165 MB
  100,000     66 ms      72 ms                108 ms         61 ms          ≤118 ms  ~190 MB
  1,000,000   906 ms     67 ms                1,766 ms       1,136 ms       ≤74 ms   ~360 MB

Display time does not depend on row count, which confirms
virtualization works. Sorting runs on a background thread, so the UI
stays responsive even during the 1.8 s sort of 1M rows. The tree handles
a 5,000-child folder and a 32-level chain with lazy loading. These are
prototype measurements on one machine, not product performance claims
(BLK-009).

### BLK-006 --- WPF Fluent theme: IN PROGRESS (TD-011)

Findings so far:

1.  `Application.ThemeMode` works for light and dark, including runtime
    switching. It is marked experimental (`WPF0001`).
2.  Fluent control styles use the Windows accent colour. Project tokens
    now follow it too, so selection, focus and meters match.
3.  In dark mode the Fluent DataGrid's selected row uses a light accent
    fill with dark text. It is readable, but the visual weight is high;
    acceptable for now.
4.  DataGrid sizing caveat: fixed-width columns shrink to slivers next
    to star columns unless `MinWidth` is set (fixed in the prototype).
5.  High contrast switches to the classic theme with system colours.
    **Not yet validated** with an actual Windows contrast theme or with
    Narrator.
6.  WPF has no equivalent of UWP's `AccessibilityView`. Decorative
    icon glyphs (Segoe Fluent Icons) are exposed to UI Automation as
    text. Follow-up: a small decorative-icon control with an automation
    peer that hides it.

Remaining evidence: manual high-contrast and Narrator pass, and a
review of dialogs, context menus and tooltips as those screens arrive.

## Product-owner questions

### BLK-017 --- Cloud placeholder folders (OneDrive Files On-Demand)

-   **Status:** RESOLVED → DEC-029 (2026-11-03). The product owner chose
    the recommendation: list cloud folders, never follow links, label
    cloud-only files with their real (often zero) space on disk.
-   **Opened:** 2026-11-03
-   **Affected features:** F-009, F-012, F-013; PR-002, PR-003
-   **Question:** With OneDrive Files On-Demand (and other cloud sync
    clients), synced folders are reparse points with a cloud tag. PR-003
    says reparse points are not followed, so FreeAll currently records
    these folders as "not followed" and their contents are missing from
    the index. They are real folders on the same drive, not links. Should
    FreeAll list them?
-   **Why clarification is required:** It changes the traversal rule in
    PR-003 and can cause network activity: listing a folder whose
    contents are only in the cloud makes the sync client fetch that
    folder's file list from the cloud (file contents are never
    downloaded, because FreeAll does not open files).
-   **Options:** (a) keep the current behaviour; (b) list cloud
    placeholder folders, never links (junctions, symbolic links, mount
    points); (c) make (b) a setting, off by default.
-   **Recommendation:** (b). Show cloud-only files with their logical
    size and their real (often zero) space on disk, labelled "cloud
    file", so the totals stay honest.
-   **Security/data/UX impact:** No privilege change. Possible cloud
    requests for folder listings. Today, OneDrive content is invisible in
    sizes, and the scan says so.
-   **Decision owner:** Product owner.

### BLK-015 --- Source-code publication and licence

-   **Status:** RESOLVED → DEC-027 (2026-11-03). Source is public; MIT
    licence applied as the recommended permissive choice.

### BLK-016 --- Default scan selection for unmounted fixed volumes

-   **Status:** RESOLVED → DEC-028 (2026-11-03). TD-012 confirmed.

BLK-010 to BLK-014 were resolved by the product owner on 2026-11-02 and
recorded as DEC-022 to DEC-026. The records below are kept for history.

### BLK-010 --- Recycle Bin unavailable on removable volumes

-   **Status:** RESOLVED → DEC-022 (recommendation accepted)
-   **Affected features:** F-029, F-030, F-031; PR-015
-   **Question:** Removable volumes often have no Recycle Bin. Silent
    fallback to permanent deletion is prohibited. What is the default
    on such volumes?
-   **Recommendation:** Show that the Recycle Bin is unavailable for
    that volume, preselect nothing, and require the user to choose
    quarantine (central location) or permanent deletion.
-   **Impact:** Destructive-operation semantics, so product-owner
    approval is required.

### BLK-011 --- Elevation for USN Journal and protected-location scanning

-   **Status:** RESOLVED → DEC-023 (elevated scanning in v1)
-   **Affected features:** F-019, F-020, F-033; PR-002, PR-013
-   **Question:** Reading the USN Change Journal requires a volume
    handle that needs administrator rights, so USN-accelerated
    incremental scans would need a UAC prompt. Is an elevated scan of
    protected locations (via helper `EnumerateDirectory`) in v1 scope,
    or is the helper limited to cleanup operations?
-   **Recommendation:** The standard-user full scan is the default;
    USN acceleration and elevated scanning are explicit opt-ins that
    trigger UAC per session.
-   **Impact:** Privilege boundary, so product-owner approval is
    required.

### BLK-012 --- Licensing and distribution model

-   **Status:** RESOLVED → DEC-024 (free to use and redistribute)
-   **Affected features:** F-021, F-035, all third-party dependencies
-   **Question:** Is FreeAll free, open-source or commercial, and under
    what licence? This determines which libraries are permissible,
    e.g. PDF generation and chart/treemap controls with revenue-capped
    or commercial licences.
-   **Impact:** Dependency selection and repository licence.

### BLK-013 --- Code-signing certificate and publisher identity

-   **Status:** RESOLVED → DEC-025 (unsigned builds accepted for now)
-   **Affected features:** F-002, F-033, F-036; Phase 0 signed pipeline
-   **Question:** Is a code-signing certificate available, and what is
    the publisher name?
-   **Current state:** `build.ps1` publishes unsigned builds and reports
    that signing was skipped.

### BLK-014 --- Pause support for scans

-   **Status:** RESOLVED → DEC-026 (cancel only)
-   **Affected features:** F-009, F-011
-   **Question:** Is cancel-only acceptable for v1?

## Resolved documentation conflicts

  ---------------------------------------------------------------------------
  Conflict                                 Resolution
  ---------------------------------------- ----------------------------------
  Architecture diagram arrows vs.          TD-001
  dependency rules                         

  Single `FreeAll.Tests` (doc 05) vs.      TD-002
  per-area test projects (doc 06)          

  README index omitted documents 21--22    Index updated

  Working-document names in doc 15 vs.     TD-007
  numbered files                           
  ---------------------------------------------------------------------------

## Clarification rule

Ask the product owner only when a pending choice materially changes
product behaviour, security, privacy, supported hardware, user
experience or scope. Routine implementation choices should be documented
by the engineering team without repeatedly interrupting the product
owner.

## Blocker record template

``` text
ID:
Title:
Status:
Affected feature:
Question:
Why clarification is required:
Options:
Recommendation:
Security/data/UX impact:
Decision owner:
Date opened:
Date resolved:
Resolution:
Tests/documentation required:
```
