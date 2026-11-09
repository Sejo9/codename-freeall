using System.Diagnostics;
using FreeAll.Infrastructure.Updates;

namespace FreeAll.Integration.Tests.Updates;

/// <summary>The release scripts in tools/updates produce what the app accepts (TD-050).</summary>
public sealed class UpdateToolingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_manifest_signed_by_the_release_scripts_verifies_in_the_app()
    {
        var pwsh = FindPowerShell();
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) isn't on PATH.");
        var tools = Path.Combine(RepositoryRoot(), "tools", "updates");
        var key = _temp.Combine("update-key.pem");
        var setup = _temp.Combine("FreeAll-Setup-9.1.0-x64.exe");
        File.WriteAllText(setup, "pretend setup");

        var publicKey = Run(pwsh!, $"$p = ConvertTo-SecureString 'test-only-password' -AsPlainText -Force; & '{tools}\\New-UpdateKey.ps1' -PrivateKeyPath '{key}' -Password $p").Trim().Split('\n')[^1].Trim();
        Run(pwsh!, $"$p = ConvertTo-SecureString 'test-only-password' -AsPlainText -Force; & '{tools}\\New-UpdateManifest.ps1' -SetupPath '{setup}' -Version 9.1.0 -PackageUrl 'https://updates.example.test/FreeAll-Setup-9.1.0-x64.exe' -Sequence 12 -PrivateKeyPath '{key}' -OutputDirectory '{_temp.Path}' -Password $p -Notes 'Test notes' -Now '2026-11-08T00:00:00Z'");

        var configuration = UpdateConfiguration.TryCreate("https://updates.example.test/manifest.json", publicKey);
        Assert.NotNull(configuration);
        using var verifier = UpdateManifestVerifier.LoadPublicKey(configuration.PublicKey);
        var manifest = UpdateManifestVerifier.Verify(
            File.ReadAllBytes(_temp.Combine("manifest.json")),
            File.ReadAllBytes(_temp.Combine("manifest.json.sig")),
            verifier,
            new UpdateVerificationContext(11, new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(new Version(9, 1, 0), manifest.Version);
        Assert.Equal(12, manifest.Sequence);
        Assert.Equal(new FileInfo(setup).Length, manifest.PackageSize);
        Assert.Equal("Test notes", manifest.Notes);
        Assert.DoesNotContain("PRIVATE KEY-----" + Environment.NewLine + "MI", File.ReadAllText(key), StringComparison.Ordinal);
        Assert.Contains("ENCRYPTED PRIVATE KEY", File.ReadAllText(key), StringComparison.Ordinal);
    }

    private static string Run(string pwsh, string script)
    {
        var start = new ProcessStartInfo(pwsh) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error + output);
        return output;
    }

    private static string? FindPowerShell() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "pwsh.exe"))
            .FirstOrDefault(File.Exists);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeAll.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
