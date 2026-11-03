# FreeAll

Windows 11 storage intelligence and safe-cleanup desktop application (WPF, .NET 10, x64).

**Status:** Phase 3 (advanced storage analysis). FreeAll scans local drives (read-only, never
following links; OneDrive and other cloud folders are listed without downloading files), updates them
incrementally from the NTFS change journal, and stores the results locally. Storage Explorer (list or
treemap), Large Files, File Categories, Search and Scan History (with comparison) read those results,
and any list can be exported as CSV, JSON, HTML or PDF. Items can be set aside in a review queue;
cleanup itself is not implemented yet. See [docs/17-feature-checklist.md](docs/17-feature-checklist.md).

FreeAll is free and open source under the [MIT licence](LICENSE).

## Documentation

Start at [docs/README.md](docs/README.md). Contributors, including AI agents, must follow
[docs/15-ai-agent-rules.md](docs/15-ai-agent-rules.md).

## Repository layout

```text
FreeAll.slnx                 Solution
build.ps1                    Build, test, audit and publish (used by CI)
Directory.Build.props        Shared compiler, analyzer and platform settings
Directory.Packages.props     Central package versions
src/
  FreeAll.Desktop            WPF app (composition root), assembly name FreeAll
  FreeAll.Application        Use cases and abstractions
  FreeAll.Core               Domain models and policy rules
  FreeAll.Scanner            Volume discovery and enumeration
  FreeAll.Residue            Application residue analysis
  FreeAll.Duplicates         Per-volume exact duplicate detection
  FreeAll.Cleanup            Review, Recycle Bin, quarantine, restore, audit
  FreeAll.Infrastructure     SQLite, Windows adapters, logging, configuration
  FreeAll.Helper             On-demand elevated helper
tests/
  FreeAll.Architecture.Tests Dependency and privilege rules
  FreeAll.Core.Tests         Domain rules
  FreeAll.Application.Tests  Application services and formatting
  FreeAll.Scanner.Tests      Volume discovery and scanning of temp folder trees
  FreeAll.Integration.Tests  SQLite, access lists, logging and the scan pipeline in temp folders
  FreeAll.Desktop.Tests      View models and theme logic
docs/                        Product and engineering baseline
```

## Building

Requires the .NET 10 SDK on Windows 11 x64.

```powershell
./build.ps1                                        # Debug build and tests
./build.ps1 -Configuration Release -Locked -Publish  # What CI runs
```

Publish output goes to `artifacts/publish/win-x64`. Builds are unsigned (DEC-025).

FreeAll stores its database and logs in `%LocalAppData%\FreeAll`.

### Visual review (Debug builds only)

```powershell
src/FreeAll.Desktop/bin/Debug/net10.0-windows/FreeAll.exe --capture-screens <folder>
```

Renders every page in light and dark themes to PNG files and exits. Add `--capture-scan <path>` to
scan a folder or drive first, followed by an incremental update and a name search (metrics go to
`metrics.txt`), and `--data-dir <folder>` to use a separate data folder instead of
`%LocalAppData%\FreeAll`. Quote a path that contains spaces.
