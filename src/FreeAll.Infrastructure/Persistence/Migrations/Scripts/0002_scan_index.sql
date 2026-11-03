-- FreeAll schema version 2: drives, scan sessions, the file index, scan errors and exclusions
-- (docs/11-data-model.md, Phase 2).
--
-- Paths are volume-relative ("\" is the volume root), so a drive-letter change does not invalidate
-- the index. Enum columns hold the numeric values defined in FreeAll.Core; those values never change.
-- Inventory timestamps are FILETIME ticks (UTC); session and drive times are ISO 8601 UTC text.
-- Folder and file rows carry their session ID (scan provenance).

CREATE TABLE Drives (
    Id           INTEGER PRIMARY KEY,
    VolumeGuid   TEXT    NOT NULL UNIQUE,
    Label        TEXT,
    FileSystem   TEXT,
    DriveKind    INTEGER NOT NULL,
    MountPoints  TEXT    NOT NULL,  -- newline-separated, as last seen; may be empty
    TotalBytes   INTEGER,
    FreeBytes    INTEGER,
    IsAvailable  INTEGER NOT NULL,
    FirstSeenUtc TEXT    NOT NULL,
    LastSeenUtc  TEXT    NOT NULL
) STRICT;

CREATE TABLE ScanSessions (
    Id                  INTEGER PRIMARY KEY,
    DriveId             INTEGER NOT NULL REFERENCES Drives (Id),
    State               INTEGER NOT NULL,
    RootPath            TEXT    NOT NULL,
    Exclusions          TEXT    NOT NULL,  -- newline-separated volume-relative paths
    ReparsePolicy       TEXT    NOT NULL,
    ScannerVersion      INTEGER NOT NULL,
    ClassifierVersion   INTEGER NOT NULL,
    StartedUtc          TEXT    NOT NULL,
    EndedUtc            TEXT,
    DirectoryCount      INTEGER NOT NULL DEFAULT 0,
    FileCount           INTEGER NOT NULL DEFAULT 0,
    LogicalBytes        INTEGER NOT NULL DEFAULT 0,
    AllocatedBytes      INTEGER NOT NULL DEFAULT 0,
    IssueCount          INTEGER NOT NULL DEFAULT 0,
    MaterialIssueCount  INTEGER NOT NULL DEFAULT 0,
    ReparseSkippedCount INTEGER NOT NULL DEFAULT 0,
    ExcludedCount       INTEGER NOT NULL DEFAULT 0,
    NotScannedCount     INTEGER NOT NULL DEFAULT 0,
    VolumeTotalBytes    INTEGER,
    VolumeFreeBytes     INTEGER,
    Note                TEXT,
    HasInventory        INTEGER NOT NULL DEFAULT 1
) STRICT;

CREATE INDEX IX_ScanSessions_Drive ON ScanSessions (DriveId, Id);

-- One row per folder found, including folders that were skipped, excluded or unreadable.
-- File* and *Bytes are for files directly in the folder; Total* are recursive and are filled in
-- when the session completes. IncompleteCount counts this folder and descendants whose contents
-- are missing, so a non-zero value means the totals are a lower bound.
CREATE TABLE Directories (
    SessionId           INTEGER NOT NULL REFERENCES ScanSessions (Id),
    DirKey              INTEGER NOT NULL,
    ParentKey           INTEGER,
    Depth               INTEGER NOT NULL,
    Name                TEXT    NOT NULL,
    RelativePath        TEXT    NOT NULL,
    State               INTEGER NOT NULL,
    Attributes          INTEGER NOT NULL,
    ReparseTag          INTEGER NOT NULL,
    FileId              BLOB,
    CreatedTicks        INTEGER,
    ModifiedTicks       INTEGER,
    FileCount           INTEGER NOT NULL,
    LogicalBytes        INTEGER NOT NULL,
    AllocatedBytes      INTEGER NOT NULL,
    TotalFileCount      INTEGER NOT NULL DEFAULT 0,
    TotalDirectoryCount INTEGER NOT NULL DEFAULT 0,
    TotalLogicalBytes   INTEGER NOT NULL DEFAULT 0,
    TotalAllocatedBytes INTEGER NOT NULL DEFAULT 0,
    IncompleteCount     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (SessionId, DirKey)
) STRICT, WITHOUT ROWID;

CREATE INDEX IX_Directories_Parent ON Directories (SessionId, ParentKey);
CREATE INDEX IX_Directories_Depth ON Directories (SessionId, Depth);
CREATE INDEX IX_Directories_Size ON Directories (SessionId, TotalAllocatedBytes);

CREATE TABLE Files (
    Id             INTEGER PRIMARY KEY,
    SessionId      INTEGER NOT NULL REFERENCES ScanSessions (Id),
    DirKey         INTEGER NOT NULL,
    Name           TEXT    NOT NULL,
    Extension      TEXT    NOT NULL,
    LogicalSize    INTEGER NOT NULL,
    AllocatedSize  INTEGER NOT NULL,
    CreatedTicks   INTEGER,
    ModifiedTicks  INTEGER,
    AccessedTicks  INTEGER,
    Attributes     INTEGER NOT NULL,
    ReparseTag     INTEGER NOT NULL,
    FileId         BLOB,
    Category       INTEGER NOT NULL,
    CategoryReason INTEGER NOT NULL
) STRICT;

CREATE INDEX IX_Files_Directory ON Files (SessionId, DirKey);
CREATE INDEX IX_Files_Size ON Files (SessionId, LogicalSize);

CREATE TABLE ScanErrors (
    Id           INTEGER PRIMARY KEY,
    SessionId    INTEGER NOT NULL REFERENCES ScanSessions (Id),
    DirKey       INTEGER,
    RelativePath TEXT    NOT NULL,
    Operation    TEXT    NOT NULL,
    ErrorCode    INTEGER NOT NULL,
    Kind         INTEGER NOT NULL,
    OccurredUtc  TEXT    NOT NULL
) STRICT;

CREATE INDEX IX_ScanErrors_Session ON ScanErrors (SessionId, Id);

CREATE TABLE SessionCategoryTotals (
    SessionId      INTEGER NOT NULL REFERENCES ScanSessions (Id),
    Category       INTEGER NOT NULL,
    FileCount      INTEGER NOT NULL,
    LogicalBytes   INTEGER NOT NULL,
    AllocatedBytes INTEGER NOT NULL,
    PRIMARY KEY (SessionId, Category)
) STRICT, WITHOUT ROWID;

CREATE TABLE Exclusions (
    Id           INTEGER PRIMARY KEY,
    VolumeGuid   TEXT NOT NULL,
    RelativePath TEXT NOT NULL COLLATE NOCASE,
    DisplayPath  TEXT NOT NULL,
    CreatedUtc   TEXT NOT NULL,
    UNIQUE (VolumeGuid, RelativePath)
) STRICT;
