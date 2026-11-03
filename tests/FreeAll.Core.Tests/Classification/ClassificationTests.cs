using FreeAll.Core.Classification;

namespace FreeAll.Core.Tests.Classification;

public sealed class ClassificationTests
{
    [Theory]
    [InlineData("photo.JPG", "jpg")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData(".gitignore", "gitignore")]
    [InlineData("README", "")]
    [InlineData("trailing dot.", "")]
    public void Extension_is_lowercase_without_the_dot(string name, string expected) =>
        Assert.Equal(expected, FileClassifier.ExtensionOf(name));

    [Theory]
    [InlineData("jpg", FileCategory.Images)]
    [InlineData("mkv", FileCategory.Videos)]
    [InlineData("flac", FileCategory.Audio)]
    [InlineData("pdf", FileCategory.Documents)]
    [InlineData("xlsx", FileCategory.Spreadsheets)]
    [InlineData("pptx", FileCategory.Presentations)]
    [InlineData("7z", FileCategory.Archives)]
    [InlineData("msi", FileCategory.Executables)]
    [InlineData("cs", FileCategory.Development)]
    [InlineData("sqlite", FileCategory.Databases)]
    [InlineData("sys", FileCategory.System)]
    [InlineData("tmp", FileCategory.Temporary)]
    public void Known_extensions_win_over_location(string extension, FileCategory expected) =>
        Assert.Equal(new FileClassification(expected, ClassificationReason.Extension), FileClassifier.Classify(extension, StorageLocation.TemporaryFolder));

    [Theory]
    [InlineData(StorageLocation.TemporaryFolder, FileCategory.Temporary)]
    [InlineData(StorageLocation.Windows, FileCategory.System)]
    [InlineData(StorageLocation.UserAppData, FileCategory.ApplicationData)]
    [InlineData(StorageLocation.ProgramData, FileCategory.ApplicationData)]
    public void Unknown_extensions_fall_back_to_location(StorageLocation location, FileCategory expected) =>
        Assert.Equal(new FileClassification(expected, ClassificationReason.Location), FileClassifier.Classify("xyz", location));

    [Fact]
    public void Unknown_files_elsewhere_are_other()
    {
        Assert.Equal(new FileClassification(FileCategory.Other, ClassificationReason.None), FileClassifier.Classify("xyz", StorageLocation.UserProfile));
        Assert.Equal(new FileClassification(FileCategory.Other, ClassificationReason.None), FileClassifier.Classify(string.Empty, StorageLocation.Other));
    }

    [Fact]
    public void Ambiguous_ts_extension_is_not_classified() =>
        Assert.Equal(FileCategory.Other, FileClassifier.Classify("ts", StorageLocation.Other).Category);

    [Fact]
    public void Every_extension_belongs_to_one_category()
    {
        var all = Enum.GetValues<FileCategory>().SelectMany(FileClassifier.ExtensionsFor).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, e => Assert.Equal(e.ToLowerInvariant(), e));
    }

    [Theory]
    [InlineData(@"\Windows\System32", StorageLocation.Windows)]
    [InlineData(@"\WINDOWS\Temp\x", StorageLocation.TemporaryFolder)]
    [InlineData(@"\Program Files (x86)\App", StorageLocation.ProgramFiles)]
    [InlineData(@"\ProgramData\Vendor", StorageLocation.ProgramData)]
    [InlineData(@"\Users\Ana\Documents", StorageLocation.UserProfile)]
    [InlineData(@"\Users\Ana\AppData\Roaming\App", StorageLocation.UserAppData)]
    [InlineData(@"\Users\Ana\AppData\Local\Temp", StorageLocation.TemporaryFolder)]
    [InlineData(@"\Users\Ana\AppData\Local\Temp\a\b\c", StorageLocation.TemporaryFolder)]
    [InlineData(@"\$Recycle.Bin\S-1-5-21", StorageLocation.RecycleBin)]
    [InlineData(@"\System Volume Information", StorageLocation.SystemVolumeInformation)]
    [InlineData(@"\Users", StorageLocation.Other)]
    [InlineData(@"\", StorageLocation.Other)]
    [InlineData(@"\Games", StorageLocation.Other)]
    public void Locations_are_recognized_by_leading_segments(string path, StorageLocation expected) =>
        Assert.Equal(expected, StorageLocations.Classify(path));
}
