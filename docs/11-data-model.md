# FreeAll --- Data Model and Persistence

## 1. Persistence goals

-   Local-only metadata storage.
-   Transactional scan and cleanup history.
-   Efficient querying of large inventories.
-   Crash recovery.
-   Schema migrations.
-   No file contents stored.
-   Database remains independent of WPF.

## 2. Database choice

SQLite is the proposed embedded database. Use a .NET 10-compatible
provider after validating x64 and ARM64 native assets, packaging,
licensing, concurrency and maintenance. Initial candidate:
`Microsoft.Data.Sqlite`. High-volume ingestion should use parameterized
SQL and controlled batch writes; an ORM is optional and must be
justified by measured overhead.

## 3. Proposed tables

  Table                  Purpose
  ---------------------- ------------------------------------------------
  `Drives`               Stable volume identity and volume metadata
  `ScanSessions`         Scan scope, state, timestamps and completeness
  `Directories`          Directory inventory and aggregate metadata
  `Files`                File metadata and classification
  `FileIdentities`       Volume/file ID identity mapping
  `FileHashes`           Hash cache and verification state
  `FileCategories`       Versioned category definitions
  `Applications`         Installed application inventory
  `ApplicationPaths`     Known application path relationships
  `ResidueCandidates`    Candidate residue paths and confidence
  `ResidueEvidence`      Evidence supporting residue association
  `DuplicateRuns`        One analysis of one scan of one volume (TD-041)
  `DuplicateGroups`      Exact duplicate groups within one volume
  `DuplicateItems`       File membership and hard-link identity
  `DeletionOperations`   Operation-level cleanup audit
  `DeletionItems`        Per-target result and destination
  `QuarantineItems`      Restore metadata and content verification
  `UserDecisions`        Keep/ignore/ownership/review choices
  `Exclusions`           Volume/path/pattern exclusions
  `ScanErrors`           Error details and scan provenance
  `UserPreferences`      User configuration
  `SchemaMigrations`     Applied schema migration history
  `SearchIndexes`        Per-session name search index registry (TD-027)
  `ReviewItems`          Items set aside in the review queue (TD-029)

## 4. Key design rules

-   Use stable internal IDs.
-   Use volume identity plus file identity where supported.
-   Do not use drive letter as the primary volume key.
-   Keep scan-session provenance on discovered records.
-   Avoid storing a single "safe to delete" boolean.
-   Store classification, evidence, confidence and risk as separate
    concepts.
-   Use UTC timestamps internally; display local time.
-   Use parameterized SQL.
-   Add indexes based on query plans and real workloads.
-   Keep migrations forward-only unless a tested rollback strategy
    exists.

## 5. Hash cache validity

A cached hash may be reused only when the implementation can establish
that the same file identity and unchanged content metadata still apply.
Invalidate on: - File identity change. - Size change. -
Last-write/change metadata change. - Volume identity change. - Hash
algorithm/version change. - Uncertain filesystem state.

## 6. Database operation

-   WAL mode may be used after validating backup and checkpoint
    behaviour.
-   Use one controlled writer or serialized write queue.
-   Batch inserts and updates in bounded transactions.
-   Readers use short-lived read transactions.
-   Do not hold a database transaction open while hashing or copying
    files.
-   Run integrity checks and recover safely after abnormal termination.
-   Backups use SQLite-supported backup/checkpoint methods.

## 7. Retention

-   Scan history retention is user-configurable (Settings → Scan
    history, TD-030). Defaults keep every scan summary and the detailed
    results of the newest scan, plus the newest complete one, per drive.
-   Quarantine content is never auto-purged.
-   User decisions and exclusions should survive ordinary upgrades.
-   Uninstall must ask whether to retain or remove user data.
