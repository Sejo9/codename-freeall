# FreeAll --- Dependency Register

Every third-party package must be recorded here before use, as required
by `06-technical-design.md` §8, and must satisfy the licence rule in
DEC-024 (MIT, Apache-2.0, BSD, MS-PL or public domain). Versions are
pinned centrally in `Directory.Packages.props` (with transitive pinning)
and locked by `packages.lock.json`.

## Runtime dependencies

  ------------------------------------------------------------------------------------------------
  Package                                   Version   Licence      Used by            Purpose
  ----------------------------------------- --------- ------------ ------------------ -------------
  CommunityToolkit.Mvvm                     8.4.2     MIT          Desktop            MVVM source
                                                                                      generators,
                                                                                      commands

  Microsoft.Extensions.DependencyInjection  10.0.14   MIT          Desktop            Composition
                                                                                      root

  Microsoft.Extensions.Logging.Abstractions 10.0.14   MIT          Scanner,           `ILogger`
                                                                   Infrastructure,    abstraction
                                                                   Desktop            

  Microsoft.Data.Sqlite                     10.0.14   MIT          Infrastructure     SQLite
                                                                                      provider
                                                                                      (BLK-002)

  SQLitePCLRaw.bundle_e_sqlite3             2.1.13    Apache-2.0   Infrastructure     Native SQLite
  (transitive)                                                     (transitive)       build
                                                                                      (`e_sqlite3`)

  SQLite (native, inside the bundle)        bundled   Public       Infrastructure     Database
                                                      domain                          engine

  Serilog                                   4.4.0     Apache-2.0   Infrastructure     Structured
                                                                                      logging

  Serilog.Extensions.Logging                10.0.0    Apache-2.0   Infrastructure     Bridges
                                                                                      Serilog to
                                                                                      `ILogger`

  Serilog.Sinks.File                        7.0.0     Apache-2.0   Infrastructure     Rolling local
                                                                                      log files

  PdfPig                                    0.1.16    Apache-2.0   Infrastructure     PDF export
                                                                                      (F-035)
  ------------------------------------------------------------------------------------------------

### Review notes

-   **Maintainers and cadence:** Microsoft packages follow the .NET
    release cadence. CommunityToolkit.Mvvm is a .NET Foundation project.
    Serilog is a mature, actively maintained community project.
    SQLitePCLRaw is maintained by Eric Sink and is the native layer used
    by Microsoft.Data.Sqlite.
-   **Security advisories:** none reported by NuGet audit on
    2026-11-02. `build.ps1` re-checks on every build.
-   **Native dependencies and architectures:** only `e_sqlite3.dll`. The
    bundle ships `win-x64` and `win-arm64` builds. x64 is verified in
    the self-contained publish; ARM64 stays unverified under BLK-001.
-   **Runtime and installer impact:** about 2.3 MB in total, mostly
    `e_sqlite3.dll`.
-   **Replaceability:** all are behind project-owned abstractions
    (`IAppDatabase`, `IUserPreferences`, `ILogger`). Serilog is only
    referenced by `FileLogging`.
-   **PdfPig (Phase 3):** UglyToad/PdfPig, an active open-source project
    (Apache-2.0) used for reading and writing PDFs. It has no package
    dependencies on .NET 8 and later, no native code, and adds about
    5.5 MB (seven managed assemblies, including its document layout
    analysis, which FreeAll doesn't use). FreeAll only uses its document
    builder: reports embed a subset of a Windows font (Segoe UI, or
    Arial), so the PDF stays small. NuGet audit reported no advisories on
    2026-11-03. It is used only by `PdfReportWriter`, behind
    `IReportExporter`, so it can be replaced.
-   **Rejected for PDF export (Phase 3):** PDFsharp 6.5.0 is licensed
    AGPL-3.0-only (copyleft, not allowed by DEC-024) and also pulls in
    System.Security.Cryptography.Pkcs 8.0.1, which has known advisories.
    QuestPDF (community licence, revenue-capped) and iText (AGPL) are also
    not allowed.
-   **Not used:** Microsoft.Extensions.Hosting (heavier than needed for
    a desktop composition root), Entity Framework Core (no measured need
    for an ORM, per `11-data-model.md` §2), System.Data.SQLite (rejected
    by `06-technical-design.md` §1).

## Development and test dependencies

### xunit.v3

  Field                        Value
  ---------------------------- -----------------------------------------------
  Version                      4.0.2
  Used by                      `tests/*` only; never shipped
  Purpose                      Unit, integration and architecture tests
  Alternatives considered      MSTest, NUnit
  Licence                      Apache-2.0
  Maintainer / cadence         xUnit.net (.NET Foundation project); active
  Security advisories          None reported by NuGet audit (2026-11-02)
  Native dependencies          None
  .NET 10 compatibility        Yes; runs on Microsoft.Testing.Platform
  Runtime / installer impact   None
  Required or replaceable      Replaceable
  Notable transitive packages  `xunit.v3.*`, `Microsoft.Testing.Platform` (MIT)

## Review log

  Date         Package                                       Change     Reviewer
  ------------ --------------------------------------------- ---------- --------------------
  2026-11-02   xunit.v3                                      Added      AI agent (Phase 0)
  2026-11-03   PdfPig 0.1.16                                 Added      AI agent (Phase 3)
  2026-11-03   PDFsharp 6.5.0                                Rejected   AI agent (Phase 3):
                                                                        AGPL-3.0-only
  2026-11-02   CommunityToolkit.Mvvm,                        Added      AI agent (Phase 1)
               Microsoft.Extensions.DependencyInjection,                
               Microsoft.Extensions.Logging.Abstractions,               
               Microsoft.Data.Sqlite, Serilog,                          
               Serilog.Extensions.Logging, Serilog.Sinks.File           
