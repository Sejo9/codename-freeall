using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using FreeAll.Application.Cleanup;
using FreeAll.Core.Cleanup;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Cleanup.Elevation;

/// <summary>What the helper must be before it is started (DEC-025).</summary>
/// <param name="Folder">The folder holding it.</param>
/// <param name="ExpectedSha256">SHA-256 of its executable and library as built, embedded at publish. Null when this build has none.</param>
/// <param name="Verb">How the shell starts it: "runas" asks UAC. Tests leave it empty, which is the same launch without elevation.</param>
public sealed record HelperLaunchOptions(string Folder, string? ExpectedSha256, string Verb = "runas");

/// <summary>
/// Starts the helper through UAC only when asked, after checking it is the build this app shipped with, and
/// connects to a pipe with a name made up for this session. The pipe's server must be the process just
/// started, and the helper can't act as this user: the connection allows identification only.
/// </summary>
public sealed partial class ElevatedHelperClient : IElevatedHelper
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly HelperLaunchOptions _options;

    public ElevatedHelperClient(HelperLaunchOptions options) => _options = options;

    /// <summary>The digest compared at launch: the executable, then the library that holds the code.</summary>
    public static string ComputeDigest(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "FreeAll.Helper.exe", "FreeAll.Helper.dll" })
        {
            hash.AppendData(File.ReadAllBytes(Path.Combine(folder, name)));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<IElevatedSession> StartAsync(CancellationToken cancellationToken)
    {
        Verify();

        var pipeName = "FreeAll.Helper." + Guid.NewGuid().ToString("N");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var start = new ProcessStartInfo(Path.Combine(_options.Folder, "FreeAll.Helper.exe"))
        {
            UseShellExecute = true,
            Verb = _options.Verb,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[] { "--pipe", pipeName, "--client", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--sid", sid })
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new HelperUnavailableException("The administrator helper didn't start.");
        }
        catch (Win32Exception ex)
        {
            throw new HelperUnavailableException(ex.NativeErrorCode == 1223 ? "Administrator approval was declined, so nothing was changed." : "The administrator helper couldn't be started.", ex);
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != (uint)process.Id)
            {
                throw new HelperUnavailableException("Something other than the administrator helper answered, so it wasn't used.");
            }

            var session = new Session(pipe, process);
            var hello = await session.SendAsync(new HelperRequest(HelperProtocol.Version, 0, HelperCommand.Hello), cancellationToken).ConfigureAwait(false);
            return hello.Status == HelperStatus.Ok ? session : throw new HelperUnavailableException(hello.Message ?? "The administrator helper refused the session.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw new HelperUnavailableException("The administrator helper couldn't be reached.", ex);
        }
        catch (HelperUnavailableException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    private void Verify()
    {
        if (_options.ExpectedSha256 is not { Length: 64 } expected)
        {
            throw new HelperUnavailableException("This build of FreeAll has no verified administrator helper, so it can't be used.");
        }

        string actual;
        try
        {
            actual = ComputeDigest(_options.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HelperUnavailableException("The administrator helper is missing.", ex);
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new HelperUnavailableException("The administrator helper isn't the one this version of FreeAll shipped with, so it wasn't started.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    private sealed class Session(NamedPipeClientStream pipe, Process process) : IElevatedSession
    {
        public async Task<HelperResponse> SendAsync(HelperRequest request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                await HelperProtocol.WriteAsync(pipe, HelperProtocol.Serialize(request), timeout.Token).ConfigureAwait(false);
                var bytes = await HelperProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                var response = bytes is null ? null : HelperProtocol.Deserialize<HelperResponse>(bytes);
                return response is not null && response.Version == HelperProtocol.Version && response.RequestId == request.RequestId
                    ? response
                    : throw new HelperUnavailableException("The administrator helper stopped answering.");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                throw new HelperUnavailableException("The administrator helper stopped answering.", ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await SendAsync(new HelperRequest(HelperProtocol.Version, long.MaxValue, HelperCommand.Shutdown), CancellationToken.None).ConfigureAwait(false);
            }
            catch (HelperUnavailableException)
            {
                // Already gone; it also exits by itself when the pipe closes.
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
        }
    }
}
