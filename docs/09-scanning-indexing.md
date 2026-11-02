# FreeAll --- Scanning and Indexing Design

## 1. Scope

The scanner inventories accessible files and directories on selected
local volumes. It must disclose scope limitations and errors. It is not
a raw disk forensic imager and does not promise attribution of every
allocated cluster.

## 2. Volume discovery

-   Discover local fixed volumes automatically.
-   Removable volumes are on-demand.
-   Network volumes are excluded by default.
-   Use volume GUID identity where available.
-   Record filesystem type, label, mount points, capacity, free space
    and drive type.
-   Deduplicate multiple mount points for the same volume.
-   Handle drive letter changes and removable-drive disconnects.

## 3. Traversal

-   Stream directory entries; do not materialize entire directories.
-   Detect hidden, system, read-only and other attributes.
-   Do not follow reparse points by default.
-   Avoid duplicate mount aliases.
-   Keep an explicit visited identity set where needed to prevent loops.
-   Apply exclusions before descending.
-   Do not bypass permissions.
-   Continue after recoverable access errors and record them.
-   Cancellation must stop new work and persist a partial session.

## 4. Metadata

Capture where available: - Volume identity. - Full path and normalized
path. - File/directory kind. - Name and extension. - Logical length. -
Allocated size where reliably available. - Creation, last-write and
last-access timestamps where meaningful. - Attributes. - File
identity. - Reparse-point tag. - Category and classification
provenance. - Scan session and error state.

Timestamps may be disabled, synthetic, copied or unreliable on some
filesystems. Do not treat them as proof of use or ownership.

## 5. Size accounting

-   Logical size: file's reported length.
-   Allocated size: storage allocation attributed to the file where the
    filesystem/API provides reliable information.
-   Reclaimable estimate: estimated unique allocated storage that could
    be freed by the proposed operation.
-   Volume used space: OS-reported volume usage, not necessarily equal
    to sum of indexed files.
-   Explain slack space, metadata, reserved storage, snapshots, hard
    links, compression, sparse files, alternate streams and inaccessible
    locations as accounting caveats.

## 6. File classification

Use extension, known signatures only when explicitly enabled/needed,
metadata and location rules. Do not open or upload file contents for
classification by default.

Classification must be: - Deterministic. - Versioned. - Explainable. -
Reversible/recomputable. - Separate from deletion safety.

## 7. Incremental scanning

-   Full scan is the correctness baseline.
-   USN Change Journal may accelerate NTFS incremental scans where
    available.
-   Persist journal ID and checkpoint.
-   Detect journal wrap, reset, missing privileges, unsupported
    filesystem or invalid checkpoint.
-   Fall back to full scan when change history cannot be trusted.
-   Do not claim a complete incremental result if journal continuity is
    unknown.
-   Other filesystems need a documented fallback strategy.

## 8. Performance architecture

-   Bounded producer/consumer queues.
-   Configurable worker count.
-   Batched database writes.
-   Limited concurrent hashing.
-   UI progress aggregation and throttling.
-   Memory bounded independently of number of files.
-   Avoid repeatedly recalculating full directory aggregates during
    traversal.
-   Benchmark on HDD, SATA SSD, NVMe, USB and large directory trees.

## 9. Scan states and completeness

States: Queued, Running, Cancelling, Completed, Partial, Failed,
Interrupted.

Record: - Start/end time. - Scope and exclusions. - Volume identity. -
Directories/files discovered. - Bytes logically indexed. - Errors by
category. - Reparse points skipped. - Inaccessible paths. - Journal
status. - Completion classification.

A scan must not be labelled complete if errors or unsupported scope
materially affect completeness.

## 10. Alternate data streams

V1 policy: alternate data streams are not included in ordinary file size
or duplicate-content claims unless a separately approved implementation
is added. The UI and docs must avoid implying that all stream data has
been inventoried. This policy is an explicit open decision for later
review.
