-- FreeAll schema version 1: user preferences.
-- Scan, cleanup and audit tables are added by later migrations with the features that own them.

CREATE TABLE UserPreferences (
    Key        TEXT NOT NULL PRIMARY KEY,
    Value      TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
) STRICT;
