# FreeAll --- Developer Decisions Register

**Status:** Approved baseline plus explicitly identified pending
validations.

## Approved decisions

  -----------------------------------------------------------------------
  ID                      Decision                Status
  ----------------------- ----------------------- -----------------------
  DEC-001                 Windows 11 only;        APPROVED
                          Windows 10 excluded     
                          completely              

  DEC-002                 WPF on modern .NET 10   APPROVED
                          LTS                     

  DEC-003                 Support all currently   APPROVED
                          serviced Windows 11     
                          releases, subject to    
                          compatibility testing   

  DEC-004                 x64 is the primary      APPROVED
                          build target            

  DEC-005                 ARM64 support is        APPROVED
                          conditional on          
                          validation              

  DEC-006                 No x86 build            APPROVED

  DEC-007                 Standard-user main      APPROVED
                          application with        
                          separate on-demand      
                          elevated helper         

  DEC-008                 Per-user installation   APPROVED
                          without admin rights    
                          for ordinary            
                          installation            

  DEC-009                 Self-contained          APPROVED
                          deployment              

  DEC-010                 Recycle Bin is default  APPROVED
                          deletion destination    

  DEC-011                 Optional quarantine;    APPROVED
                          user chooses            
                          same-volume or central  
                          location                

  DEC-012                 Permanent deletion is a APPROVED
                          separate explicitly     
                          confirmed mode          

  DEC-013                 Exact duplicate         APPROVED
                          detection in v1, within 
                          each volume only        

  DEC-014                 Full hash plus byte     APPROVED
                          verification; hard-link 
                          awareness               

  DEC-015                 Default scan all local  APPROVED
                          fixed drives; removable 
                          drives on demand        

  DEC-016                 Network drives excluded APPROVED
                          by default              

  DEC-017                 User-selected scan      APPROVED
                          exclusions              

  DEC-018                 Local processing; no    APPROVED
                          telemetry or metadata   
                          upload by default       

  DEC-019                 Update checks are       APPROVED
                          offered during initial  
                          setup and remain opt-in 

  DEC-020                 New Windows 11 releases APPROVED
                          added after             
                          compatibility testing;  
                          releases retired when   
                          servicing ends          

  DEC-021                 No automatic cleanup or APPROVED
                          residue deletion        

  DEC-022                 Removable volumes       APPROVED
                          without a Recycle Bin:  (2026-11-02,
                          show that it is         BLK-010)
                          unavailable, preselect  
                          nothing, user chooses   
                          quarantine or permanent 
                          deletion                

  DEC-023                 Elevated scanning       APPROVED
                          (protected locations    (2026-11-02,
                          and USN Journal) is in  BLK-011)
                          v1 scope as an explicit 
                          opt-in via the helper   

  DEC-024                 FreeAll is free to      APPROVED
                          download, use and       (2026-11-02,
                          redistribute; only      BLK-012)
                          dependencies with       
                          permissive,             
                          royalty-free licences   
                          are allowed             

  DEC-025                 Unsigned builds are     APPROVED
                          accepted until a        (2026-11-02,
                          code-signing            BLK-013)
                          certificate is          
                          available               

  DEC-026                 Scans support cancel    APPROVED
                          only in v1; no pause    (2026-11-02,
                                                  BLK-014)
  -----------------------------------------------------------------------

### Notes on DEC-022 to DEC-026

-   **DEC-022:** The cleanup review shows the destination as
    unavailable for that volume with a plain-language reason. Neither
    quarantine nor permanent deletion is preselected. Permanent deletion
    keeps its separate warning and confirmation (DEC-012).
-   **DEC-023:** The default scan stays standard-user. The user may opt
    in to an elevated scan, which launches the helper through UAC for
    that session only. Helper commands remain allowlisted
    (`08-security-privacy.md` §4). Results must state which scope was
    scanned elevated.
-   **DEC-024:** Allowed licence families are MIT, Apache-2.0,
    BSD-2/3-Clause, MS-PL and public domain. Not allowed: copyleft
    licences that would constrain redistribution, and
    revenue-capped, "community", commercial or non-commercial-only
    licences. Each dependency is checked against this in
    `23-dependency-register.md`. Whether the *source code* is published
    and under which licence is still open (BLK-015).
-   **DEC-025:** Known consequences: Windows SmartScreen warns on first
    run, and the UAC prompt for the helper shows "Unknown publisher". The
    helper publisher-signature check (`08-security-privacy.md` §4) is
    replaced for now by a SHA-256 check of the helper binary against a
    value embedded in the main app at build time. This detects
    corruption and mismatched versions but does not protect against an
    attacker who can already modify the user's install folder. Update
    manifests (PR-018) will be signed with a project-owned key, which
    needs no certificate. The signing step in `build.ps1` stays in place
    so it can be enabled later.
-   **DEC-026:** Supersedes proposal TD-009.

