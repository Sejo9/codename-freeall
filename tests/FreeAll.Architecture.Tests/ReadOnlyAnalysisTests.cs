namespace FreeAll.Architecture.Tests;

/// <summary>
/// Residue and temporary-file analysis only reads (docs/08-security-privacy.md §6: no automatic removal;
/// residue can't be removed automatically). A guard against file, registry or process calls creeping in.
/// </summary>
public sealed class ReadOnlyAnalysisTests
{
    private static readonly string[] Forbidden =
    [
        "File.Delete", "File.Move", "File.Replace", "File.WriteAll", "File.Create", "File.Open(", "Directory.Delete", "Directory.Move", "Directory.CreateDirectory",
        ".Delete(", "DeleteSubKey", "CreateSubKey", "SetValue(", "DeleteValue", "Process.Start", "FileStream", "SHFileOperation", "IFileOperation",
    ];

    [Fact]
    public void The_residue_project_never_changes_files_registry_or_starts_programs()
    {
        var sources = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src", "FreeAll.Residue"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var violations = sources
            .SelectMany(f => File.ReadAllLines(f.FullName).Select((line, i) => (File: f.Name, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => Forbidden.Any(f => l.Text.Contains(f, StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.Empty(violations);
        Assert.NotEmpty(sources);
    }
}
