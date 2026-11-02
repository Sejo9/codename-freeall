# FreeAll --- Product Requirements Document (PRD)

## 1. Status

Approved product direction. Detailed acceptance criteria are expanded in
the functional, security, scanning and QA documents.

## 2. Platform requirements

  -----------------------------------------------------------------------
  Area                                Requirement
  ----------------------------------- -----------------------------------
  Operating system                    Windows 11 only

  OS servicing policy                 Support Windows 11 releases while
                                      they remain in Microsoft's
                                      applicable servicing lifecycle

  New OS releases                     Add after compatibility testing

  Retired OS releases                 Remove from supported matrix when
                                      servicing ends

  Framework                           .NET 10 LTS

  UI                                  WPF

  Primary architecture                x64

  ARM64                               Conditional; validate native
                                      dependencies, helper, packaging and
                                      performance before claiming support

  x86                                 Not supported

  Windows 10                          Not supported

  Deployment                          Self-contained per-user
                                      installation

  Privilege model                     Standard-user main app; separate
                                      on-demand elevated helper
  -----------------------------------------------------------------------

The .NET 10 lifecycle is maintained by Microsoft. At the documentation
baseline date, Microsoft's published policy lists .NET 10 LTS support
through November 2028. The release team must re-check the official
policy before each release. See [Microsoft .NET support
policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## 3. Core requirements

### PR-001 --- Drive discovery

Discover local fixed volumes automatically. Removable volumes are
available on demand. Network volumes are excluded by default. Identify
volumes by stable volume identity rather than drive letter alone.

### PR-002 --- Comprehensive scan

Inspect files and directories, including hidden, system and read-only
entries, subject to access rights and explicit safety boundaries. Report
inaccessible paths and scan errors.

### PR-003 --- Safe traversal

Do not recurse through reparse points, symbolic links, junctions or
mount-point aliases by default. Prevent loops and duplicate traversal.
Any future opt-in traversal must have explicit scope and cycle
protections.

### PR-004 --- Storage accounting

Report capacity, used and free space at volume level, plus file-level
logical and allocated size where available. Clearly identify unknown,
inaccessible and non-file space.

### PR-005 --- Classification

Classify known file types and storage roles while retaining an
`Other`/`Unknown` category. Classification is informational, not a
deletion decision.

### PR-006 --- Explorer

Provide a directory explorer with recursive sizes, item counts, sorting,
filtering, search, hidden/system indicators, path actions and safe
selection workflows.

### PR-007 --- Large items

Find large files and folders with configurable filters and sorting. Size
alone must never imply that an item is safe to delete.

### PR-008 --- Application residue

Discover likely application leftovers using installed application
inventories, uninstall metadata, MSIX/Store registration and known
application-data locations. Every candidate must show evidence,
ownership rationale and confidence. Never automatically delete residue.

### PR-009 --- Temporary and cache candidates

Identify known temporary and cache locations using documented rules.
Explain the purpose, age/usage evidence, risk and potential
consequences. Avoid blanket cache deletion.

### PR-010 --- Exact duplicates

Provide exact duplicate detection in v1, scoped to each individual
volume. Use size prefiltering, cryptographic hashing and byte-for-byte
verification. Account for hard links. No automatic deletion.

### PR-011 --- Windows storage

Expose safe Windows storage analysis and, where appropriate, supported
Windows cleanup mechanisms. Do not manually remove protected OS
components or servicing-store content.

### PR-012 --- Visualization

Provide an interactive storage treemap and category summaries.

### PR-013 --- Search and history

Provide indexed search, scan history, full and incremental scans, and
supported USN Change Journal acceleration with safe full-scan fallback.

### PR-014 --- Export

Export scan and analysis results to CSV, JSON, HTML and PDF.

### PR-015 --- Deletion

Support Windows Recycle Bin as default. Offer optional quarantine, with
user choice of same-volume or central location. Permanent deletion must
be a separate, clearly labelled, explicitly confirmed action.

### PR-016 --- Restore

Allow restore of quarantined items with path, volume and conflict
handling. Do not auto-purge quarantine.

### PR-017 --- Privacy

All processing local by default. No telemetry or file metadata upload
without separate, explicit, informed consent.

### PR-018 --- Update

Ask about update checks during initial setup. Update checks are
optional, use official HTTPS endpoints, verify signatures and require
user consent before installation.

## 4. UX requirements

-   Modern Windows 11 visual language; polished light and dark themes.
-   Clear information hierarchy and restrained animation.
-   High-DPI support and responsive layouts.
-   Keyboard navigation and screen-reader semantics.
-   Plain-language errors and explanations.
-   No deceptive urgency, misleading cleanup scores or exaggerated
    reclaimable-space claims.
-   Destructive actions use preview, risk explanation, revalidation and
    confirmation.

## 5. Non-functional requirements

-   Scanning must be cancellable and must not freeze the UI.
-   Large directory inventories must be processed with bounded memory.
-   Database writes must be transactional and recoverable.
-   Every destructive operation must be auditable.
-   The app must tolerate removable-drive disconnection, permission
    failures, locked files and interrupted scans.
-   No operation may bypass access controls.
-   User data and scan history must survive application upgrades.
-   Uninstall must ask before removing user data.

## 6. Release acceptance

A release cannot ship unless: - All release-blocking security and
data-loss defects are resolved. - The supported Windows 11 matrix has
passed compatibility tests. - x64 packaging and upgrade/uninstall paths
pass. - ARM64 is either validated and declared supported or explicitly
listed as unsupported. - Cleanup and quarantine recovery tests pass. -
Documentation, release notes and known limitations are complete.
