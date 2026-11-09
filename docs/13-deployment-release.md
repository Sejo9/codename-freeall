# FreeAll --- Deployment, Updates and Support

## 1. Runtime and target

-   Target framework: `net10.0-windows`.
-   WPF desktop application.
-   Windows 11 only.
-   x64 primary release.
-   ARM64 only after validation.
-   No x86.
-   Self-contained deployment so the user does not need to install a
    machine-wide .NET runtime.

## 2. Installation

Proposed per-user locations: - Application:
`%LocalAppData%\\Programs\\FreeAll` - Data: `%LocalAppData%\\FreeAll`

Implemented (TD-049): a project-owned per-user installer,
`FreeAll-Setup-<version>-x64.exe`, which installs to
`%LocalAppData%\Programs\FreeAll`, registers under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\FreeAll`, adds a
Start menu shortcut, and keeps `%LocalAppData%\FreeAll` unless the user asks
to delete it when uninstalling. Silent use: `/silent` (install), `/uninstall
/silent` (keeps data), `/uninstall /silent /remove-data`. Exit codes: 0 done,
1 failed, 2 cancelled, 3 refused (older than installed), 4 FreeAll still
running.

The requirements it meets: -
Per-user install without admin rights. - Signed installer and
binaries. - Clean upgrade and rollback behaviour. - No writes to
machine-wide protected locations during ordinary install. - Preserve
user data during upgrade. - Ask before deleting user data on
uninstall. - Register shortcuts and uninstall metadata cleanly. -
Support repair or reinstall without corrupting the database.

## 3. Self-contained deployment

-   Publish runtime-specific assets.
-   x64 is the first supported package.
-   ARM64 package is built only after native dependency validation.
-   Test package size and update delta.
-   Ensure all native libraries match target architecture.
-   Do not bundle unnecessary SDK/development files.
-   Do not assume self-contained means independent of Windows OS APIs or
    prerequisites.

## 4. Elevated helper packaging

-   Separate signed executable.
-   Same release version as main app.
-   Verify publisher signature before launch.
-   Use secure IPC and explicit protocol version.
-   Do not install as a service.
-   Do not request elevation during normal app launch or installation.

## 5. Update policy

Implemented (TD-050). Release builds need `FREEALL_UPDATE_MANIFEST_URL` and
`FREEALL_UPDATE_PUBLIC_KEY` (BLK-021); without them a build can't check and
doesn't ask.

-   Ask user during initial setup whether to check for updates.
-   No telemetry.
-   Official HTTPS update endpoint.
-   Signed manifest and package verification.
-   Show release notes and compatibility information.
-   Require user approval to download/install.
-   Do not silently elevate or install.
-   If update signature fails, reject update and report.
-   Re-check .NET 10 servicing and security status for each release.

## 6. Windows compatibility policy

The matrix itself is `24-compatibility-matrix.md`.

Maintain a matrix with: - Windows 11 edition. - Release/version. -
Microsoft's servicing end date. - FreeAll build tested. - x64/ARM64
status. - Scan/cleanup/helper/installer test status. - Known issues.

Support all Windows 11 releases while they remain within Microsoft's
applicable servicing lifecycle. Newly released versions enter support
only after compatibility testing. Retire releases when servicing ends.
Since servicing dates differ by edition, document the tested editions
and lifecycle basis.

## 7. Release pipeline

1.  Restore pinned dependencies.
2.  Build Release configuration.
3.  Run static analysis and dependency vulnerability checks.
4.  Run unit and integration tests.
5.  Run security tests.
6.  Publish self-contained x64.
7.  Run installer/upgrade/uninstall tests.
8.  Sign binaries and installer.
9.  Verify signatures.
10. Generate SBOM and checksums.
11. Produce release notes and known issues.
12. Publish to official distribution channel.

`build.ps1 -Configuration Release -Locked -Publish` does steps 1 to 7 and 10
(installer smoke test on throwaway locations; checksums and an SPDX SBOM in
`artifacts/release`). Steps 8 and 9 wait for a certificate (DEC-025); steps
11 and 12 are `25-release-notes.md` and the runbook (`22-operations-runbook.md`
§7).

## 8. Backup and restore

-   Database backup uses SQLite-supported backup/checkpoint mechanisms.
-   Quarantine content is managed separately from database backup.
-   Provide export of user settings and exclusions.
-   Document recovery after database corruption.
-   Do not claim a database backup alone protects quarantined content.

## 9. Support lifecycle

-   Track .NET 10 support end date.
-   Plan framework upgrade before end of support.
-   Track Windows 11 servicing changes.
-   Maintain dependency inventory and security advisories.
-   Publish known issues and supported compatibility matrix.
