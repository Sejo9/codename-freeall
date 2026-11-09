using FreeAll.Infrastructure.Windows;

namespace FreeAll.Integration.Tests.Windows;

/// <summary>Windows' own Recycle Bin figure, read through the supported shell query (F-034). Read-only.</summary>
public sealed class RecycleBinUsageTests
{
    [Fact]
    public async Task The_windows_drive_reports_a_size_and_an_item_count()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        var usage = Assert.Single(await new ShellRecycleBinUsage().QueryAsync([root], TestContext.Current.CancellationToken));

        Assert.Equal(root, usage.Root);
        Assert.NotNull(usage.Bytes);
        Assert.True(usage.Bytes >= 0 && usage.Items >= 0);
        Assert.Null(usage.Problem);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\")]
    [InlineData("C:")]
    [InlineData("")]
    public void Anything_but_a_plain_drive_root_is_not_passed_to_the_shell(string root)
    {
        var usage = ShellRecycleBinUsage.Query(root);

        Assert.Null(usage.Bytes);
        Assert.NotNull(usage.Problem);
    }
}
