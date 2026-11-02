# FreeAll --- Feature Checklist

Use the exact status vocabulary defined in `15-ai-agent-rules.md`.

  -------------------------------------------------------------------------------------------
  ID                Feature                   Status            Acceptance evidence
  ----------------- ------------------------- ----------------- -----------------------------
  F-001             WPF .NET 10 solution      IN PROGRESS       Build and CI
                    foundation                                  

  F-002             Per-user self-contained   NOT STARTED       Install/upgrade/uninstall
                    x64 package                                 tests

  F-003             Windows 11 servicing      NOT STARTED       Tested release/edition matrix
                    compatibility matrix                        

  F-004             ARM64 feasibility and     NOT STARTED       Native dependency and
                    validation                                  end-to-end tests

  F-005             WPF modern design system  IN PROGRESS       Theme and accessibility
                                                                review

  F-006             Dashboard                 NOT STARTED       UI acceptance

  F-007             Drive discovery           IN PROGRESS       Volume integration tests

  F-008             Volume identity and mount IN PROGRESS       Mount-point tests
                    alias handling                              

  F-009             Streaming filesystem      NOT STARTED       Large tree and memory tests
                    scanner                                     

  F-010             Hidden/system/read-only   NOT STARTED       Attribute tests
                    discovery                                   

  F-011             Error and completeness    NOT STARTED       Permission/error tests
                    accounting                                  

  F-012             Reparse-point safe        NOT STARTED       Junction/symlink loop tests
                    handling                                    

  F-013             Directory aggregation     NOT STARTED       Size/count correctness tests

  F-014             Storage explorer          IN PROGRESS       Virtualization tests

  F-015             File classification       NOT STARTED       Classification corpus

  F-016             Large files and folders   NOT STARTED       Query and UX tests

  F-017             Search index              NOT STARTED       Search
                                                                correctness/performance

  F-018             Full scan history         NOT STARTED       Persistence/recovery tests

  F-019             Incremental scan          NOT STARTED       Change tracking tests

  F-020             USN Journal acceleration  NOT STARTED       Journal reset/wrap fallback
                                                                tests

  F-021             Storage treemap           NOT STARTED       Large-data visualization
                                                                tests

  F-022             Application inventory     NOT STARTED       Inventory tests

  F-023             Residue                   NOT STARTED       False-positive review
                    evidence/confidence                         

  F-024             Temporary/cache rules     NOT STARTED       Rule and safety review

  F-025             Duplicate candidate       NOT STARTED       Same-volume scope tests
                    grouping                                    

  F-026             SHA-256 streaming hash    NOT STARTED       Hash correctness tests

  F-027             Byte verification         NOT STARTED       Mismatch/collision-boundary
                                                                tests

  F-028             Hard-link awareness       NOT STARTED       File identity tests

  F-029             Recycle Bin workflow      NOT STARTED       Restore and failure tests

  F-030             Quarantine same-volume    NOT STARTED       Integrity/restore tests

  F-031             Quarantine cross-volume   NOT STARTED       Copy-verify-remove tests

  F-032             Restore/conflict handling NOT STARTED       Conflict tests

  F-033             Elevated helper and       NOT STARTED       Security test suite
                    secure IPC                                  

  F-034             Windows storage           NOT STARTED       Supported API validation
                    integration                                 

  F-035             CSV/JSON/HTML/PDF export  NOT STARTED       Format and injection tests

  F-036             Update opt-in and         NOT STARTED       Update security tests
                    signature verification                      

  F-037             Audit and diagnostics     IN PROGRESS       Audit completeness tests

  F-038             Accessibility and high    IN PROGRESS       Manual accessibility review
                    DPI                                         

  F-039             Persistence foundation    IMPLEMENTED       Migration, recovery and
                    (SQLite, migrations,                        backup tests
                    backup)                                     

  F-040             Logging, redaction and    IMPLEMENTED       Logging and redaction tests
                    error presentation                          

  F-041             Windows 11 / x64 runtime  IMPLEMENTED       Platform gate tests
                    gate                                        
  -------------------------------------------------------------------------------------------

