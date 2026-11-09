namespace FreeAll.Architecture.Tests;

/// <summary>
/// Project-level dependency rules (docs/05-system-architecture.md §4, docs/16-developer-decisions.md TD-001).
/// A new production project must be added to <see cref="AllowedReferences"/> before it can build cleanly.
/// </summary>
public sealed class ProjectDependencyTests
{
    private static readonly string[] FeatureProjects =
    [
        "FreeAll.Scanner",
        "FreeAll.Residue",
        "FreeAll.Duplicates",
        "FreeAll.Cleanup",
    ];

    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        ["FreeAll.Core"] = [],
        ["FreeAll.Application"] = ["FreeAll.Core"],
        ["FreeAll.Scanner"] = ["FreeAll.Application", "FreeAll.Core"],
        ["FreeAll.Residue"] = ["FreeAll.Application", "FreeAll.Core"],
        ["FreeAll.Duplicates"] = ["FreeAll.Application", "FreeAll.Core"],
        ["FreeAll.Cleanup"] = ["FreeAll.Application", "FreeAll.Core"],
        ["FreeAll.Infrastructure"] = ["FreeAll.Application", "FreeAll.Core"],
        ["FreeAll.Helper"] = ["FreeAll.Core"],
        // A standalone executable: it installs FreeAll, so it can't depend on it (TD-049).
        ["FreeAll.Installer"] = [],
        ["FreeAll.Desktop"] =
        [
            "FreeAll.Application",
            "FreeAll.Cleanup",
            "FreeAll.Core",
            "FreeAll.Duplicates",
            "FreeAll.Infrastructure",
            "FreeAll.Residue",
            "FreeAll.Scanner",
        ],
    };

    public static TheoryData<string> ProductionProjects() => [.. AllowedReferences.Keys];

    [Fact]
    public void Every_source_project_has_a_declared_dependency_rule()
    {
        var actual = RepositoryLayout.SourceProjects().Select(p => Path.GetFileNameWithoutExtension(p.Name));

        Assert.Equal(AllowedReferences.Keys.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ProductionProjects))]
    public void Project_references_only_allowed_projects(string projectName)
    {
        var references = RepositoryLayout.ProjectReferences(RepositoryLayout.SourceProject(projectName));

        var disallowed = references.Except(AllowedReferences[projectName], StringComparer.Ordinal).ToList();

        Assert.True(disallowed.Count == 0, $"{projectName} references disallowed projects: {string.Join(", ", disallowed)}");
    }

    [Theory]
    [MemberData(nameof(ProductionProjects))]
    public void Only_desktop_uses_a_ui_framework(string projectName)
    {
        if (projectName == "FreeAll.Desktop")
        {
            return;
        }

        var project = RepositoryLayout.SourceProject(projectName);

        Assert.NotEqual("true", RepositoryLayout.Property(project, "UseWPF"), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("true", RepositoryLayout.Property(project, "UseWindowsForms"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_and_application_are_platform_neutral()
    {
        foreach (var name in new[] { "FreeAll.Core", "FreeAll.Application" })
        {
            Assert.Equal("net10.0", RepositoryLayout.Property(RepositoryLayout.SourceProject(name), "TargetFramework"));
        }
    }

    [Fact]
    public void Feature_projects_do_not_reference_each_other()
    {
        foreach (var feature in FeatureProjects)
        {
            var references = RepositoryLayout.ProjectReferences(RepositoryLayout.SourceProject(feature));

            Assert.DoesNotContain(references, r => FeatureProjects.Contains(r, StringComparer.Ordinal));
        }
    }
}
