using System.Collections.Frozen;

namespace FreeAll.Core.Classification;

/// <summary>
/// Deterministic, versioned file classification (docs/09-scanning-indexing.md §6). Uses the file name
/// extension first, then the folder location. File contents are never read. Increase
/// <see cref="Version"/> whenever a rule changes so stored results can be recomputed.
/// </summary>
public static class FileClassifier
{
    public const int Version = 1;

    private static readonly FrozenDictionary<string, FileCategory> ByExtension = BuildExtensionMap();

    /// <summary>The extensions assigned to each category, for explaining a classification.</summary>
    public static IReadOnlyList<string> ExtensionsFor(FileCategory category) =>
        ByExtension.Where(p => p.Value == category).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>The lowercase extension without the dot, or an empty string. <c>.gitignore</c> gives <c>gitignore</c>.</summary>
    public static string ExtensionOf(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return string.Empty;
        }

        return fileName[(dot + 1)..].ToString().ToLowerInvariant();
    }

    /// <summary>Classifies a file from its lowercase extension and the location of its folder.</summary>
    public static FileClassification Classify(string extension, StorageLocation folderLocation)
    {
        ArgumentNullException.ThrowIfNull(extension);

        if (extension.Length > 0 && ByExtension.TryGetValue(extension, out var category))
        {
            return new FileClassification(category, ClassificationReason.Extension);
        }

        return folderLocation switch
        {
            StorageLocation.TemporaryFolder => new FileClassification(FileCategory.Temporary, ClassificationReason.Location),
            StorageLocation.Windows => new FileClassification(FileCategory.System, ClassificationReason.Location),
            StorageLocation.ProgramData or StorageLocation.UserAppData => new FileClassification(FileCategory.ApplicationData, ClassificationReason.Location),
            _ => new FileClassification(FileCategory.Other, ClassificationReason.None),
        };
    }

    private static FrozenDictionary<string, FileCategory> BuildExtensionMap()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        void Add(FileCategory category, string extensions)
        {
            foreach (var extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map.Add(extension, category);
            }
        }

        // "ts" is left out on purpose: it is both MPEG transport stream video and TypeScript.
        Add(FileCategory.Images, "jpg jpeg jpe jfif png gif bmp dib tif tiff webp heic heif avif jxl ico svg psd raw cr2 cr3 nef arw orf rw2 dng");
        Add(FileCategory.Videos, "mp4 m4v mkv avi mov wmv flv webm mpg mpeg m2ts mts 3gp vob");
        Add(FileCategory.Audio, "mp3 wav flac aac m4a ogg oga opus wma aif aiff mid midi");
        Add(FileCategory.Documents, "pdf doc docx docm odt rtf txt md epub xps oxps tex");
        Add(FileCategory.Spreadsheets, "xls xlsx xlsm xlsb ods csv tsv numbers");
        Add(FileCategory.Presentations, "ppt pptx pptm pps ppsx odp key");
        Add(FileCategory.Archives, "zip 7z rar tar gz tgz bz2 xz zst lz lzma cab iso img wim esd vhd vhdx vmdk");
        Add(FileCategory.Executables, "exe msi msp msix msixbundle appx appxbundle com scr");
        Add(FileCategory.Development, "c cc cpp cxx h hpp cs csproj vbproj fsproj sln slnx props targets vb fs java class jar kt kts go rs swift py pyc rb php pl lua js mjs cjs jsx tsx vue css scss less sh bat cmd ps1 psm1 vbs pdb obj lib nupkg whl");
        Add(FileCategory.Databases, "db sqlite sqlite3 db-wal db-shm mdb accdb sdf mdf ldf ndf dbf edb");
        Add(FileCategory.System, "sys dll drv inf cat mui ocx cpl efi evtx");
        Add(FileCategory.Temporary, "tmp temp crdownload part");

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
