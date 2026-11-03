namespace FreeAll.Core.Classification;

/// <summary>
/// File categories from docs/03-feature-catalogue.md §E. Values are persisted; never renumber them.
/// A category describes what a file is. It never means the file is safe to remove.
/// </summary>
public enum FileCategory
{
    Other = 0,
    Images = 1,
    Videos = 2,
    Audio = 3,
    Documents = 4,
    Spreadsheets = 5,
    Presentations = 6,
    Archives = 7,
    Executables = 8,
    Development = 9,
    Databases = 10,
    System = 11,
    Temporary = 12,
    ApplicationData = 13,
}

/// <summary>Which rule assigned the category. Values are persisted; never renumber them.</summary>
public enum ClassificationReason
{
    /// <summary>No rule matched.</summary>
    None = 0,

    /// <summary>The file name extension is in the category's list.</summary>
    Extension = 1,

    /// <summary>The extension is not recognized, and the file is in a known location (for example a temporary folder).</summary>
    Location = 2,
}

public readonly record struct FileClassification(FileCategory Category, ClassificationReason Reason);

public static class FileCategories
{
    /// <summary>
    /// Categories that are usually a person's own content (documents, pictures, music, videos). A folder
    /// holding them is never presented as low risk.
    /// </summary>
    public static bool IsUserContent(this FileCategory category) => category is
        FileCategory.Documents or
        FileCategory.Spreadsheets or
        FileCategory.Presentations or
        FileCategory.Images or
        FileCategory.Videos or
        FileCategory.Audio;
}
