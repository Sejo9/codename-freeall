-- FreeAll schema version 4 (Phase 4): application residue and temporary files.

-- Per folder: the newest last-write time of the files directly inside, and the allocated size of those
-- that are documents, pictures, music or videos; Total* are the same over the whole subtree, rolled up
-- when the session completes. Sessions from scanner version 2 and earlier leave them empty.
ALTER TABLE Directories ADD COLUMN NewestFileTicks INTEGER;
ALTER TABLE Directories ADD COLUMN UserContentBytes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Directories ADD COLUMN TotalNewestFileTicks INTEGER;
ALTER TABLE Directories ADD COLUMN TotalUserContentBytes INTEGER NOT NULL DEFAULT 0;

-- Installed applications as FreeAll last saw them (Win32 uninstall entries and Store packages).
-- Rows are kept when an application disappears (IsInstalled = 0), so its leftovers can be recognized
-- later. Source is FreeAll.Core ApplicationSource; AppKey is the uninstall key name or the package
-- family name. Nothing is ever run from these values (the uninstall command is not stored).
CREATE TABLE Applications (
    Id                INTEGER PRIMARY KEY,
    Source            INTEGER NOT NULL,
    AppKey            TEXT    NOT NULL COLLATE NOCASE,
    Name              TEXT    NOT NULL,
    Publisher         TEXT,
    Version           TEXT,
    InstallLocation   TEXT,
    InstallDate       TEXT,
    EstimatedBytes    INTEGER,
    IsSystemComponent INTEGER NOT NULL,
    FirstSeenUtc      TEXT    NOT NULL,
    LastSeenUtc       TEXT    NOT NULL,
    IsInstalled       INTEGER NOT NULL,
    UNIQUE (Source, AppKey)
) STRICT;

-- Keep, ignore and ownership decisions (docs/04-functional-specification.md §5 step 6).
-- Feature: 0 application residue, 1 temporary files. Scope: FreeAll.Core DecisionScope. Target is the
-- volume GUID and volume-relative path ("{guid}|\path") for a path, the group key for an application,
-- or the rule ID for a rule.
CREATE TABLE UserDecisions (
    Id           INTEGER PRIMARY KEY,
    Feature      INTEGER NOT NULL,
    Scope        INTEGER NOT NULL,
    Target       TEXT    NOT NULL COLLATE NOCASE,
    Decision     INTEGER NOT NULL,
    VolumeGuid   TEXT,
    RelativePath TEXT,
    DisplayName  TEXT    NOT NULL,
    OwnerName    TEXT,
    CreatedUtc   TEXT    NOT NULL,
    UNIQUE (Feature, Scope, Target)
) STRICT;
