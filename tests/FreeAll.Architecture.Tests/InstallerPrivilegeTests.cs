using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FreeAll.Architecture.Tests;

/// <summary>
/// The installer installs for the current user only (DEC-008, docs/13-deployment-release.md §2): it never asks
/// for elevation and never writes machine-wide locations.
/// </summary>
public sealed partial class InstallerPrivilegeTests
{
    private static readonly XNamespace AsmV3 = "urn:schemas-microsoft-com:asm.v3";

    private static DirectoryInfo InstallerDirectory => new(Path.Combine(RepositoryLayout.SourceDirectory.FullName, "FreeAll.Installer"));

    [Fact]
    public void Installer_manifest_requests_as_invoker()
    {
        var level = XDocument.Load(Path.Combine(InstallerDirectory.FullName, "app.manifest")).Descendants(AsmV3 + "requestedExecutionLevel").Single();

        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
        Assert.Equal("app.manifest", RepositoryLayout.Property(RepositoryLayout.SourceProject("FreeAll.Installer"), "ApplicationManifest"));
    }

    [Fact]
    public void Installer_never_targets_machine_wide_locations_or_elevation()
    {
        foreach (var file in InstallerDirectory.EnumerateFiles("*.cs", SearchOption.AllDirectories).Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file.FullName);
            Assert.False(MachineWide().IsMatch(text), $"{file.Name} refers to a machine-wide location or to elevation.");
        }
    }

    [Fact]
    public void Installer_is_published_as_native_code_for_x64()
    {
        var project = RepositoryLayout.SourceProject("FreeAll.Installer");

        Assert.Equal("true", RepositoryLayout.Property(project, "PublishAot"));
        Assert.Null(RepositoryLayout.Property(project, "UseWPF"));
    }

    [GeneratedRegex(@"LocalMachine|HKEY_LOCAL_MACHINE|HKLM|SpecialFolder\.(ProgramFiles|CommonApplicationData|CommonPrograms|CommonStartMenu|CommonDesktopDirectory|Windows|System)|""runas""|Verb\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex MachineWide();
}
