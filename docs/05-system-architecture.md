# FreeAll --- System Architecture

## 1. Architectural goals

-   Keep UI, application workflows, domain rules, Windows integration
    and persistence separated.
-   Make destructive operations auditable and independently validated.
-   Support bounded-memory scans of very large volumes.
-   Permit x64 first and ARM64 only after validation.
-   Keep the main process unprivileged.
-   Enable independent testing of core policies without launching WPF.

## 2. Logical architecture

``` mermaid
flowchart TB
  UI[FreeAll.Desktop - WPF]
  APP[FreeAll.Application]
  CORE[FreeAll.Core]
  SCAN[FreeAll.Scanner]
  RES[FreeAll.Residue]
  DUP[FreeAll.Duplicates]
  CLEAN[FreeAll.Cleanup]
  INFRA[FreeAll.Infrastructure]
  DB[(SQLite database)]
  HELPER[FreeAll.Helper - elevated on demand]
  WIN[Windows APIs and filesystem]

  UI --> APP
  APP --> CORE
  APP --> SCAN
  APP --> RES
  APP --> DUP
  APP --> CLEAN
  APP --> INFRA
  SCAN --> INFRA
  RES --> INFRA
  DUP --> INFRA
  CLEAN --> INFRA
  INFRA --> DB
  SCAN --> WIN
  INFRA --> WIN
  CLEAN --> HELPER
  HELPER --> WIN
```

## 3. Solution projects

  -----------------------------------------------------------------------
  Project                             Responsibility
  ----------------------------------- -----------------------------------
  `FreeAll.Desktop`                   WPF views, view models, navigation,
                                      theme and UI composition

  `FreeAll.Application`               Use cases, orchestration, DTOs,
                                      interfaces and workflows

  `FreeAll.Core`                      Domain models, enums, validation
                                      and policy rules

  `FreeAll.Scanner`                   Volume discovery, enumeration,
                                      metadata, directory analysis and
                                      incremental scanning

  `FreeAll.Residue`                   Installed-app inventory, path
                                      discovery, evidence and confidence

  `FreeAll.Duplicates`                Candidate grouping, hashing, byte
                                      verification and hard-link
                                      awareness

  `FreeAll.Cleanup`                   Review queue, deletion, Recycle
                                      Bin, quarantine, restore and audit

  `FreeAll.Infrastructure`            SQLite, Windows API adapters,
                                      logging, configuration and update
                                      infrastructure

  `FreeAll.Helper`                    Small privileged executable and
                                      restricted IPC command handlers

  `FreeAll.Tests`                     Unit, integration, filesystem,
                                      security and performance tests
  -----------------------------------------------------------------------

Optional later projects: `FreeAll.Reporting`, `FreeAll.Benchmarks`,
`FreeAll.Installer`.

## 4. Dependency rules

-   `Core` has no dependency on WPF, Windows UI or infrastructure
    packages.
-   `Application` depends on `Core` and abstractions.
-   Feature projects implement application-facing abstractions and
    depend on `Core`.
-   `Infrastructure` implements persistence and OS adapters.
-   `Desktop` composes dependencies and presents application state.
-   `Helper` is a separate process and does not share UI code.
-   No UI project reference is allowed from scanner, cleanup, residue or
    duplicate projects.
-   No arbitrary service locator usage in domain code.

## 5. Process model

### Main process

-   Runs as the signed-in user.
-   Owns UI, scan orchestration, database, ordinary filesystem
    operations and audit.
-   Does not request elevation at startup.
-   Never delegates arbitrary commands to the helper.

### Elevated helper

-   Started only when a specific approved action requires elevation.
-   Uses UAC `runas`.
-   Performs a small allowlist of narrowly scoped operations.
-   Validates every request independently.
-   Exits after work completes or after a bounded idle period.
-   No persistent Windows service.

## 6. Data flow

-   Scanner emits immutable metadata records through bounded channels.
-   Application services batch records into persistence.
-   UI receives progress snapshots and paged query results, not raw
    per-file event floods.
-   Duplicate hashing streams file bytes; it never loads entire files
    into memory.
-   Cleanup receives immutable reviewed targets and revalidates them
    before execution.
-   Audit is persisted transactionally with operation state.

## 7. Deployment architecture

-   Self-contained .NET 10 WPF app.
-   Per-user install under `%LocalAppData%\\Programs\\FreeAll` (final
    installer layout subject to packaging validation).
-   Per-user data under `%LocalAppData%\\FreeAll`.
-   x64 is primary.
-   ARM64 is not officially supported until all native dependencies and
    helper binaries are validated.
-   No x86 build and no Windows 10 support.

## 8. Resilience

-   Scan sessions have explicit states: queued, running, cancelling,
    completed, partial, failed, interrupted.
-   Work is checkpointed in batches.
-   Cancellation stops producers and drains or safely abandons queues
    according to transaction boundaries.
-   Database corruption or write failure blocks cleanup.
-   A removed volume is marked unavailable; its historical records
    remain associated with volume identity.
