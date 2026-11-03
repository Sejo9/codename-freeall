using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Scanning;

namespace FreeAll.Application.Tests.Formatting;

public sealed class ScanTextTests
{
    [Fact]
    public void Attribute_letters_follow_a_fixed_order()
    {
        Assert.Equal("RHSA", ScanText.Attributes(FileAttributes.Archive | FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly));
        Assert.Equal(string.Empty, ScanText.Attributes(FileAttributes.Normal));
        Assert.Equal("PL", ScanText.Attributes(FileAttributes.ReparsePoint | FileAttributes.SparseFile));
        Assert.Equal("Hidden, System", ScanText.AttributeNames(FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal("None", ScanText.AttributeNames(0));
    }

    [Fact]
    public void Classification_is_explained_in_words()
    {
        Assert.Equal(
            "The .jpg extension is listed under Images.",
            ScanText.ClassificationExplanation(new(FileCategory.Images, ClassificationReason.Extension), "jpg", StorageLocation.Other));
        Assert.Equal(
            "The extension isn't recognized, and the file is in a user's application data (AppData).",
            ScanText.ClassificationExplanation(new(FileCategory.ApplicationData, ClassificationReason.Location), "bin", StorageLocation.UserAppData));
        Assert.Equal(
            "The file has no extension, and its location has no rule.",
            ScanText.ClassificationExplanation(new(FileCategory.Other, ClassificationReason.None), string.Empty, StorageLocation.Other));
    }

    [Fact]
    public void Every_enum_value_has_text()
    {
        Assert.All(Enum.GetValues<ScanState>(), s => Assert.NotEqual("Unknown", ScanText.State(s)));
        Assert.All(Enum.GetValues<DirectoryScanState>(), s => Assert.NotEqual("Unknown", ScanText.FolderState(s)));
        Assert.Equal(Enum.GetValues<FileCategory>().Length, Enum.GetValues<FileCategory>().Select(ScanText.Category).Distinct().Count());
    }

    [Fact]
    public void Notes_explain_why_a_scan_is_not_complete()
    {
        var progress = new ScanProgress();
        progress.AddIssue(affectsCompleteness: true);
        progress.AddNotScanned();

        var note = ScanNotes.Describe(ScanState.Partial, new VolumeScanResult(ScanTermination.Cancelled), progress, scannerFailed: false);

        Assert.Equal("The scan was cancelled before it finished. 1 folder was found but not scanned. 1 item couldn't be read, so sizes above it are lower than the real totals.", note);
    }

    [Fact]
    public void A_complete_scan_mentions_only_items_removed_during_the_scan()
    {
        var progress = new ScanProgress();
        Assert.Null(ScanNotes.Describe(ScanState.Completed, new VolumeScanResult(ScanTermination.Finished), progress, false));

        progress.AddIssue(affectsCompleteness: false);
        Assert.Equal("1 item was removed while the scan was running.", ScanNotes.Describe(ScanState.Completed, new VolumeScanResult(ScanTermination.Finished), progress, false));
    }
}
