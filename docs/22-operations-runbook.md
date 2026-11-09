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

Releases and updates go through GitHub Releases (DEC-031, TD-054).

### One-time set-up (product owner)

1.  On your own PC, outside the repository:
    `pwsh tools/updates/New-UpdateKey.ps1 -PrivateKeyPath D:\keys\freeall-update.pem`.
    Choose a long password. The last line printed is the public key.
2.  Keep a backup of the `.pem` file and its password somewhere offline (a
    password manager plus a USB drive, for example). Whoever has both can
    publish updates every FreeAll installation will offer.
3.  GitHub › Settings › Secrets and variables › Actions › Variables: add
    `FREEALL_UPDATE_PUBLIC_KEY` with the public key.
4.  GitHub › Settings › Environments: create `release`. Add yourself as a
    required reviewer, tick "Prevent self-review" only if someone else will
    approve, and limit deployment to tags matching `v*`. Add the environment
    secrets `FREEALL_UPDATE_SIGNING_KEY` (the whole text of the `.pem` file)
    and `FREEALL_UPDATE_SIGNING_PASSWORD`.
5.  Builds made after step 3 check
    `https://github.com/<owner>/<repo>/releases/latest/download/manifest.json`
    (set `FREEALL_UPDATE_MANIFEST_URL` as a variable only to use another
    address).

### Each release

1.  Set `<Version>` in `Directory.Build.props`, add the version's section to
    `25-release-notes.md`, and merge to `main` with CI green.
2.  Tag and push: `git tag v<version>` then `git push origin v<version>`.
3.  The Release workflow builds and tests without the key, then waits for
    your approval of the `release` environment. Approve it. It signs, checks
    the signature and the setup file, and publishes the release with the
    setup file, SBOM, checksums and the signed manifest.
4.  The newest release that isn't a pre-release or draft is what apps are
    offered. Mark a release as a pre-release to keep it out of updates.

### Keeping the manifest valid

Manifests expire after 90 days. The "Refresh update manifest" workflow
re-signs the latest one on the 1st of each month and waits for your
approval; it can also be started by hand from the Actions tab. If it is
skipped for three months, apps report that the update information has
expired until it runs.

### Without GitHub Actions

`tools/updates/New-UpdateManifest.ps1 -PrivateKeyPath …` signs on your own
PC; `tools/updates/Test-UpdateManifest.ps1` checks the result. Upload
`manifest.json` and `manifest.json.sig` together to the latest release.
Sequences must always rise; the workflows use the signing time in seconds.

### If the key may be compromised

Delete the environment secrets, stop publishing, make a new key, ship a
release carrying the new public key (signed with the old key while it is
still trusted, if possible), and tell users who can't update to download it
directly.

## 8. Incident handling

For suspected data loss or privilege issue: 1. Stop further cleanup
operations. 2. Preserve logs and operation IDs. 3. Do not overwrite
affected storage. 4. Identify whether the issue is app, filesystem,
helper or user-selection related. 5. Reproduce in a disposable test
environment. 6. Record root cause, impact, corrective action and
regression test.
