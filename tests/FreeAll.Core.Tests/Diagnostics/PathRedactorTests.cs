using FreeAll.Core.Diagnostics;

namespace FreeAll.Core.Tests.Diagnostics;

public sealed class PathRedactorTests
{
    private readonly PathRedactor _redactor = new();

    [Fact]
    public void Redacted_path_keeps_only_drive_root_and_fingerprint()
    {
        var redacted = _redactor.Redact(@"C:\Users\alice\Documents\Secret Plans.docx");

        Assert.StartsWith(@"C:\[path:", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fingerprint_is_stable_and_ignores_case_and_separator_style()
    {
        Assert.Equal(
            _redactor.Redact(@"C:\Users\Alice\file.txt"),
            _redactor.Redact("c:/users/alice/FILE.TXT"));
    }

    [Fact]
    public void Different_paths_have_different_fingerprints()
    {
        Assert.NotEqual(_redactor.Redact(@"C:\a\one.txt"), _redactor.Redact(@"C:\a\two.txt"));
    }

    [Theory]
    [InlineData(@"\\fileserver\finance\q3.xlsx")]
    [InlineData(@"\\?\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\data")]
    public void Unc_and_volume_roots_are_fully_redacted(string path)
    {
        var redacted = _redactor.Redact(path);

        Assert.StartsWith("[path:", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Volume", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_paths_are_kept_only_when_explicitly_enabled()
    {
        const string path = @"D:\Projects\report.pdf";

        Assert.Equal(path, new PathRedactor(includeFullPaths: true).Redact(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_path_is_reported_as_such(string? path)
    {
        Assert.Equal("[no path]", _redactor.Redact(path));
    }
}
