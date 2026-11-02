using System.Xml.Linq;

namespace FreeAll.Architecture.Tests;

/// <summary>
/// The main application runs as the signed-in standard user and never requests elevation at launch
/// (DEC-007, docs/08-security-privacy.md §3).
/// </summary>
public sealed class PrivilegeManifestTests
{
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";

    [Fact]
    public void Desktop_manifest_requests_as_invoker_without_ui_access()
    {
        var manifestPath = Path.Combine(RepositoryLayout.SourceDirectory.FullName, "FreeAll.Desktop", "app.manifest");

        var level = XDocument.Load(manifestPath).Descendants(AsmV3 + "requestedExecutionLevel").Single();

        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }

    [Fact]
    public void Desktop_project_uses_the_checked_in_manifest()
    {
        var project = RepositoryLayout.SourceProject("FreeAll.Desktop");

        Assert.Equal("app.manifest", RepositoryLayout.Property(project, "ApplicationManifest"));
    }
}
