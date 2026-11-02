using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeAll.Desktop.ViewModels.Explorer;

/// <summary>
/// Deterministic synthetic folders and files for the explorer prototype (BLK-004). Nothing here
/// touches the real file system; the production explorer queries the scan index instead.
/// </summary>
internal static class SyntheticStorage
{
    public const int ClusterSize = 4096;

    private static readonly string[] FolderWords =
    [
        "Archive", "Assets", "Backup", "Build", "Cache", "Config", "Data", "Downloads", "Exports", "Images",
        "Logs", "Media", "Packages", "Projects", "Reports", "Resources", "Samples", "Source", "Temp", "Tools",
    ];

    private static readonly string[] FileWords =
    [
        "report", "photo", "invoice", "notes", "draft", "setup", "backup", "video", "track", "sheet",
        "slides", "readme", "dataset", "export", "capture", "module", "archive", "config", "index", "summary",
    ];

    private static readonly (string Extension, string Type, string Category)[] FileTypes =
    [
        (".jpg", "JPEG image", "Images"),
        (".png", "PNG image", "Images"),
        (".mp4", "MP4 video", "Videos"),
        (".mp3", "MP3 audio", "Audio"),
        (".docx", "Word document", "Documents"),
        (".pdf", "PDF document", "Documents"),
        (".xlsx", "Excel workbook", "Spreadsheets"),
        (".pptx", "PowerPoint presentation", "Presentations"),
        (".zip", "Compressed archive", "Archives"),
        (".exe", "Application", "Executables and installers"),
        (".msi", "Windows Installer package", "Executables and installers"),
        (".cs", "C# source file", "Development files"),
        (".db", "Database file", "Databases"),
        (".sys", "System file", "System and driver files"),
        (".tmp", "Temporary file", "Temporary files"),
        (".dat", "DAT file", "Other / unknown"),
    ];

    private static readonly DateTime ReferenceTime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Local);

    public static SyntheticFolderNode CreateRoot()
    {
        var root = new SyntheticFolderNode("Synthetic volume", @"S:\", depth: 0, childCount: 0, fileCount: 0, seed: 1);
        root.AddFixedChildren(
        [
            new SyntheticFolderNode("Users", @"S:\Users", 1, 8, 40, Seed(@"S:\Users")),
            new SyntheticFolderNode("Program Files", @"S:\Program Files", 1, 12, 15, Seed(@"S:\Program Files")),
            new SyntheticFolderNode("ProgramData", @"S:\ProgramData", 1, 10, 25, Seed(@"S:\ProgramData")),
            new SyntheticFolderNode("Wide folder (5,000 subfolders)", @"S:\Wide", 1, 5_000, 2_000, Seed(@"S:\Wide")),
            new SyntheticFolderNode("Large folder (250,000 files)", @"S:\Large", 1, 0, 250_000, Seed(@"S:\Large")),
            new SyntheticFolderNode("Deep tree (32 levels)", @"S:\Deep", 1, 1, 10, Seed(@"S:\Deep")) { IsDeepChain = true },
        ]);
        root.IsExpanded = true;
        return root;
    }

    internal static IEnumerable<SyntheticFolderNode> GenerateChildren(SyntheticFolderNode parent)
    {
        if (parent.IsDeepChain)
        {
            if (parent.Depth < 32)
            {
                var path = parent.Path + @"\Level" + parent.Depth.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                yield return new SyntheticFolderNode(System.IO.Path.GetFileName(path), path, parent.Depth + 1, parent.Depth < 31 ? 1 : 0, 10, Seed(path)) { IsDeepChain = true };
            }

            yield break;
        }

        var random = new Random(parent.Seed);
        for (var i = 0; i < parent.ChildCount; i++)
        {
            var name = parent.ChildCount > 100
                ? $"Folder {i + 1:D5}"
                : $"{FolderWords[random.Next(FolderWords.Length)]} {i + 1}";
            var path = parent.Path + "\\" + name;
            var childCount = parent.Depth >= 5 || parent.ChildCount > 100 ? 0 : random.Next(0, 9);
            var fileCount = random.Next(0, 1_500);
            yield return new SyntheticFolderNode(name, path, parent.Depth + 1, childCount, fileCount, Seed(path));
        }
    }

    public static List<SyntheticFileRow> GenerateFiles(string folderPath, int count, int seed)
    {
        var random = new Random(seed);
        var rows = new List<SyntheticFileRow>(count);

        for (var i = 0; i < count; i++)
        {
            var (extension, type, category) = FileTypes[random.Next(FileTypes.Length)];

            // Log-uniform sizes from 0 bytes to ~3.5 GB.
            var logical = random.Next(50) == 0 ? 0 : (long)Math.Exp(random.NextDouble() * 22);
            var allocated = (logical + ClusterSize - 1) / ClusterSize * ClusterSize;

            var attributes = (random.Next(20) == 0 ? "H" : string.Empty)
                + (random.Next(50) == 0 ? "S" : string.Empty)
                + (random.Next(10) == 0 ? "R" : string.Empty);

            rows.Add(new SyntheticFileRow
            {
                Name = $"{FileWords[random.Next(FileWords.Length)]}_{i:D7}{extension}",
                FolderPath = folderPath,
                TypeName = type,
                Category = category,
                LogicalSize = logical,
                AllocatedSize = allocated,
                Modified = ReferenceTime.AddSeconds(-random.Next(0, 5 * 365 * 24 * 3600)),
                Attributes = attributes.Length == 0 ? "—" : attributes,
            });
        }

        return rows;
    }

    /// <summary>FNV-1a, so seeds are stable across runs (string.GetHashCode is randomized per process).</summary>
    internal static int Seed(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash & int.MaxValue;
        }
    }
}

internal sealed class SyntheticFileRow
{
    public required string Name { get; init; }

    public required string FolderPath { get; init; }

    public required string TypeName { get; init; }

    public required string Category { get; init; }

    public required long LogicalSize { get; init; }

    public required long AllocatedSize { get; init; }

    public required DateTime Modified { get; init; }

    public required string Attributes { get; init; }

    public string FullPath => FolderPath.TrimEnd('\\') + "\\" + Name;
}

/// <summary>Tree node whose children are generated only when it is first expanded.</summary>
internal sealed partial class SyntheticFolderNode : ObservableObject
{
    private bool _childrenLoaded;

    public SyntheticFolderNode(string name, string path, int depth, int childCount, int fileCount, int seed)
    {
        Name = name;
        Path = path;
        Depth = depth;
        ChildCount = childCount;
        FileCount = fileCount;
        Seed = seed;

        if (childCount > 0)
        {
            Children.Add(LoadingPlaceholder());
        }
    }

    public string Name { get; }

    public string Path { get; }

    public int Depth { get; }

    public int ChildCount { get; private set; }

    public int FileCount { get; }

    public int Seed { get; }

    public bool IsDeepChain { get; init; }

    public bool IsPlaceholder { get; private init; }

    public ObservableCollection<SyntheticFolderNode> Children { get; } = [];

    public string Summary => $"{ChildCount:N0} folders · {FileCount:N0} files";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    internal void AddFixedChildren(IEnumerable<SyntheticFolderNode> children)
    {
        Children.Clear();
        foreach (var child in children)
        {
            Children.Add(child);
        }

        ChildCount = Children.Count;
        _childrenLoaded = true;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _childrenLoaded)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in SyntheticStorage.GenerateChildren(this))
        {
            Children.Add(child);
        }
    }

    private static SyntheticFolderNode LoadingPlaceholder() =>
        new("Loading…", string.Empty, 0, 0, 0, 0) { IsPlaceholder = true };
}
