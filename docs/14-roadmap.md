# FreeAll --- Roadmap and Delivery Plan

## Phase 0 --- Governance and foundation

-   Confirm product baseline and architecture.
-   Create solution and project boundaries.
-   Establish coding standards, CI and test strategy.
-   Implement decision/checklist/log/blocker governance.
-   Establish signed build pipeline and dependency policy.

**Exit:** clean build, test pipeline, documented architecture and
approved design tokens.

## Phase 1 --- UI and data foundation

-   WPF shell and navigation.
-   MVVM and dependency injection.
-   Light/dark theme.
-   SQLite provider validation and schema migrations.
-   Logging, configuration and error presentation.
-   Drive discovery prototype.
-   Virtualized explorer prototype.

**Exit:** responsive shell, proven data-grid/tree performance, migration
and logging tests.

## Phase 2 --- Scanner and storage explorer

-   Volume identity.
-   Fixed-drive discovery.
-   Streaming enumeration.
-   Metadata and directory aggregation.
-   Error and completeness accounting.
-   Exclusions and cancellation.
-   Explorer, large files and categories.

**Exit:** scanner correctness suite and large-data performance baseline.

## Phase 3 --- Advanced storage analysis

-   Incremental scan foundation.
-   USN Journal support where validated.
-   Search.
-   Treemap and charts.
-   Scan history and exports.

**Exit:** incremental correctness and visualization performance tests.

## Phase 4 --- Application residue and temporary data

-   Installed-app inventory.
-   Registry and MSIX inventory.
-   Known path discovery.
-   Evidence and confidence model.
-   Temporary/cache rules.
-   Review/keep/ignore workflows.

**Exit:** false-positive review, explainability and safety sign-off.

## Phase 5 --- Duplicate detection

-   Size grouping.
-   Streaming SHA-256.
-   Byte verification.
-   Hard-link awareness.
-   Hash cache and invalidation.
-   Per-volume result UX.

**Exit:** correctness and mutation-race tests.

## Phase 6 --- Cleanup, quarantine and helper

-   Review queue.
-   Recycle Bin.
-   Quarantine same-volume.
-   Cross-volume verified copy.
-   Restore and conflict handling.
-   Elevated helper and IPC.
-   Audit.

**Exit:** destructive-operation safety, restore and security tests all
pass.

## Phase 7 --- Windows storage, installer and release hardening

-   Supported Windows cleanup integration.
-   Self-contained x64 installer.
-   Update opt-in.
-   Signature verification.
-   OS compatibility matrix.
-   Accessibility and localization readiness.
-   Performance/soak tests.

**Exit:** release acceptance criteria satisfied.

## After Phase 7 --- first release

Phase 7 is the last build phase. The first release follows when the gates
in `25-release-notes.md` close; most are reviews and manual tests by people
(BLK-006, BLK-018, BLK-019, BLK-020, the compatibility matrix and the
interactive installer run). Two approved items had no phase: Overview
(F-006), built on 2026-11-09 (DEC-033), and elevated scanning (DEC-023),
which waits for BLK-024. Not planned for v1: ARM64 (F-004, listed as
unsupported), alternate data streams (BLK-008), and code signing until a
certificate is available (DEC-025).

## Delivery governance

-   Do not start a phase by silently assuming unresolved product
    behaviour.
-   Technical choices may be made by the team when they do not
    materially alter approved requirements, provided they are
    documented.
-   Changes to deletion semantics, data handling, privilege boundaries,
    supported OS, architecture or feature scope require product-owner
    approval.
