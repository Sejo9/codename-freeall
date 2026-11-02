# FreeAll --- Technical Design

## 1. Baseline stack

  -----------------------------------------------------------------------
  Area                                Baseline
  ----------------------------------- -----------------------------------
  Language                            C#

  Runtime                             .NET 10 LTS

  UI                                  WPF

  UI pattern                          MVVM

  MVVM toolkit                        CommunityToolkit.Mvvm candidate

  Persistence                         SQLite via a .NET 10-compatible
                                      provider

  Logging                             Structured local logging with
                                      redaction

  IPC                                 Authenticated Windows named pipes

  Hashing                             SHA-256 streaming

  Installer                           Signed per-user Windows installer;
                                      exact packaging technology to be
                                      validated

  Tests                               Unit, integration, security and
                                      performance suites
  -----------------------------------------------------------------------

Do not use `System.Data.SQLite` merely because it appeared in an earlier
.NET Framework proposal. Re-evaluate current .NET 10 provider support,
native asset packaging, architecture support, licensing and update
cadence. `Microsoft.Data.Sqlite` is the initial provider candidate;
benchmark and validate before locking it.

## 2. WPF presentation

-   Use MVVM with view models that expose immutable or observable UI
    state.
-   Use `IAsyncRelayCommand` or equivalent asynchronous commands for
    long-running work.
-   Never block the dispatcher on filesystem, hashing, database or
    helper calls.
-   Use UI virtualization for large lists and trees.
-   Use paging/incremental loading for large search results.
-   Throttle progress updates; aggregate scanner events into snapshots.
-   Use a centralized theme/resource system and design tokens.
-   Evaluate the .NET 10 WPF Fluent theme; do not assume every control
    has a complete modern style. Fill gaps with a project-owned design
    system.
-   Avoid coupling the product to a third-party theme library without
    checking maintenance, accessibility, licensing and .NET 10
    compatibility.

## 3. Suggested solution structure

``` text
FreeAll.sln
/src
  /FreeAll.Desktop
    /Views
    /ViewModels
    /Controls
    /Themes
    /Resources
    /Navigation
  /FreeAll.Application
    /Abstractions
    /UseCases
    /DTOs
    /Policies
  /FreeAll.Core
    /Entities
    /ValueObjects
    /Enums
    /Rules
  /FreeAll.Scanner
    /Volumes
    /Enumeration
    /Metadata
    /DirectoryAnalysis
    /Incremental
  /FreeAll.Residue
    /Inventory
    /Discovery
    /Evidence
    /Scoring
  /FreeAll.Duplicates
    /Candidates
    /Hashing
    /Verification
    /HardLinks
  /FreeAll.Cleanup
    /Review
    /Deletion
    /RecycleBin
    /Quarantine
    /Restore
    /Audit
  /FreeAll.Infrastructure
    /Persistence
    /Windows
    /Logging
    /Configuration
    /Updates
  /FreeAll.Helper
    /IPC
    /Commands
    /Validation
/tests
  /FreeAll.Core.Tests
  /FreeAll.Application.Tests
  /FreeAll.Scanner.Tests
  /FreeAll.Cleanup.Tests
  /FreeAll.Security.Tests
  /FreeAll.Integration.Tests
  /FreeAll.Performance.Tests
/docs
```

## 4. Concurrency and resource control

-   Use bounded `Channel<T>` pipelines.
-   Apply configurable concurrency limits to directory enumeration and
    hashing.
-   Avoid unbounded task creation and per-file UI notifications.
-   Use cancellation tokens through every long-running service.
-   Use sequential or carefully bounded reads for hashing to avoid
    saturating disks.
-   Respect storage type and system load; benchmark SSD, HDD, USB and
    network-like latency cases.
-   Do not claim a universal scan speed.

## 5. Native Windows integration

-   Prefer documented Win32 APIs and supported Windows interfaces.
-   Wrap P/Invoke in small, testable adapters.
-   Validate all native structures, handle lifetimes, error codes and
    architecture assumptions.
-   Use safe handles and deterministic disposal.
-   Never assume drive letters are stable.
-   Treat file identity as volume identity plus file ID where supported.
-   Reparse points are detected and not followed by default.

## 6. Database and repository pattern

-   Use parameterized SQL only.
-   Use explicit schema migrations.
-   Use a single controlled write pipeline or serialized writer.
-   Use WAL where appropriate, with checkpoint policy and backup
    coordination.
-   Keep transactions bounded.
-   Index paths, volume IDs, sizes, timestamps, file IDs, categories and
    scan IDs based on measured query plans.
-   Store no file content.
-   Store hashes only for files eligible for duplicate analysis and only
    with reliable invalidation metadata.

## 7. Error model

Errors should include: - Operation name. - Volume identity. - Path
(redacted in logs where necessary). - Native/managed error code. -
Recoverability classification. - Timestamp. - Scan or cleanup operation
ID.

The UI should present a human-readable summary while retaining technical
detail for diagnostics.

## 8. Third-party dependency policy

Before adding a package, record: - Purpose and alternatives. - Licence
and attribution requirements. - Maintainer activity and release
cadence. - Security advisories. - Native dependencies and architecture
support. - WPF/.NET 10 compatibility. - Runtime and installer impact. -
Whether the dependency is required or replaceable.

No dependency is approved merely because it appears in an example.
