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
