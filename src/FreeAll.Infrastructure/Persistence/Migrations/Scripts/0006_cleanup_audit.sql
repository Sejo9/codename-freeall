-- FreeAll schema version 6 (Phase 6): cleanup audit and quarantine records.
--
-- Nothing in these tables is ever removed by retention or by FreeAll itself: they are the record of what
-- was done to the user's files, and quarantine rows are the only way back (docs/10-cleanup-quarantine.md).
-- Enum columns hold the numeric values defined in FreeAll.Core; those values never change.

CREATE TABLE DeletionOperations (
    Id             INTEGER PRIMARY KEY,
    OperationGuid  TEXT    NOT NULL,
    Kind           INTEGER NOT NULL,
    Mode           INTEGER,
    State          INTEGER NOT NULL,
    UserName       TEXT    NOT NULL,
    StartedUtc     TEXT    NOT NULL,
    EndedUtc       TEXT,
    PolicyVersion  INTEGER NOT NULL,
    Confirmation   TEXT    NOT NULL,
    EstimatedBytes INTEGER NOT NULL,
    MeasuredBytes  INTEGER,
    Note           TEXT
) STRICT;

CREATE INDEX IX_DeletionOperations_State ON DeletionOperations (State);

-- One row per selected item, written as Pending before anything is touched. State moves to InProgress
-- immediately before the item is touched, so a crash shows exactly which item was being worked on.
CREATE TABLE DeletionItems (
    Id                 INTEGER PRIMARY KEY,
    OperationId        INTEGER NOT NULL REFERENCES DeletionOperations (Id),
    Number             INTEGER NOT NULL,
    VolumeGuid         TEXT    NOT NULL,
    RelativePath       TEXT    NOT NULL,
    DisplayPath        TEXT    NOT NULL,
    IsFolder           INTEGER NOT NULL,
    LogicalBytes       INTEGER NOT NULL,
    AllocatedBytes     INTEGER NOT NULL,
    FileId             BLOB,
    Category           INTEGER,
    ModifiedTicks      INTEGER,
    Origin             INTEGER NOT NULL,
    PolicyLevel        INTEGER NOT NULL,
    PolicyReason       INTEGER NOT NULL,
    State              INTEGER NOT NULL,
    ErrorCode          INTEGER NOT NULL DEFAULT 0,
    Message            TEXT,
    Destination        TEXT,
    NeedsAdministrator INTEGER NOT NULL DEFAULT 0,
    UsedHelper         INTEGER NOT NULL DEFAULT 0,
    UpdatedUtc         TEXT
) STRICT;

CREATE INDEX IX_DeletionItems_Operation ON DeletionItems (OperationId, Number);
CREATE INDEX IX_DeletionItems_State ON DeletionItems (State);

-- What it takes to put an item back. The row is written before the move (State 0), then settled, so a
-- crash can be reconciled against the disk. HeldPath is volume-relative: a changed drive letter doesn't
-- lose anything. SecurityDescriptor is SDDL, kept only when a copy to another drive would lose it.
CREATE TABLE QuarantineItems (
    Id                  INTEGER PRIMARY KEY,
    DeletionItemId      INTEGER NOT NULL REFERENCES DeletionItems (Id),
    OperationGuid       TEXT    NOT NULL,
    OriginalVolumeGuid  TEXT    NOT NULL,
    OriginalPath        TEXT    NOT NULL,
    OriginalDisplayPath TEXT    NOT NULL,
    IsFolder            INTEGER NOT NULL,
    LogicalBytes        INTEGER NOT NULL,
    HeldVolumeGuid      TEXT    NOT NULL,
    HeldPath            TEXT    NOT NULL,
    ContentHash         BLOB    NOT NULL,
    HashVersion         INTEGER NOT NULL,
    FileId              BLOB,
    CreatedTicks        INTEGER NOT NULL,
    ModifiedTicks       INTEGER NOT NULL,
    Attributes          INTEGER NOT NULL,
    SecurityDescriptor  TEXT,
    State               INTEGER NOT NULL,
    QuarantinedUtc      TEXT    NOT NULL,
    RestoredUtc         TEXT,
    RestoredDisplayPath TEXT,
    Note                TEXT
) STRICT;

CREATE INDEX IX_QuarantineItems_State ON QuarantineItems (State, Id);

-- Finding a file's duplicate group, for the rule that another copy must remain (TD-041).
CREATE INDEX IX_DuplicateItems_Path ON DuplicateItems (RelativePath);