## Superseded decisions

  Earlier decision                     Superseded by
  ------------------------------------ ----------------------------------------
  Windows 10 21H2+ and Windows 11      Windows 11 only
  x86 and x64 builds                   x64 primary; ARM64 conditional; no x86
  WPF undecided                        WPF on .NET 10 approved
  WinUI 3 provisional recommendation   Rejected; WPF selected

## Pending technical validations

These are not product-scope decisions unless otherwise noted: - ARM64
native dependency and helper validation. - SQLite provider and native
packaging selection. - Installer technology and signing pipeline. - WPF
data-grid/tree control selection. - Charting library and treemap
rendering approach. - Built-in WPF Fluent theme suitability and fallback
styling. - Exact Windows cleanup APIs for each supported cleanup
category. - Alternate data stream policy beyond the v1 baseline. -
Quantitative performance targets after benchmark phase.

## Technical decisions

Engineering choices made under the delivery-governance rule in
`14-roadmap.md` (they do not alter approved product behaviour). Each can
be revisited through the change procedure below.

### TD-001 --- Dependency inversion between layers (2026-11-02)

`05-system-architecture.md` §2 draws `Application → Scanner` and
`Scanner → Infrastructure`, while §4 says feature projects *implement*
application-facing abstractions. Taking the arrows as compile-time
references would create a cycle. Resolution: the arrows describe runtime
use. Abstractions live in `FreeAll.Application`; feature projects and
`FreeAll.Infrastructure` reference only `Core` and `Application`;
feature projects do not reference each other; `FreeAll.Desktop` is the
composition root; `FreeAll.Helper` references only `Core`. Enforced by
`tests/FreeAll.Architecture.Tests`.

### TD-002 --- Test project layout (2026-11-02)

`05-system-architecture.md` §3 lists a single `FreeAll.Tests`;
`06-technical-design.md` §3 lists per-area test projects. The more
detailed §3 layout of document 06 is followed. Each test project is
created when its target project gains code, so no empty suites exist.
`FreeAll.Architecture.Tests` is added for dependency, privilege-manifest
and x64 rules.

### TD-003 --- Solution file format (2026-11-02)

`FreeAll.slnx` (the XML solution format that is the .NET 10 SDK default)
replaces the suggested `FreeAll.sln`.

### TD-004 --- Test framework (2026-11-02)

xUnit v3 on Microsoft.Testing.Platform, opted in through `global.json`.
See `23-dependency-register.md`.

### TD-005 --- Build baseline (2026-11-02)

Nullable reference types, warnings as errors, `Recommended` .NET
analyzers, code style enforced in build, `PlatformTarget` x64, central
package management, committed `packages.lock.json` files restored in
locked mode by CI, and NuGet audit at `low` severity across transitive
packages. `RuntimeIdentifiers` is `win-x64` for every project so that a
`win-x64` publish does not rewrite lock files and break later locked
restores (found during Phase 0 verification). `build.ps1` is the single build entry point for developers and
CI.

### TD-006 --- Windows 11 enforcement (2026-11-02)

The application manifest cannot distinguish Windows 10 from Windows 11
(they share a `supportedOS` ID), and raising `SupportedOSPlatformVersion`
would require a Windows SDK target framework and its projection
assemblies. Projects therefore target `net10.0-windows`, and a runtime
OS-build check (build 22000 or later) is added to the Desktop startup in
Phase 1.

### TD-007 --- Documentation location (2026-11-02)

The numbered documents in `/docs` are canonical. The working documents
named in `15-ai-agent-rules.md` §3 map to them: `FEATURE_CHECKLIST.md` →
`17-feature-checklist.md`, `AI_AGENT_RULES.md` → `15-ai-agent-rules.md`,
`DEVELOPER_DECISIONS.md` → `16-developer-decisions.md`,
`DEVELOPMENT_LOG.md` → `18-development-log.md`, `BLOCKED_FEATURES.md` →
`19-blocked-features.md`. No uppercase duplicates are kept.

### TD-008 --- CI host (2026-11-02)

The repository is hosted on GitHub and CI uses GitHub Actions
(`.github/workflows/ci.yml`), as confirmed by the product owner. The
workflow only calls `build.ps1`.

### TD-009 --- Scan control: cancel only in v1 (2026-11-02) --- APPROVED as DEC-026

`04-functional-specification.md` §2 mentions "pause/cancel where
supported", but the state model in `05-system-architecture.md` §8 and
`09-scanning-indexing.md` §9 has no `Paused` state. v1 supports
cancellation only; a cancelled or interrupted session is persisted as
`Partial`/`Interrupted` and the user starts a new scan.

## Decision change procedure

Any change must include: - Decision ID. - Current decision. - Proposed
change. - Reason and impact. - Security/data/UX/compatibility
consequences. - Migration and testing impact. - Product-owner approval
where scope or user-visible behaviour changes. - Date and author.
