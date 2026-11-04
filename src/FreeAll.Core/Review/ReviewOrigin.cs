namespace FreeAll.Core.Review;

/// <summary>The page an item was added to the review queue from. Values are persisted; never renumber them.</summary>
public enum ReviewOrigin
{
    StorageExplorer = 0,
    LargeFiles = 1,
    Search = 2,
    Treemap = 3,
    ApplicationResidue = 4,
    TemporaryFiles = 5,
    Duplicates = 6,
}
