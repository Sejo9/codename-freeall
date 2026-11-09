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

    /// <summary>
    /// The release workflow signs with the key's text (a GitHub secret) and checks the result, and the setup
    /// file, with Test-UpdateManifest.ps1 before publishing (DEC-031).
    /// </summary>
    [Fact]
    public void The_release_check_accepts_what_was_signed_and_refuses_another_key_or_setup_file()
    {
        var pwsh = FindPowerShell();
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) isn't on PATH.");
        var tools = Path.Combine(RepositoryRoot(), "tools", "updates");
        var key = _temp.Combine("update-key.pem");
        var otherKey = _temp.Combine("other-key.pem");
        var setup = _temp.Combine("FreeAll-Setup-9.2.0-x64.exe");
        File.WriteAllText(setup, "pretend setup");
        const string Password = "$p = ConvertTo-SecureString 'test-only-password' -AsPlainText -Force; ";

        var publicKey = Run(pwsh!, Password + $"& '{tools}\\New-UpdateKey.ps1' -PrivateKeyPath '{key}' -Password $p").Trim().Split('\n')[^1].Trim();
        var otherPublicKey = Run(pwsh!, Password + $"& '{tools}\\New-UpdateKey.ps1' -PrivateKeyPath '{otherKey}' -Password $p").Trim().Split('\n')[^1].Trim();
        var output = _temp.Combine("signed");
        Run(pwsh!, Password + $"$pem = Get-Content '{key}' -Raw; & '{tools}\\New-UpdateManifest.ps1' -SetupPath '{setup}' -Version 9.2.0 -PackageUrl 'https://updates.example.test/s.exe' -Sequence 1762646400 -PrivateKeyPem $pem -OutputDirectory '{output}' -Password $p");

        var check = $"& '{tools}\\Test-UpdateManifest.ps1' -ManifestPath '{output}\\manifest.json' -SignaturePath '{output}\\manifest.json.sig'";
        var result = Run(pwsh!, check + $" -PublicKey '{publicKey}' -SetupPath '{setup}' | Format-List");
        Assert.Contains("1762646400", result, StringComparison.Ordinal);

        var wrongKey = RunExpectingFailure(pwsh!, check + $" -PublicKey '{otherPublicKey}'");
        Assert.Contains("signing key and the key built into the app differ", wrongKey, StringComparison.Ordinal);

        File.AppendAllText(setup, "!");
        var swapped = RunExpectingFailure(pwsh!, check + $" -PublicKey '{publicKey}' -SetupPath '{setup}'");
        Assert.Contains("bytes; the manifest says", swapped, StringComparison.Ordinal);
    }

    private static string RunExpectingFailure(string pwsh, string script)
    {
        var (exitCode, output) = Execute(pwsh, script);
        Assert.NotEqual(0, exitCode);
        return output;
    }

    private static string Run(string pwsh, string script)
    {
        var (exitCode, output) = Execute(pwsh, script);
        Assert.True(exitCode == 0, output);
        return output;
    }

    private static (int ExitCode, string Output) Execute(string pwsh, string script)
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
        return (process.ExitCode, process.ExitCode == 0 ? output : error + output);
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
