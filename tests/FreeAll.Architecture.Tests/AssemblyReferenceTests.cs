using System.Reflection;
using System.Reflection.PortableExecutable;

namespace FreeAll.Architecture.Tests;

/// <summary>
/// Compiled-assembly rules. These catch UI or cross-layer coupling that arrives through
/// package references or transitive dependencies rather than direct project references.
/// </summary>
public sealed class AssemblyReferenceTests
{
    private static readonly string[] UiAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "System.Windows.Forms",
        "System.Xaml",
        "WindowsBase",
    ];

    // The Desktop project's assembly name is "FreeAll" (see FreeAll.Desktop.csproj).
    private const string DesktopAssemblyName = "FreeAll";

    public static TheoryData<string> NonUiAssemblies() =>
    [
        "FreeAll.Core",
        "FreeAll.Application",
        "FreeAll.Scanner",
        "FreeAll.Residue",
        "FreeAll.Duplicates",
        "FreeAll.Cleanup",
        "FreeAll.Infrastructure",
        "FreeAll.Helper",
    ];

    [Theory]
    [MemberData(nameof(NonUiAssemblies))]
    public void Non_ui_assembly_does_not_reference_ui_frameworks(string assemblyName)
    {
        var references = ReferencedAssemblyNames(assemblyName);

        Assert.DoesNotContain(references, r => UiAssemblies.Contains(r, StringComparer.Ordinal));
        Assert.DoesNotContain(DesktopAssemblyName, references);
    }

    [Fact]
    public void Core_does_not_reference_other_freeall_assemblies()
    {
        var references = ReferencedAssemblyNames("FreeAll.Core");

        Assert.DoesNotContain(references, r => r.StartsWith("FreeAll", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NonUiAssemblies))]
    public void Assembly_is_built_for_x64(string assemblyName)
    {
        var path = Assembly.Load(new AssemblyName(assemblyName)).Location;

        using var stream = File.OpenRead(path);
        using var reader = new PEReader(stream);

        Assert.Equal(Machine.Amd64, reader.PEHeaders.CoffHeader.Machine);
    }

    private static List<string> ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(new AssemblyName(assemblyName))
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();
}
