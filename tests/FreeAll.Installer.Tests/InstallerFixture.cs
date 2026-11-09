using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FreeAll.Installer.Tests;

/// <summary>
/// A throwaway folder with its own install root, Start menu folder and data folder. Everything a test
/// creates is under it and is removed afterwards; no real installation location is ever used.
/// </summary>
internal sealed class InstallerFixture : IDisposable
{
    public InstallerFixture()
    {
        Directory = System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FreeAll.Installer.Tests", Guid.NewGuid().ToString("N"))).FullName;
        Locations = new InstallLocations(
            Path.Combine(Directory, "Programs", "FreeAll"),
            Path.Combine(Directory, "StartMenu"),
            Path.Combine(Directory, "Data", "FreeAll"),
            InstallLocations.TestRegistryPrefix + Guid.NewGuid().ToString("N"));
        Stub = Path.Combine(Directory, "stub.exe");

        // Any bytes do as a stub for packing; only a real process test needs a real executable.
        File.WriteAllBytes(Stub, Encoding.ASCII.GetBytes("MZ fake installer stub for tests"));
    }

    public string Directory { get; }

    public InstallLocations Locations { get; }

    public string Stub { get; }

    public FakeRegistration Registration { get; } = new();

    public TestUi Ui { get; } = new();

    /// <summary>A fake app folder for a version: FreeAll.exe whose bytes name the version, plus a library in a subfolder.</summary>
    public string App(string version, params (string Path, string Content)[] extra)
    {
        var folder = Path.Combine(Directory, "app-" + version);
        System.IO.Directory.CreateDirectory(Path.Combine(folder, "runtimes", "win-x64"));
        File.WriteAllText(Path.Combine(folder, "FreeAll.exe"), "FreeAll " + version);
        File.WriteAllText(Path.Combine(folder, "FreeAll.Helper.exe"), "helper " + version);
        File.WriteAllText(Path.Combine(folder, "runtimes", "win-x64", "e_sqlite3.dll"), "native " + version);
        File.WriteAllText(Path.Combine(folder, "FreeAll.pdb"), "symbols are left out");
        foreach (var (path, content) in extra)
        {
            File.WriteAllText(Path.Combine(folder, path), content);
        }

        return folder;
    }

    public string Setup(string version, params (string Path, string Content)[] extra)
    {
        var output = Path.Combine(Directory, "FreeAll-Setup-" + version + ".exe");
        Payload.Pack(Stub, App(version, extra), Version.Parse(version), output);
        return output;
    }

    public InstallEngine Engine(Func<string, bool>? running = null) =>
        new(Locations, Ui, Registration, running ?? (_ => false), TimeSpan.FromSeconds(1));

    public ExitCode Install(string setupPath, bool update = false, Func<string, bool>? running = null)
    {
        using var setup = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var location = Payload.Find(setup) ?? throw new InvalidOperationException("No payload.");
        return Engine(running).Install(setup, location, update);
    }

    /// <summary>A setup file written by hand, for packages Pack would never produce.</summary>
    public string CraftedSetup(Action<ZipArchive> fill)
    {
        var output = Path.Combine(Directory, "crafted-" + Guid.NewGuid().ToString("N") + ".exe");
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(archive);
        }

        var zip = memory.ToArray();
        var stub = File.ReadAllBytes(Stub);
        var trailer = new byte[Payload.TrailerLength];
        BitConverter.TryWriteBytes(trailer.AsSpan(0, 8), (long)stub.Length);
        BitConverter.TryWriteBytes(trailer.AsSpan(8, 8), (long)zip.Length);
        SHA256.HashData(zip).CopyTo(trailer, 16);
        Encoding.ASCII.GetBytes("FREEALL-SETUP-01").CopyTo(trailer, 48);
        File.WriteAllBytes(output, [.. stub, .. zip, .. trailer]);

        return output;
    }

    public static void AddFile(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    public static string ManifestJson(string version, params (string Path, string Content)[] files)
    {
        var entries = string.Join(",", files.Select(f =>
            $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(f.Path)},\"size\":{Encoding.UTF8.GetByteCount(f.Content)},\"sha256\":\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Content)))}\"}}"));
        return $"{{\"schema\":\"freeall.install\",\"schemaVersion\":1,\"product\":\"FreeAll\",\"version\":\"{version}\",\"architecture\":\"x64\",\"files\":[{entries}]}}";
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Locations.RegistryKey, throwOnMissingSubKey: false);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A process test may still be releasing a file; the temp folder is cleaned by Windows later.
        }
    }
}

internal sealed class FakeRegistration : IRegistration
{
    public string? Version { get; private set; }

    public int Registrations { get; private set; }

    public bool Fail { get; set; }

    public void Register(InstallLocations locations, InstallManifest manifest)
    {
        if (Fail)
        {
            throw new IOException("Registry write failed (simulated).");
        }

        Version = manifest.Version.ToString(3);
        Registrations++;
    }

    public void Unregister(InstallLocations locations) => Version = null;

    public string? RegisteredVersion(InstallLocations locations) => Version;
}

/// <summary>Silent answers that a test chooses, with a record of what was asked.</summary>
internal sealed class TestUi : IInstallerUi
{
    public bool IsSilent => true;

    public bool ConfirmAnswer { get; set; } = true;

    public DataChoice DataAnswer { get; set; } = DataChoice.Keep;

    public List<string> Asked { get; } = [];

    public List<string> Failures { get; } = [];

    public bool Confirm(string instruction, string content)
    {
        Asked.Add(instruction);
        return ConfirmAnswer;
    }

    public DataChoice AskAboutData(string instruction, string content)
    {
        Asked.Add(instruction);
        return DataAnswer;
    }

    public bool AskRetry(string instruction, string content)
    {
        Asked.Add(instruction);
        return false;
    }

    public void Inform(string instruction, string content) => Asked.Add(instruction + " " + content);

    public void Fail(string instruction, string content) => Failures.Add(instruction + " " + content);

    public void Log(string line)
    {
    }
}
