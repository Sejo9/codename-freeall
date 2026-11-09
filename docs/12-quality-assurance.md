# FreeAll --- Quality Assurance and Acceptance

## 1. Test strategy

Testing layers: - Unit tests for domain rules and classification. -
Application tests for workflows and state transitions. - Filesystem
integration tests using disposable test volumes/directories. - Native
API tests. - Helper/IPC security tests. - Database migration and
recovery tests. - UI automation and accessibility tests. - Performance
and soak tests. - Installer, upgrade and uninstall tests. -
Compatibility tests for serviced Windows 11 releases.

## 2. Critical safety tests

-   A scan never deletes files.
-   No residue candidate is automatically removed.
-   Duplicate detection never auto-selects deletion.
-   Reparse points are not traversed by default.
-   A file replaced after review is not deleted as though it were the
    reviewed file.
-   Helper rejects arbitrary commands and unauthorized paths.
-   Cross-volume quarantine verifies copy before source removal.
-   Verification failure preserves source.
-   Source removal failure preserves the verified copy and reports no
    reclaim.
-   Recycle Bin failure never silently becomes permanent deletion.
-   Database failure blocks destructive operations.
-   Partial operations report per-item outcomes.

## 3. Scanner test matrix

Test: - Empty directories. - Very deep trees. - Very large
directories. - Long paths. - Unicode and unusual filenames. -
Hidden/system/read-only entries. - Access denied. - Locked files. -
Sparse and compressed files. - Hard links. - Symbolic links, junctions
and mount points. - Disconnected removable drives. - Volume letter
changes. - FAT/exFAT/NTFS and other supported filesystems. - Journal
wrap/reset and fallback. - Cancellation and crash interruption.

### 3.1 Manual volume tests (F-007)

These need a physical PC and a USB drive, so they are run by a person
before a release. Record the result and date in
`17-feature-checklist.md` (F-007).

**A. USB drive unplugged during a scan**

1.  Plug in a USB drive with a few thousand files. Within about a second
    the Drives page shows it without pressing Refresh.
2.  Select **Scan** on that drive. While the progress card shows it
    scanning, unplug the drive.
3.  Expected: the scan stops within a few seconds; the app keeps
    responding; the drive disappears from the Drives page; the last
    result says the scan is partial and that the drive stopped responding
    or was disconnected; Scan History lists the session as Partial with
    "Drive unavailable" errors. Storage Explorer browses what was found.
4.  Plug the drive back in. It reappears on the Drives page by itself,
    and its last scan line still shows the partial scan.

**B. Drive letter changed**

1.  Scan a removable or secondary drive (for example as `F:`).
2.  In Disk Management (needs administrator rights), change its letter
    to `G:`.
3.  Expected: the Drives page updates by itself and shows `G:`; Storage
    Explorer, Large Files and Scan History show the existing results
    under `G:\…` paths (sessions are stored by volume GUID); excluded
    folders on that drive still apply; **Update** on the drive works
    (on NTFS it reads the change journal rather than scanning in full).
4.  Optional: change the letter during a scan. The scan carries on,
    because it opens folders through the volume GUID path.

**C. Incremental update across a journal reset** (NTFS, administrator)

1.  Scan a test drive, then run `fsutil usn deletejournal /d X:` and
    `fsutil usn createjournal m=33554432 a=4194304 X:`.
2.  Select **Update**. Expected: a full scan runs, and its note says the
    change journal was reset so its history is gone.

### 3.2 Manual cleanup and helper tests (F-029, F-033)

These need a person: a UAC prompt can't be answered by automation. Use
disposable files only. Run them on a published build, because other
builds refuse to start the helper. Record the result and date in
`17-feature-checklist.md`.

**A. Administrator approval**

1.  As an administrator, create `C:\ProgramData\FreeAllTest\old.bin`, then
    remove your own user's write access to it.
2.  Scan C:, add the file to the review queue from Storage Explorer, and
    choose **Clean up**, then **Quarantine on the same drive**.
3.  Expected: the item fails with a plain permission message, and
    **Try again with administrator approval** appears.
4.  Choose it and decline the UAC prompt. Expected: "Administrator
    approval was declined", and the file is untouched.
5.  Choose it again and approve. Expected: the file is in
    `C:\FreeAll Quarantine`, Activity shows the helper took part, and
    `FreeAll.Helper.exe` is no longer running a minute later.
6.  Repeat with **Permanently delete**.

**B. Helper integrity**

1.  In a copy of the published folder, change one byte of
    `FreeAll.Helper.dll`.
2.  Repeat step A.4 from that copy. Expected: no UAC prompt, and a message
    that the helper isn't the one this version shipped with.

