using FreeAll.Core.Volumes;

namespace FreeAll.Core.Tests.Volumes;

public sealed class VolumeIdTests
{
    private const string Sample = @"\\?\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\";

    [Theory]
    [InlineData(Sample)]
    [InlineData(@"\\?\Volume{3F2504E0-4F89-11D3-9A0C-0305E82C3301}\")]
    [InlineData(@"\\?\volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    public void Parses_volume_guid_paths_case_insensitively_with_optional_trailing_slash(string path)
    {
        Assert.True(VolumeId.TryParse(path, out var id));
        Assert.Equal(VolumeId.Parse(Sample), id);
        Assert.Equal(Sample, id.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\Volume{not-a-guid}\")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\")]
    [InlineData(@"\\?\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\Windows")]
    [InlineData(@"\\?\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void Rejects_anything_that_is_not_a_volume_guid_path(string? path)
    {
        Assert.False(VolumeId.TryParse(path, out _));
    }

    [Fact]
    public void Parse_throws_for_invalid_input()
    {
        Assert.Throws<FormatException>(() => VolumeId.Parse(@"D:\"));
    }

    [Fact]
    public void Empty_guid_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => VolumeId.FromGuid(Guid.Empty));
    }
}
