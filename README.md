# FreeAll

Windows 11 storage intelligence and safe-cleanup desktop application (WPF, .NET 10, x64).

**Status:** Phase 0, solution foundation. No product features are implemented yet; see
[docs/17-feature-checklist.md](docs/17-feature-checklist.md).

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
docs/                        Product and engineering baseline
```

## Building

Requires the .NET 10 SDK on Windows 11 x64.

```powershell
./build.ps1                                        # Debug build and tests
./build.ps1 -Configuration Release -Locked -Publish  # What CI runs
```

Publish output goes to `artifacts/publish/win-x64`. Builds are unsigned until a code-signing
certificate is configured.