**C. Recycle Bin limits**

1.  Plug in a USB drive. Scan it, queue a file and choose **Clean up**.
    Expected: nothing is preselected and the Recycle Bin is shown as
    unavailable.
2.  On a test PC, set a drive's Recycle Bin to its smallest size and queue a
    larger file. Choose **Move to Recycle Bin**. Expected: the item fails,
    saying nothing was deleted, and the file is still there.
3.  In the Recycle Bin's properties, choose "Don't move files to the
    Recycle Bin". Expected: it is shown as unavailable for that drive.

**D. Interrupted cleanup**

1.  Quarantine a large folder to another drive, and end `FreeAll.exe` from
    Task Manager during the copy.
2.  Start FreeAll. Expected: Activity shows the operation as Interrupted
    and the item as needing a check; the original is intact.

### 3.3 Manual installer and update tests (F-002, F-036)

Use a test user account or a virtual machine: these use the real install
locations. Record the result and date in `17-feature-checklist.md` and in
`24-compatibility-matrix.md`.

**A. Install, upgrade, uninstall**

1.  As a standard user, run `FreeAll-Setup-<version>-x64.exe`. Expected:
    SmartScreen warns (unsigned, DEC-025); no UAC prompt; the setup asks to
    install for this account only and names both folders.
2.  Open FreeAll from the Start menu. Scan a small folder.
3.  Run the setup of a newer build. Expected: "Update FreeAll a to b?"; the
    scan history is still there afterwards.
4.  While FreeAll is open, run setup again. Expected: "FreeAll is running",
    Retry and Cancel; nothing changed when cancelled.
5.  Run an older setup. Expected: refused, nothing changed.
6.  Uninstall from Settings › Apps. Expected: a confirmation, then "Keep your
    FreeAll data?" with Yes first. Choose Yes: the program folder, shortcut
    and entry are gone; `%LocalAppData%\FreeAll` remains. Reinstall, then
    uninstall choosing No: the data folder is gone.

**B. Update**

Needs a test key and a test address, never the real release key (DEC-031):
a key made with `tools/updates/New-UpdateKey.ps1`, manifests signed with
`New-UpdateManifest.ps1` and served over HTTPS (for example from a fork's
GitHub releases), and a build made with `build.ps1 -UpdateManifestUrl …
-UpdatePublicKey …`:

1.  First start: the update question appears. Choose "Don't check": nothing
    is fetched (check with a network monitor).
2.  In Settings choose weekly, then Check now. Expected: the release is
    offered with its notes; Download, then Install update asks again;
    FreeAll closes, the installer updates it without asking again, and the
    new version starts.
3.  Publish a manifest with a wrong signature, then one with a lower
    sequence. Expected: both refused, nothing downloaded.

## 4. Duplicate tests

-   Same content, same size.
-   Different content, same size.
-   Hash collision simulation at verification boundary.
-   Hard links.
-   Sparse/compressed files.
-   File mutation during hashing.
-   Volume boundary enforcement.
-   Cache invalidation.
-   Byte verification mismatch.

## 5. UI tests

-   Large virtualized data sets.
-   Rapid sorting/filtering.
-   Scan progress update pressure.
-   Cancel/pause states.
-   Empty/loading/partial/error/success states.
-   Light/dark theme.
-   High DPI and text scaling.
-   Keyboard-only navigation.
-   Narrator/screen-reader semantics.
-   Destructive review and confirmation.

## 6. Performance tests

Establish baselines on: - HDD. - SATA SSD. - NVMe SSD. - USB removable
storage. - Small and large file counts. - Large directory fan-out. -
High-latency and error-heavy trees.

Measure: - Scan throughput and CPU. - Peak working set. - Database
growth and write throughput. - UI responsiveness. - Search latency. -
Hashing throughput. - Cancellation latency. - Recovery time.

Targets are set only after representative benchmark data. Do not encode
arbitrary performance promises.

## 7. Acceptance criteria

A feature is accepted only when: - Requirements are traceable. -
Implementation is reviewed. - Unit/integration tests pass. - Security
implications are reviewed. - Accessibility and UX are validated where
applicable. - Error and cancellation paths are tested. - Documentation
is updated. - No unresolved release-blocking defects remain.

## 8. Defect severity

-   Critical: data loss, unsafe deletion, privilege escalation, remote
    code execution or unrecoverable corruption.
-   High: major feature failure, incorrect storage reporting or failed
    recovery.
-   Medium: degraded workflow with workaround.
-   Low: cosmetic or minor usability defect.

Critical and high defects block release.
