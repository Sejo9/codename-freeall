using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FreeAll.Application.Cleanup;
using FreeAll.Cleanup.Elevation;
using FreeAll.Cleanup.Files;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;
using FreeAll.Helper;

namespace FreeAll.Security.Tests;

/// <summary>
/// The real helper executable, started here without elevation: everything but UAC itself is the same.
/// A real elevated run needs a person at the prompt and is in docs/12-quality-assurance.md.
/// </summary>
public sealed class HelperProcessTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FreeAll.Tests", Guid.NewGuid().ToString("N"));

    public HelperProcessTests() => Directory.CreateDirectory(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string HelperFolder => AppContext.BaseDirectory.TrimEnd('\\');

    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder must not fail the test.
        }
    }

    [Fact]
    public async Task A_verified_helper_starts_and_inspects_a_file()
    {
        var path = Write("a.bin");
        var client = Client();
        await using var session = await client.StartAsync(Ct);

        var response = await session.SendAsync(new HelperRequest(HelperProtocol.Version, 5, HelperCommand.InspectPath, Target(path)), Ct);

        Assert.Equal(HelperStatus.Ok, response.Status);
        Assert.Equal(5, response.RequestId);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), response.ContentHash);
    }

    [Fact]
    public async Task The_helper_deletes_exactly_the_item_it_was_told_about()
    {
        var path = Write("a.bin");
        await using var session = await Client().StartAsync(Ct);

        var response = await session.SendAsync(Operation(Target(path), ApprovedAction.Delete), Ct);

        Assert.Equal(HelperStatus.Ok, response.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_file_that_isnt_the_one_reviewed_is_left_alone()
    {
        var path = Write("a.bin");
        var target = Target(path);
        File.WriteAllBytes(path, [1, 2, 3]);
        await using var session = await Client().StartAsync(Ct);

        var response = await session.SendAsync(Operation(target, ApprovedAction.Delete), Ct);

        Assert.Equal(HelperStatus.TargetChanged, response.Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_wrong_identity_is_left_alone_even_with_the_right_path()
    {
        var path = Write("a.bin");
        await using var session = await Client().StartAsync(Ct);

        var response = await session.SendAsync(Operation(Target(path) with { FileId = new string('a', 32) }, ApprovedAction.Delete), Ct);

        Assert.Equal(HelperStatus.TargetChanged, response.Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_protected_path_is_refused_by_the_helper_itself()
    {
        await using var session = await Client().StartAsync(Ct);
        var target = Target(Write("a.bin")) with { RelativePath = @"\Windows\System32\kernel32.dll" };

        var response = await session.SendAsync(Operation(target, ApprovedAction.Delete), Ct);

        Assert.Equal(HelperStatus.Refused, response.Status);
    }

    [Fact]
    public async Task The_helper_moves_into_quarantine_only_through_its_slot_and_never_replaces()
    {
        var path = Write("a.bin");
        var target = Target(path);
        var slot = TargetTree.QuarantineSlot(ParentRelative(path), Guid.NewGuid(), 1, "a.bin");
        await using var session = await Client().StartAsync(Ct);

        // A quarantine folder anywhere but the top of the drive isn't a slot, so even a real one is refused.
        var response = await session.SendAsync(Operation(target, ApprovedAction.MoveToQuarantine, slot), Ct);

        Assert.Equal(HelperStatus.Refused, response.Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_folder_with_a_link_inside_is_refused()
    {
        var folder = Path.Combine(_folder, "folder");
        var outside = Path.Combine(_folder, "outside");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "precious.bin"), [1]);
        Run("cmd", "/c", "mklink", "/J", Path.Combine(folder, "link"), outside);
        await using var session = await Client().StartAsync(Ct);

        var response = await session.SendAsync(Operation(Target(folder), ApprovedAction.Delete), Ct);

        Assert.Equal(HelperStatus.Refused, response.Status);
        Assert.True(File.Exists(Path.Combine(outside, "precious.bin")));
        Directory.Delete(Path.Combine(folder, "link"));
    }

    [Fact]
    public async Task A_helper_that_doesnt_match_the_embedded_digest_is_never_started()
    {
        var client = new ElevatedHelperClient(new HelperLaunchOptions(HelperFolder, new string('0', 64), Verb: string.Empty));

        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => client.StartAsync(Ct));

        Assert.Contains("shipped with", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_build_with_no_digest_refuses_to_start_the_helper(string? digest)
    {
        var client = new ElevatedHelperClient(new HelperLaunchOptions(HelperFolder, digest, Verb: string.Empty));

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.StartAsync(Ct));
    }

    [Fact]
    public async Task A_missing_helper_is_reported()
    {
        var client = new ElevatedHelperClient(new HelperLaunchOptions(_folder, new string('0', 64), Verb: string.Empty));

        await Assert.ThrowsAsync<HelperUnavailableException>(() => client.StartAsync(Ct));
    }

    [Fact]
    public async Task A_client_that_isnt_the_expected_process_is_turned_away()
    {
        var name = PipeName();

        // The helper expects another process, so this one is the impostor.
        using var helper = Start(name, Environment.ProcessId + 1);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, Ct);

        await helper.WaitForExitAsync(Ct);

        Assert.Equal(HelperServer.Refused, helper.ExitCode);
    }

    [Fact]
    public async Task A_pipe_name_already_taken_means_nothing_is_served()
    {
        var name = PipeName();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1);

        using var helper = Start(name, Environment.ProcessId);
        await helper.WaitForExitAsync(Ct);

        Assert.Equal(HelperServer.Refused, helper.ExitCode);
    }

    [Theory]
    [InlineData]
    [InlineData("--pipe", "FreeAll.Helper.0123456789abcdef0123456789abcdef")]
    [InlineData("--pipe", "other", "--client", "1", "--sid", "S-1-5-21")]
    [InlineData("--pipe", "FreeAll.Helper.0123456789abcdef0123456789abcdef", "--client", "x", "--sid", "S-1-5-21")]
    [InlineData("--pipe", "FreeAll.Helper.0123456789abcdef0123456789abcdef", "--client", "1", "--sid", "nobody")]
    [InlineData("--pipe", @"..\evil.0123456789abcdef0123456789abcd", "--client", "1", "--sid", "S-1-5-21")]
    public void Arguments_that_arent_exact_stop_the_helper(params string[] arguments) => Assert.Null(HelperArguments.Parse(arguments));

    [Fact]
    public async Task The_pipe_is_open_only_to_the_expected_user()
    {
        var name = PipeName();
        using var helper = Start(name, Environment.ProcessId);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, Ct);

        var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.All(rules, r => Assert.Equal(Sid, r.IdentityReference.Value));
    }

    [Fact]
    public async Task An_oversized_message_ends_the_session()
    {
        await using var session = await RawSessionAsync();

        await session.Pipe.WriteAsync(BitConverter.GetBytes(HelperProtocol.MaxMessageBytes + 1), Ct);
        await session.Pipe.FlushAsync(Ct);
        await session.Helper.WaitForExitAsync(Ct);

        Assert.Equal(0, session.Helper.ExitCode);
    }

    [Fact]
    public async Task Malformed_json_is_refused_and_ends_the_session()
    {
        await using var session = await RawSessionAsync();

        await HelperProtocol.WriteAsync(session.Pipe, "{not json"u8.ToArray(), Ct);
        var bytes = await HelperProtocol.ReadAsync(session.Pipe, Ct);

        Assert.Equal(HelperStatus.Refused, HelperProtocol.Deserialize<HelperResponse>(bytes!)!.Status);
        await session.Helper.WaitForExitAsync(Ct);
    }

    [Fact]
    public async Task A_mismatched_protocol_version_is_refused_and_ends_the_session()
    {
        await using var session = await RawSessionAsync();

        await HelperProtocol.WriteAsync(session.Pipe, HelperProtocol.Serialize(new HelperRequest(99, 1, HelperCommand.Hello)), Ct);
        var bytes = await HelperProtocol.ReadAsync(session.Pipe, Ct);

        Assert.Equal(HelperStatus.Refused, HelperProtocol.Deserialize<HelperResponse>(bytes!)!.Status);
        await session.Helper.WaitForExitAsync(Ct);
    }

    [Fact]
    public async Task The_helper_exits_when_the_client_goes_away()
    {
        var session = await RawSessionAsync();
        var helper = session.Helper;

        await session.Pipe.DisposeAsync();
        await helper.WaitForExitAsync(Ct);

        Assert.True(helper.HasExited);
        helper.Dispose();
    }

    [Fact]
    public async Task A_killed_helper_fails_closed_for_the_caller()
    {
        var path = Write("a.bin");
        var running = Process.GetProcessesByName("FreeAll.Helper").Select(p => p.Id).ToHashSet();
        var session = await Client().StartAsync(Ct);
        foreach (var process in Process.GetProcessesByName("FreeAll.Helper").Where(p => !running.Contains(p.Id)))
        {
            process.Kill();
            await process.WaitForExitAsync(Ct);
        }

        await Assert.ThrowsAsync<HelperUnavailableException>(() => session.SendAsync(Operation(Target(path), ApprovedAction.Delete), Ct));

        Assert.True(File.Exists(path));
        await session.DisposeAsync();
    }

    private static HelperRequest Operation(HelperTarget target, ApprovedAction action, string? destination = null) =>
        new(HelperProtocol.Version, 9, HelperCommand.ExecuteApprovedOperation, target, action, destination);

    private static string PipeName() => "FreeAll.Helper." + Guid.NewGuid().ToString("N");

    private static ElevatedHelperClient Client() => new(new HelperLaunchOptions(HelperFolder, ElevatedHelperClient.ComputeDigest(HelperFolder), Verb: string.Empty));

    private static Process Start(string pipe, int client)
    {
        var start = new ProcessStartInfo(Path.Combine(HelperFolder, "FreeAll.Helper.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--pipe", pipe, "--client", client.ToString(System.Globalization.CultureInfo.InvariantCulture), "--sid", Sid })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
    }

    private static async Task<RawSession> RawSessionAsync()
    {
        var name = PipeName();
        var helper = Start(name, Environment.ProcessId);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, Ct);
        return new RawSession(pipe, helper);
    }

    private static (VolumeId Volume, string Relative) Resolve(string path)
    {
        Assert.True(Resolver.TryResolve(path, out var volume, out var relative));
        return (volume, relative);
    }

    private static string ParentRelative(string path) => Resolve(Path.GetDirectoryName(path)!).Relative;

    private static HelperTarget Target(string path)
    {
        var (volume, relative) = Resolve(path);
        Assert.Equal(TargetOpenStatus.Opened, SafeTarget.Open(volume, relative, false, out var target));
        using (target)
        {
            var state = target!.State;
            return new HelperTarget(volume.VolumeGuid, relative, state.IsDirectory, SafeTarget.FormatFileId(state.FileId!.Value), state.ModifiedTicks, state.Size);
        }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed record RawSession(NamedPipeClientStream Pipe, Process Helper) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync();
            if (!Helper.HasExited)
            {
                await Helper.WaitForExitAsync(CancellationToken.None);
            }

            Helper.Dispose();
        }
    }
}

/// <summary>Resolves a path to its volume through the mount manager, without the scanner.</summary>
internal static partial class Resolver
{
    public static bool TryResolve(string path, out VolumeId volume, out string relative)
    {
        var root = Path.GetPathRoot(path)!;
        var buffer = new char[64];
        var ok = GetVolumeNameForVolumeMountPoint(root, buffer, (uint)buffer.Length);
        volume = ok ? VolumeId.Parse(new string(buffer).TrimEnd('\0')) : default;
        relative = @"\" + path[root.Length..];
        return ok;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [System.Runtime.InteropServices.Out] char[] name, uint length);
}
