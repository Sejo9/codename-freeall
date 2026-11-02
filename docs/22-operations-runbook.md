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

## 6. Incident handling

For suspected data loss or privilege issue: 1. Stop further cleanup
operations. 2. Preserve logs and operation IDs. 3. Do not overwrite
affected storage. 4. Identify whether the issue is app, filesystem,
helper or user-selection related. 5. Reproduce in a disposable test
environment. 6. Record root cause, impact, corrective action and
regression test.
