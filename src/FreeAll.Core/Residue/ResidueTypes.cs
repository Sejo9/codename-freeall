namespace FreeAll.Core.Residue;

/// <summary>
/// How strongly the evidence links a folder to an application that is no longer installed
/// (docs/03-feature-catalogue.md §G). Confidence is never a statement that removal is safe.
/// Values are persisted; never renumber them.
/// </summary>
public enum ResidueConfidence
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// What could be lost by removing an item: settings, documents, licences, things other programs need.
/// Separate from confidence (docs/11-data-model.md §4). Unknown is never treated as low.
/// Values are persisted; never renumber them.
/// </summary>
public enum RemovalRisk
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Where an installed application was found. Values are persisted; never renumber them.</summary>
public enum ApplicationSource
{
    /// <summary>Uninstall registry, 64-bit view, all users.</summary>
    Win32Machine = 0,

    /// <summary>Uninstall registry, 32-bit view (WOW6432Node), all users.</summary>
    Win32Machine32 = 1,

    /// <summary>Uninstall registry of the current user.</summary>
    Win32User = 2,

    /// <summary>MSIX / Microsoft Store package registered for the current user.</summary>
    Msix = 3,

    /// <summary>A program folder on the PATH (package managers and developer tools that don't register). Never stored.</summary>
    CommandPath = 4,
}

/// <summary>A user's decision about a candidate. Values are persisted; never renumber them.</summary>
public enum DecisionKind
{
    /// <summary>Keep: never suggest it again; it stays visible under "Kept".</summary>
    Keep = 0,

    /// <summary>Ignore: hide it from the list.</summary>
    Ignore = 1,

    /// <summary>It belongs to a program the user names (still in use).</summary>
    Owned = 2,
}

/// <summary>What a decision applies to. Values are persisted; never renumber them.</summary>
public enum DecisionScope
{
    /// <summary>One folder.</summary>
    Path = 0,

    /// <summary>Every folder attributed to one application or vendor group.</summary>
    Application = 1,

    /// <summary>Every folder a temporary-file rule matches.</summary>
    Rule = 2,
}

/// <summary>The kinds of evidence behind a residue candidate. Values are stable; never renumber them.</summary>
public enum EvidenceKind
{
    /// <summary>Data folder of a Store package that isn't registered for this user.</summary>
    PackageNotInstalled = 0,

    /// <summary>Data folder of an older version of a Store package that is installed in another version.</summary>
    PackageOlderVersion = 1,

    /// <summary>The folder matches an application FreeAll saw installed before, which is no longer installed.</summary>
    FormerlyInstalled = 2,

    /// <summary>No installed application's name, publisher or install folder matches the folder.</summary>
    NoInstalledMatch = 3,

    /// <summary>The vendor still has installed applications, but none matches this product folder.</summary>
    VendorInstalledProductNot = 4,

    /// <summary>Nothing inside has changed for a long time.</summary>
    NotModifiedRecently = 5,

    /// <summary>Something inside changed recently, so a program may still use it.</summary>
    ModifiedRecently = 6,

    /// <summary>It holds documents, pictures, music or videos.</summary>
    ContainsUserContent = 7,

    /// <summary>Location carries settings or data shared by other programs.</summary>
    Location = 8,

    /// <summary>Part of it couldn't be read, so its size and contents are incomplete.</summary>
    IncompleteScan = 9,

    /// <summary>Program files: other software may depend on them even without a registry entry.</summary>
    ProgramFiles = 10,
}

/// <summary>One piece of evidence, in words.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Text">Plain-language explanation.</param>
/// <param name="Supports">True when it points to residue; false when it points away from it (or raises risk).</param>
public sealed record ResidueEvidence(EvidenceKind Kind, string Text, bool Supports);
