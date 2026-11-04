-- FreeAll schema version 5 (Phase 5): duplicate detection.

-- Saved hashes (docs/11-data-model.md §5). A row is valid only for the file identity it is keyed by, on
-- the same formatted volume (VolumeSerial, the unsigned value as its two's-complement INTEGER), with the
-- same size, last-write and change times (FILETIME), and the same hash version. Only volumes whose file
-- IDs can be trusted (NTFS, ReFS) get rows. A row never proves two files equal; bytes are always compared.
CREATE TABLE FileHashes (
    VolumeGuid    TEXT    NOT NULL,
    FileId        BLOB    NOT NULL,
    VolumeSerial  INTEGER NOT NULL,
    LogicalSize   INTEGER NOT NULL,
    ModifiedTicks INTEGER NOT NULL,
    ChangeTicks   INTEGER NOT NULL,
    HashVersion   INTEGER NOT NULL,
    Hash          BLOB    NOT NULL,
    LastUsedTicks INTEGER NOT NULL,
    PRIMARY KEY (VolumeGuid, FileId)
) STRICT, WITHOUT ROWID;

CREATE INDEX IX_FileHashes_LastUsed ON FileHashes (VolumeGuid, LastUsedTicks);

-- One analysis of one scan. A run belongs to a single volume, so groups can never span volumes (DEC-013).
-- State is FreeAll.Core DuplicateRunState. The counts say what was and wasn't compared.
CREATE TABLE DuplicateRuns (
    Id                    INTEGER PRIMARY KEY,
    SessionId             INTEGER NOT NULL REFERENCES ScanSessions (Id) ON DELETE CASCADE,
    VolumeGuid            TEXT    NOT NULL,
    State                 INTEGER NOT NULL,
    MinimumSize           INTEGER NOT NULL,
    IncludesSystemFolders INTEGER NOT NULL,
    StartedUtc            TEXT    NOT NULL,
    EndedUtc              TEXT,
    Note                  TEXT,
    CandidateFiles        INTEGER NOT NULL DEFAULT 0,
    LeftOutByLocation     INTEGER NOT NULL DEFAULT 0,
    LeftOutCloud          INTEGER NOT NULL DEFAULT 0,
    LeftOutSpecial        INTEGER NOT NULL DEFAULT 0,
    FilesHashed           INTEGER NOT NULL DEFAULT 0,
    HashedBytes           INTEGER NOT NULL DEFAULT 0,
    CacheHits             INTEGER NOT NULL DEFAULT 0,
    ComparedBytes         INTEGER NOT NULL DEFAULT 0,
    Unreadable            INTEGER NOT NULL DEFAULT 0,
    InUse                 INTEGER NOT NULL DEFAULT 0,
    ChangedSinceScan      INTEGER NOT NULL DEFAULT 0,
    ChangedDuringAnalysis INTEGER NOT NULL DEFAULT 0,
    Redirected            INTEGER NOT NULL DEFAULT 0,
    NotCompared           INTEGER NOT NULL DEFAULT 0,
    Mismatches            INTEGER NOT NULL DEFAULT 0,
    GroupCount            INTEGER NOT NULL DEFAULT 0,
    DuplicateFiles        INTEGER NOT NULL DEFAULT 0,
    ReclaimableBytes      INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE INDEX IX_DuplicateRuns_Volume ON DuplicateRuns (VolumeGuid, Id);
CREATE INDEX IX_DuplicateRuns_Session ON DuplicateRuns (SessionId);

-- Files proved identical byte for byte. CopyCount is distinct files; NameCount includes hard links.
CREATE TABLE DuplicateGroups (
    Id               INTEGER PRIMARY KEY,
    RunId            INTEGER NOT NULL REFERENCES DuplicateRuns (Id) ON DELETE CASCADE,
    Hash             BLOB    NOT NULL,
    HashVersion      INTEGER NOT NULL,
    LogicalSize      INTEGER NOT NULL,
    CopyCount        INTEGER NOT NULL,
    NameCount        INTEGER NOT NULL,
    AllocatedBytes   INTEGER NOT NULL,
    ReclaimableBytes INTEGER NOT NULL,
    Category         INTEGER NOT NULL,
    SamplePath       TEXT    NOT NULL,
    VerifiedUtc      TEXT    NOT NULL
) STRICT;

CREATE INDEX IX_DuplicateGroups_Reclaim ON DuplicateGroups (RunId, ReclaimableBytes DESC, Id);
CREATE INDEX IX_DuplicateGroups_Category ON DuplicateGroups (RunId, Category, ReclaimableBytes DESC, Id);

-- Each name in a group. Rows with the same CopyIndex are hard links of one file. Figures are the file's
-- own when compared, so cleanup can later tell whether it is still what was compared. There is no
-- "keep" or "remove" column on purpose: FreeAll never chooses a copy.
CREATE TABLE DuplicateItems (
    Id            INTEGER PRIMARY KEY,
    GroupId       INTEGER NOT NULL REFERENCES DuplicateGroups (Id) ON DELETE CASCADE,
    CopyIndex     INTEGER NOT NULL,
    RelativePath  TEXT    NOT NULL,
    DirKey        INTEGER NOT NULL,
    FileId        BLOB,
    LogicalSize   INTEGER NOT NULL,
    AllocatedSize INTEGER NOT NULL,
    ModifiedTicks INTEGER,
    ChangeTicks   INTEGER NOT NULL,
    Attributes    INTEGER NOT NULL,
    Category      INTEGER NOT NULL,
    LinkCount     INTEGER NOT NULL,
    NamesSeen     INTEGER NOT NULL
) STRICT;

CREATE INDEX IX_DuplicateItems_Group ON DuplicateItems (GroupId, RelativePath);
