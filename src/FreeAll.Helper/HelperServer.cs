using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FreeAll.Cleanup.Files;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Volumes;
using Microsoft.Win32.SafeHandles;

namespace FreeAll.Helper;

/// <summary>Decides whether the process on the other end of the pipe is the one this session is for.</summary>
internal sealed partial class ClientVerifier
{
    private readonly int _processId;
    private readonly string _folder;

    public ClientVerifier(int processId, string folder)
    {
        _processId = processId;
        _folder = folder;
    }

    public static ClientVerifier Production(HelperArguments arguments) => new(arguments.ClientProcessId, AppContext.BaseDirectory);

    /// <summary>True only for the expected process, running from the folder the helper itself was installed in.</summary>
    public bool Accepts(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out var clientId) || clientId != (uint)_processId)
        {
            return false;
        }

        try
        {
            using var client = Process.GetProcessById(_processId);
            var image = client.MainModule?.FileName;
            return image is not null && string.Equals(Path.GetDirectoryName(image), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

/// <summary>
/// Serves one verified client (docs/08-security-privacy.md §4). The pipe is new, opened only to the client's
/// user, and refused if the name is taken. Every request is validated here without trusting the client, the
/// target is checked on the handle that changes it, and an idle or vanished client ends the session.
/// </summary>
internal sealed class HelperServer
{
    public const int Refused = 3;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HelperArguments _arguments;
    private readonly ClientVerifier _verifier;

    public HelperServer(HelperArguments arguments, ClientVerifier verifier)
    {
        _arguments = arguments;
        _verifier = verifier;
    }

    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe();
        }
        catch (IOException)
        {
            return Refused; // The name is taken: someone got there first, so nothing is served.
        }

        await using (pipe.ConfigureAwait(false))
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Refused;
            }

            if (!_verifier.Accepts(pipe.SafePipeHandle))
            {
                return Refused;
            }

            await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    /// <summary>Handles one request. Separate from the pipe so its rules can be tested directly.</summary>
    internal static HelperResponse Handle(HelperRequest? request, CancellationToken cancellationToken)
    {
        var id = request?.RequestId ?? 0;
        if (HelperRequestValidator.Validate(request) is { } problem)
        {
            return new HelperResponse(HelperProtocol.Version, id, HelperStatus.Refused, 0, problem);
        }

        try
        {
            return request!.Command switch
            {
                HelperCommand.Hello or HelperCommand.Shutdown => new HelperResponse(HelperProtocol.Version, id, HelperStatus.Ok),
                HelperCommand.InspectPath => Inspect(request, cancellationToken),
                _ => Execute(request, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HelperResponse(HelperProtocol.Version, id, HelperStatus.Failed, ex.HResult, "It couldn't be read or changed.");
        }
    }

    private static HelperResponse Inspect(HelperRequest request, CancellationToken cancellationToken)
    {
        var target = request.Target!;
        var volume = VolumeId.FromGuid(target.VolumeGuid);
        var status = SafeTarget.Open(volume, target.RelativePath, forChange: false, out var opened);
        if (status != TargetOpenStatus.Opened)
        {
            return Failure(request, status);
        }

        using (opened)
        {
            var state = opened!.State;
            if (!IsOrdinary(state))
            {
                return new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Refused, 0, "It is a link or another special item.");
            }

            var digest = TargetTree.Digest(SafeTarget.NativePath(volume, target.RelativePath), state.IsDirectory, cancellationToken);
            return new HelperResponse(
                HelperProtocol.Version,
                request.RequestId,
                HelperStatus.Ok,
                0,
                null,
                Convert.ToHexString(digest),
                state.FileId is { } id ? SafeTarget.FormatFileId(id) : null);
        }
    }

    private static HelperResponse Execute(HelperRequest request, CancellationToken cancellationToken)
    {
        var target = request.Target!;
        var volume = VolumeId.FromGuid(target.VolumeGuid);
        var status = SafeTarget.Open(volume, target.RelativePath, forChange: true, out var opened);
        if (status != TargetOpenStatus.Opened)
        {
            return Failure(request, status);
        }

        using (opened)
        {
            var state = opened!.State;
            if (!Matches(target, state))
            {
                return new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.TargetChanged, 0, "It isn't the item that was reviewed, so it was left alone.");
            }

            if (!IsOrdinary(state))
            {
                return new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Refused, 0, "It is a link or another special item.");
            }

            if (state.IsDirectory && TargetTree.Summarize(volume, target.RelativePath, countEachFileOnce: false, cancellationToken) is { HasReparsePoints: true } or { HasUnreadableParts: true })
            {
                return new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Refused, 0, "It contains a link or something that can't be read.");
            }

            var error = request.Action switch
            {
                ApprovedAction.Delete => state.IsDirectory ? TargetTree.Remove(volume, target.RelativePath, cancellationToken) : opened.DeleteNow(),
                _ => MoveWithin(opened, volume, request.DestinationPath!, request.Action == ApprovedAction.MoveToQuarantine),
            };
            return error == 0
                ? new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Ok)
                : new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Failed, error, "Windows refused the change.");
        }
    }

    private static int MoveWithin(SafeTarget target, VolumeId volume, string destination, bool intoQuarantine)
    {
        var native = SafeTarget.NativePath(volume, destination);
        if (intoQuarantine)
        {
            var slot = Path.GetDirectoryName(native)!;
            TargetTree.EnsureQuarantineFolder(Path.GetDirectoryName(Path.GetDirectoryName(slot)!)!);
            Directory.CreateDirectory(slot);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        }

        return target.MoveTo(volume, destination);
    }

    /// <summary>A path alone is never enough: it must still be the file that was reviewed.</summary>
    private static bool Matches(HelperTarget target, in TargetState state)
    {
        if (SafeTarget.ParseFileId(target.FileId) is not { } id || state.FileId != id || state.IsDirectory != target.IsFolder)
        {
            return false;
        }

        return target.IsFolder || (state.Size == target.Size && state.ModifiedTicks == target.ModifiedTicks);
    }

    private static bool IsOrdinary(in TargetState state) =>
        state.ReparseTag == 0 || ((state.ReparseTag & 0x20000000) == 0 && (state.ReparseTag == 0x80000017 || (state.ReparseTag & 0xFFFF0FFF) == 0x9000001A));

    private static HelperResponse Failure(HelperRequest request, TargetOpenStatus status) => status switch
    {
        TargetOpenStatus.Redirected => new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.TargetChanged, 0, "Its path now leads somewhere else, so it was left alone."),
        TargetOpenStatus.Missing => new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.TargetChanged, 2, "It no longer exists."),
        TargetOpenStatus.InUse => new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Failed, 32, "Another program is using it."),
        TargetOpenStatus.AccessDenied => new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Failed, 5, "Windows refused access even with administrator approval."),
        _ => new HelperResponse(HelperProtocol.Version, request.RequestId, HelperStatus.Failed, 1, "It couldn't be opened."),
    };

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_arguments.ClientSid), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        // One instance: creating it fails if the name exists, so nothing can sit in front of this pipe.
        return NamedPipeServerStreamAcl.Create(_arguments.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (pipe.IsConnected)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(IdleTimeout);
            byte[]? bytes;
            try
            {
                bytes = await HelperProtocol.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                return;
            }

            if (bytes is null)
            {
                return; // Oversized or cut short: not this protocol, so the session ends.
            }

            var request = HelperProtocol.Deserialize<HelperRequest>(bytes);
            var response = Handle(request, cancellationToken);
            try
            {
                await HelperProtocol.WriteAsync(pipe, HelperProtocol.Serialize(response), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }

            if (request is null || response.Status == HelperStatus.Refused && request.Version != HelperProtocol.Version || request.Command == HelperCommand.Shutdown)
            {
                return;
            }
        }
    }
}
