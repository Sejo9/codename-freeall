-- FreeAll schema version 3 (Phase 3): incremental scans, hard-link-aware totals, cloud files,
-- name search and the review queue.
--
-- Columns added to existing tables have defaults, so sessions recorded by schema version 2 keep
-- their meaning: TotalsVersion 1 means hard links were counted once per link (TD-022).

-- Kind: 0 full scan, 1 incremental update built from BaseSessionId (FreeAll.Core ScanKind).
-- JournalId/JournalUsn: the NTFS change journal position taken before listing started, so the
-- next incremental update replays every change made from then on (TD-025). JournalId is the
-- unsigned 64-bit journal ID stored as its two's-complement INTEGER.
-- TotalsVersion: 2 when folder and session totals count each hard-linked file once (TD-026).
-- Linked*: what counting hard links once removed from the per-link sum.
-- Cloud*: cloud files whose contents are not on this PC (BLK-017).
ALTER TABLE ScanSessions ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScanSessions ADD COLUMN BaseSessionId INTEGER;
ALTER TABLE ScanSessions ADD COLUMN JournalId INTEGER;
ALTER TABLE ScanSessions ADD COLUMN JournalUsn INTEGER;
ALTER TABLE ScanSessions ADD COLUMN ChangedDirectoryCount INTEGER;
ALTER TABLE ScanSessions ADD COLUMN TotalsVersion INTEGER NOT NULL DEFAULT 1;
ALTER TABLE ScanSessions ADD COLUMN LinkedFileCount INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScanSessions ADD COLUMN LinkedLogicalBytes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScanSessions ADD COLUMN LinkedAllocatedBytes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScanSessions ADD COLUMN CloudFileCount INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScanSessions ADD COLUMN CloudLogicalBytes INTEGER NOT NULL DEFAULT 0;

-- Hard-link corrections (zero or negative) applied at this folder when totals are rolled up.
ALTER TABLE Directories ADD COLUMN LinkLogicalAdjust INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Directories ADD COLUMN LinkAllocatedAdjust INTEGER NOT NULL DEFAULT 0;

-- Number of names this file has within the session (1 unless it is hard-linked).
ALTER TABLE Files ADD COLUMN LinkCount INTEGER NOT NULL DEFAULT 1;

-- Path lookups (comparison between scans, path filters) and identity lookups (incremental updates).
CREATE INDEX IX_Directories_Path ON Directories (SessionId, RelativePath);
CREATE INDEX IX_Directories_FileId ON Directories (SessionId, FileId);

-- One full-text index of file and folder names per session, created and dropped by the store.
-- TableName is always "SearchIndex_" followed by the session ID; no user input reaches it.
CREATE TABLE SearchIndexes (
    SessionId  INTEGER PRIMARY KEY REFERENCES ScanSessions (Id),
    TableName  TEXT    NOT NULL UNIQUE,
    Tokenizer  TEXT    NOT NULL,
    EntryCount INTEGER NOT NULL,
    CreatedUtc TEXT    NOT NULL
) STRICT;

-- Items a user set aside for review (docs/04-functional-specification.md §4). Adding an item never
-- changes the file; cleanup and its revalidation arrive in Phase 6. Sizes and identity are as found
-- by the scan that the item came from (SessionId).
CREATE TABLE ReviewItems (
    Id             INTEGER PRIMARY KEY,
    VolumeGuid     TEXT    NOT NULL,
    RelativePath   TEXT    NOT NULL COLLATE NOCASE,
    IsFolder       INTEGER NOT NULL,
    LogicalBytes   INTEGER NOT NULL,
    AllocatedBytes INTEGER NOT NULL,
    FileId         BLOB,
    Category       INTEGER,
    ModifiedTicks  INTEGER,
    Origin         INTEGER NOT NULL,
    SessionId      INTEGER NOT NULL,
    AddedUtc       TEXT    NOT NULL,
    UNIQUE (VolumeGuid, RelativePath)
) STRICT;
