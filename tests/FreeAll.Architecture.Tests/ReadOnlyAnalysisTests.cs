namespace FreeAll.Architecture.Tests;

/// <summary>
/// Residue, temporary-file and duplicate analysis only read (docs/08-security-privacy.md §6: no automatic removal;
/// residue can't be removed automatically). A guard against file, registry or process calls creeping in.
/// </summary>
public sealed class ReadOnlyAnalysisTests
{
    private static readonly string[] Forbidden =
    [
        "File.Delete", "File.Move", "File.Replace", "File.WriteAll", "File.Create", "File.Open(", "Directory.Delete", "Directory.Move", "Directory.CreateDirectory",
        ".Delete(", "DeleteSubKey", "CreateSubKey", "SetValue(", "DeleteValue", "Process.Start", "FileStream", "SHFileOperation", "IFileOperation",
    ];

    /// <summary>Win32 access that could change a file. Duplicate detection reads contents, so it is held to more than the residue project.</summary>
    private static readonly string[] ForbiddenAccess =
    [
        "GenericWrite", "GENERIC_WRITE", "FileWrite", "FILE_WRITE", "FILE_APPEND", "DELETE =", "WriteDac", "WriteOwner", "SetFileInformation", "SetFileTime", "DeviceIoControl", "WriteFile",
        "DeleteFile", "MoveFile", "RemoveDirectory", "SeBackupPrivilege", "AdjustTokenPrivileges", "RandomAccess.Write",
    ];

    [Fact]
    public void The_residue_project_never_changes_files_registry_or_starts_programs() =>
        AssertNoCalls("FreeAll.Residue", Forbidden);

    [Fact]
    public void Duplicate_detection_never_asks_for_anything_but_reading() =>
        AssertNoCalls("FreeAll.Duplicates", [.. Forbidden, .. ForbiddenAccess]);

    private static void AssertNoCalls(string project, string[] forbidden)
    {
        var sources = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", project))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var violations = sources
            .SelectMany(f => File.ReadAllLines(f.FullName).Select((line, i) => (File: f.Name, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => forbidden.Any(f => l.Text.Contains(f, StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.Empty(violations);
        Assert.NotEmpty(sources);
    }
}
