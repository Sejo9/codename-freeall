# FreeAll --- Blocked Features and Open Decisions

## Current blockers

No product-level decision remains open for the operating system or UI
framework. Implementation is not yet complete.

## Technical validation queue

  ----------------------------------------------------------------------------------------------
  ID             Item           Why it matters        Required evidence           Status
  -------------- -------------- --------------------- --------------------------- --------------
  BLK-001        ARM64 support  Native                Build, install, scan,       BLOCKED
                                API/provider/helper   cleanup and helper tests on pending
                                dependencies may      ARM64 Windows 11            validation
                                differ                                            

  BLK-002        SQLite         Native assets,        Provider comparison and     OPEN
                 provider       packaging,            x64/ARM64 prototype         
                                concurrency and                                   
                                licensing                                         

  BLK-003        Installer      Per-user install,     Install/upgrade/uninstall   OPEN
                 technology     signing, upgrades and prototype                   
                                helper packaging                                  

  BLK-004        WPF data       Large file/directory  Virtualized prototype with  OPEN
                 controls       result performance    representative data         

  BLK-005        Chart and      Rendering large       Prototype, accessibility    OPEN
                 treemap        datasets              and performance review      

  BLK-006        WPF Fluent     Theme completeness    .NET 10 theme audit and     OPEN
                 theme          and stability         fallback styles             

  BLK-007        Windows        Must use supported    API-by-category security    OPEN
                 cleanup APIs   mechanisms for        and compatibility review    
                                protected storage                                 

  BLK-008        Alternate data Scope and duplicate   Product decision before     DEFERRED
                 streams        semantics             implementing ADS inventory  

  BLK-009        Performance    Must be based on real Benchmark baseline and      OPEN
                 targets        hardware              proposed SLOs               
  ----------------------------------------------------------------------------------------------

## Product-owner questions (opened 2026-11-02)

BLK-010 to BLK-014 were resolved by the product owner on 2026-11-02 and
recorded as DEC-022 to DEC-026. The records below are kept for history.

### BLK-015 --- Source-code publication and licence

-   **Status:** OPEN
-   **Affected:** repository licence file, free signing programmes
    (most require an OSI-approved licence)
-   **Question:** DEC-024 makes FreeAll free to use and redistribute.
    Will the source code also be published, and if so under which
    licence (for example MIT)?
-   **Impact:** Does not block development.

### BLK-010 --- Recycle Bin unavailable on removable volumes

-   **Status:** RESOLVED → DEC-022 (recommendation accepted)
-   **Affected features:** F-029, F-030, F-031; PR-015
-   **Question:** Removable volumes often have no Recycle Bin. Silent
    fallback to permanent deletion is prohibited. What is the default
    on such volumes?
-   **Recommendation:** Show that the Recycle Bin is unavailable for
    that volume, preselect nothing, and require the user to choose
    quarantine (central location) or permanent deletion.
-   **Impact:** Destructive-operation semantics, so product-owner
    approval is required.

### BLK-011 --- Elevation for USN Journal and protected-location scanning

-   **Status:** RESOLVED → DEC-023 (elevated scanning in v1)
-   **Affected features:** F-019, F-020, F-033; PR-002, PR-013
-   **Question:** Reading the USN Change Journal requires a volume
    handle that needs administrator rights, so USN-accelerated
    incremental scans would need a UAC prompt. Is an elevated scan of
    protected locations (via helper `EnumerateDirectory`) in v1 scope,
    or is the helper limited to cleanup operations?
-   **Recommendation:** The standard-user full scan is the default;
    USN acceleration and elevated scanning are explicit opt-ins that
    trigger UAC per session.
-   **Impact:** Privilege boundary, so product-owner approval is
    required.

### BLK-012 --- Licensing and distribution model

-   **Status:** RESOLVED → DEC-024 (free to use and redistribute)
-   **Affected features:** F-021, F-035, all third-party dependencies
-   **Question:** Is FreeAll free, open-source or commercial, and under
    what licence? This determines which libraries are permissible,
    e.g. PDF generation and chart/treemap controls with revenue-capped
    or commercial licences.
-   **Impact:** Dependency selection and repository licence.

### BLK-013 --- Code-signing certificate and publisher identity

-   **Status:** RESOLVED → DEC-025 (unsigned builds accepted for now)
-   **Affected features:** F-002, F-033, F-036; Phase 0 signed pipeline
-   **Question:** Is a code-signing certificate available, and what is
    the publisher name?
-   **Current state:** `build.ps1` publishes unsigned builds and reports
    that signing was skipped.

### BLK-014 --- Pause support for scans

-   **Status:** RESOLVED → DEC-026 (cancel only)
-   **Affected features:** F-009, F-011
-   **Question:** Is cancel-only acceptable for v1?

## Resolved documentation conflicts

  ---------------------------------------------------------------------------
  Conflict                                 Resolution
  ---------------------------------------- ----------------------------------
  Architecture diagram arrows vs.          TD-001
  dependency rules                         

  Single `FreeAll.Tests` (doc 05) vs.      TD-002
  per-area test projects (doc 06)          

  README index omitted documents 21--22    Index updated

  Working-document names in doc 15 vs.     TD-007
  numbered files                           
  ---------------------------------------------------------------------------

## Clarification rule

Ask the product owner only when a pending choice materially changes
product behaviour, security, privacy, supported hardware, user
experience or scope. Routine implementation choices should be documented
by the engineering team without repeatedly interrupting the product
owner.

## Blocker record template

``` text
ID:
Title:
Status:
Affected feature:
Question:
Why clarification is required:
Options:
Recommendation:
Security/data/UX impact:
Decision owner:
Date opened:
Date resolved:
Resolution:
Tests/documentation required:
```
