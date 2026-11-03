using FreeAll.Core.Scanning;

namespace FreeAll.Core.Tests.Scanning;

public sealed class CloudAndReviewRulesTests
{
    [Theory]
    [InlineData(0x9000001Au, true)]  // IO_REPARSE_TAG_CLOUD
    [InlineData(0x9000701Au, true)]  // OneDrive (CLOUD_7)
    [InlineData(0x9000F01Au, true)]  // CLOUD_F
    [InlineData(0xA0000003u, false)] // junction or mount point
    [InlineData(0xA000000Cu, false)] // symbolic link
    [InlineData(0x80000017u, false)] // WOF
    [InlineData(0x9000001Cu, false)] // projected file system
    [InlineData(0u, false)]
    public void Only_cloud_placeholder_folders_are_listed(uint tag, bool listed) =>
        Assert.Equal(listed, ReparsePoints.IsListedDirectory(tag));

    [Fact]
    public void Cloud_only_files_are_recognized_by_their_recall_attributes()
    {
        Assert.True(CloudFiles.IsCloudOnly(FileAttributes.Archive | CloudFiles.RecallOnDataAccess));
        Assert.True(CloudFiles.IsCloudOnly(CloudFiles.RecallOnOpen));
        Assert.False(CloudFiles.IsCloudOnly(FileAttributes.Archive | CloudFiles.Pinned));
        Assert.False(CloudFiles.IsCloudOnly(FileAttributes.Offline));
    }

    [Theory]
    [InlineData(362)]
    [InlineData(388)]
    [InlineData(404)]
    public void Cloud_provider_errors_make_a_scan_partial(int error)
    {
        Assert.Equal(ScanIssueKind.CloudUnavailable, ScanIssueKinds.FromWin32Error(error));
        Assert.True(ScanIssueKind.CloudUnavailable.AffectsCompleteness());
    }
}
