# FreeAll --- Operations and Troubleshooting Runbook

## 1. Support bundle

A support bundle may include: - FreeAll version and build
architecture. - Windows edition/release/build. - .NET runtime deployment
mode and app version. - Scan session IDs and aggregate counts. - Error
codes and redacted diagnostic events. - Database integrity result. -
Helper version and signature status.

Do not include full filenames, paths, hashes, application inventories or
file content by default. Ask for separate consent before including
sensitive diagnostics.

## 2. Common scan issues

### Access denied

-   Record path and error.
-   Continue scanning other eligible paths.
-   Explain that totals are incomplete.
-   Do not offer permission bypass.

### Volume disconnected

-   Stop work on that volume.
-   Persist partial session.
-   Mark volume unavailable.
-   Resume only after identity and journal continuity checks.

### Locked file

-   Record sharing violation.
-   Do not forcibly terminate owning processes.
-   Continue where safe.

### Database full or write failure

-   Stop scanner ingestion safely.
-   Block cleanup.
-   Explain required free space or database repair steps.
-   Never discard unpersisted destructive-operation audit state.

## 3. Quarantine recovery

-   Verify quarantine metadata and content hash.
-   Check destination volume availability.
-   Check destination collision.
-   Restore without overwriting unless the user explicitly approves.
-   Record the restore result.
-   If integrity verification fails, do not restore as though valid.

## 4. Database recovery

-   Stop active work.
-   Preserve a copy of the database and associated WAL/SHM state using a
    safe backup procedure.
-   Run SQLite integrity checks on a copy first.
-   Attempt documented recovery only after backup.
-   Never delete quarantine content as part of database repair.

## 5. Update failure

-   Verify HTTPS endpoint and certificate chain.
-   Verify manifest/package signature and publisher.
-   Reject invalid or mismatched packages.
-   Preserve current installation.
-   Provide error code and safe retry instructions.

## 6. Installer problems

-   Exit code 4 or "FreeAll is running": close FreeAll; if the elevated
    helper was used, wait a minute for it to exit (TD-047).
-   "A newer FreeAll is already installed": an older version is refused on
    purpose. To go back, uninstall (keeping data), then install the older
    version; it will refuse to open data a newer version upgraded (TD-013),
    so restore a database backup made by that version if one exists.
-   A crash during installation leaves `FreeAll.new-…` or `FreeAll.old-…`
    beside the program folder; the next setup run removes them. The installed
    version keeps working, because the swap happens last.
-   Uninstalling never removes quarantine. Files the installer didn't put in
    the program folder are left there.

## 7. Publishing an update

1.  Build: `build.ps1 -Configuration Release -Locked -Publish` with
    `FREEALL_UPDATE_MANIFEST_URL` and `FREEALL_UPDATE_PUBLIC_KEY` set.
2.  Upload `FreeAll-Setup-<version>-x64.exe` to its HTTPS address.
3.  On the machine holding the key: `tools/updates/New-UpdateManifest.ps1
    -SetupPath … -Version … -PackageUrl … -Sequence <previous + 1>
    -PrivateKeyPath … -OutputDirectory … -Notes …`.
4.  Upload `manifest.json` and `manifest.json.sig` together to the manifest
    address. Never reuse or lower a sequence number.
5.  Re-sign with a higher sequence before the manifest expires (90 days by
    default), even without a new release.
6.  If the key may be compromised: stop publishing, make a new key, ship a
    release carrying the new public key through the normal installer, and
    tell users who can't update to download it directly.

## 8. Incident handling

For suspected data loss or privilege issue: 1. Stop further cleanup
operations. 2. Preserve logs and operation IDs. 3. Do not overwrite
affected storage. 4. Identify whether the issue is app, filesystem,
helper or user-selection related. 5. Reproduce in a disposable test
environment. 6. Record root cause, impact, corrective action and
regression test.
