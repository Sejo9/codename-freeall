using System.Xml.Linq;

namespace FreeAll.Architecture.Tests;

/// <summary>
/// Locates the repository and reads production project files without building them.
/// </summary>
internal static class RepositoryLayout
{
    private const string SolutionFileName = "FreeAll.slnx";

    public static DirectoryInfo Root { get; } = FindRoot();

    public static DirectoryInfo SourceDirectory => new(Path.Combine(Root.FullName, "src"));

    public static IReadOnlyList<FileInfo> SourceProjects() =>
        SourceDirectory
            .EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

    public static FileInfo SourceProject(string projectName) =>
        new(Path.Combine(SourceDirectory.FullName, projectName, projectName + ".csproj"));

    public static IReadOnlyList<string> ProjectReferences(FileInfo project) =>
        XDocument.Load(project.FullName)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static string? Property(FileInfo project, string propertyName) =>
        XDocument.Load(project.FullName)
            .Descendants(propertyName)
            .Select(e => e.Value.Trim())
            .LastOrDefault();

    private static DirectoryInfo FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir;
            }
        }

        throw new InvalidOperationException($"Could not locate {SolutionFileName} above {AppContext.BaseDirectory}.");
    }
}