Update status only with evidence. Add feature-specific acceptance
criteria before implementation begins.

## Feature acceptance criteria

### F-001 --- WPF .NET 10 solution foundation

Acceptance criteria:

1.  Solution contains every project in `05-system-architecture.md` §3
    with dependencies matching TD-001.
2.  Dependency, UI-framework, privilege-manifest and x64 rules are
    enforced by automated tests.
3.  `build.ps1 -Configuration Release -Locked -Publish` restores in
    locked mode, builds with zero warnings, runs tests, checks for
    vulnerable packages and publishes self-contained win-x64 output.
4.  The published Desktop app launches as a standard user and closes
    cleanly.
5.  CI runs `build.ps1` on every push and pull request and passes.

Evidence (2026-11-02):

-   Criteria 1--4 verified locally: 0 warnings, 0 errors; 40/40
    architecture tests pass; injected violations (UI framework in
    Scanner, Scanner → Cleanup reference) were detected by 3 failing
    tests; published `FreeAll.exe` opened its window and exited with
    code 0.
-   Criterion 5 pending: the GitHub repository is not yet pushed, so
    CI has not run. Builds are unsigned by decision (DEC-025).

### F-005 --- WPF modern design system (IN PROGRESS)

Acceptance criteria: colour tokens in light, dark and high contrast, with
no hard-coded colours in views; shape, spacing and typography tokens;
the Windows accent colour as the accent source; light/dark coverage of
every screen; documented component styles; accessibility review.

Evidence (2026-11-02): token dictionaries and `Metrics.xaml`/`Controls.xaml`
exist; screenshots of all 12 pages in light and dark were reviewed
(`FreeAll.exe --capture-screens <folder>`, Debug builds only).
Outstanding: high-contrast and Narrator validation and the
decorative-icon automation fix (BLK-006).

### F-007 / F-008 --- Drive discovery, volume identity (IN PROGRESS)

Acceptance criteria: fixed volumes discovered automatically; removable
volumes on demand; network drives excluded and shown as excluded;
volumes keyed by volume GUID; one entry per volume whatever its mount
points; volumes that are not ready handled; drive-letter changes and
disconnects handled across sessions.

Evidence (2026-11-02): `WindowsVolumeDiscovery` integration tests
(system volume, unique IDs, unique mount points, network separation,
cancellation) and `VolumeScanPolicy` unit tests. Outstanding: persisting
volume identity (`Drives` table, Phase 2), device arrival and removal
notifications, and confirmation of TD-012 (BLK-016).

### F-014 --- Storage explorer (IN PROGRESS)

Prototype only (synthetic data). Control choice made (TD-017, BLK-004).
Outstanding: index-backed paging, real metadata, inspector and review
queue.

### F-039 --- Persistence foundation (IMPLEMENTED)

Acceptance criteria: forward-only checksummed migrations; downgrade and
tamper detection that fails closed; serialized writes; refusal to write
while unavailable; online backup; integrity check on start. Evidence:
`SqliteAppDatabaseTests` (11 tests) and `SqliteUserPreferencesTests` (3).
Moves to COMPLETED after the security review of persistence and backup
(`15-ai-agent-rules.md` §7).

### F-040 --- Logging, redaction and error presentation (IMPLEMENTED)

Acceptance criteria: local rolling logs only; paths redacted by default;
plain-language errors for startup, UI and background failures; a
protected data folder. Evidence: `FileLoggingTests`, `PathRedactorTests`,
`DataDirectorySecurityTests`. Outstanding before COMPLETED:
exception-message redaction (TD-014 known gap).

### F-041 --- Windows 11 / x64 runtime gate (IMPLEMENTED)

Acceptance criteria: on Windows builds before 22000, or in non-x64
processes, the app shows a plain-language message and exits. Evidence:
`PlatformRequirementsTests`. Manual check on a Windows 10 machine still
outstanding.
