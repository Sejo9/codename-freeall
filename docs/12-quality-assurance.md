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
