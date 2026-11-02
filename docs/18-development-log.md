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
