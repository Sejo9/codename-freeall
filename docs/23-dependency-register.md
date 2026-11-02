# FreeAll --- Dependency Register

Every third-party package must be recorded here before use, as required
by `06-technical-design.md` §8. Versions are pinned centrally in
`Directory.Packages.props` and locked by `packages.lock.json`.

## Runtime dependencies

None.

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

  Date         Package     Change     Reviewer
  ------------ ----------- ---------- --------------------
  2026-11-02   xunit.v3    Added      AI agent (Phase 0)
