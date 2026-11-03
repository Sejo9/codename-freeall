using FreeAll.Core.Volumes;

namespace FreeAll.Core.Tests.Volumes;

public sealed class VolumePathTests
{
    private static readonly VolumeId Volume = VolumeId.Parse(@"\\?\Volume{0a1b2c3d-1111-2222-3333-444455556666}\");

    [Theory]
    [InlineData(@"\", @"\")]
    [InlineData(@"\\", @"\")]
    [InlineData(@"\Users", @"\Users")]
    [InlineData(@"\Users\", @"\Users")]
    [InlineData(@"\Users\Ana\trailing dot.", @"\Users\Ana\trailing dot.")]
    public void Normalizes_valid_paths(string input, string expected)
    {
        Assert.True(VolumePath.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Users")]
    [InlineData(@"C:\Users")]
    [InlineData(@"\Users\\Ana")]
    [InlineData(@"\Users\.\Ana")]
    [InlineData(@"\Users\..\Windows")]
    [InlineData(@"\Users\a:stream")]
    [InlineData(@"\Users/Ana")]
    public void Rejects_invalid_paths(string? input) => Assert.False(VolumePath.TryNormalize(input, out _));

    [Fact]
    public void Combines_names_and_finds_parents()
    {
        Assert.Equal(@"\Users", VolumePath.Combine(VolumePath.Root, "Users"));
        Assert.Equal(@"\Users\Ana", VolumePath.Combine(@"\Users", "Ana"));
        Assert.Equal("Ana", VolumePath.NameOf(@"\Users\Ana"));
        Assert.Equal(string.Empty, VolumePath.NameOf(VolumePath.Root));
        Assert.Equal(@"\Users", VolumePath.ParentOf(@"\Users\Ana"));
        Assert.Equal(VolumePath.Root, VolumePath.ParentOf(@"\Users"));
        Assert.Null(VolumePath.ParentOf(VolumePath.Root));
    }

    [Theory]
    [InlineData(@"\Games", @"\Games", true)]
    [InlineData(@"\GAMES\Saves", @"\Games", true)]
    [InlineData(@"\GamesBackup", @"\Games", false)]
    [InlineData(@"\Anything", @"\", true)]
    [InlineData(@"\Gam", @"\Games", false)]
    public void Ancestor_test_respects_segment_boundaries(string path, string ancestor, bool expected) =>
        Assert.Equal(expected, VolumePath.IsSameOrBelow(path, ancestor));

    [Fact]
    public void Native_root_path_keeps_its_trailing_separator()
    {
        // Without the separator, the GUID path opens the volume device rather than its root folder.
        Assert.Equal(@"\\?\Volume{0a1b2c3d-1111-2222-3333-444455556666}\", VolumePath.ToNativePath(Volume, VolumePath.Root));
        Assert.Equal(@"\\?\Volume{0a1b2c3d-1111-2222-3333-444455556666}\Users\Ana", VolumePath.ToNativePath(Volume, @"\Users\Ana"));
    }

    [Fact]
    public void Display_path_uses_the_mount_point_when_there_is_one()
    {
        Assert.Equal(@"D:\Games", VolumePath.ToDisplayPath(@"D:\", Volume, @"\Games"));
        Assert.Equal(@"D:\", VolumePath.ToDisplayPath(@"D:\", Volume, VolumePath.Root));
        Assert.Equal(@"C:\Mount\Data\Games", VolumePath.ToDisplayPath(@"C:\Mount\Data", Volume, @"\Games"));
        Assert.Equal(Volume.Path + "Games", VolumePath.ToDisplayPath(null, Volume, @"\Games"));
    }
}
