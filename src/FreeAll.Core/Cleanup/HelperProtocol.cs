using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAll.Core.Volumes;

namespace FreeAll.Core.Cleanup;

/// <summary>The only things the elevated helper can be asked (docs/08-security-privacy.md §4). There is no generic file or shell command.</summary>
public enum HelperCommand
{
    Hello = 0,
    InspectPath = 1,
    ExecuteApprovedOperation = 2,
    Shutdown = 3,
}

/// <summary>What an approved operation does to its one target.</summary>
public enum ApprovedAction
{
    Delete = 0,
    MoveToQuarantine = 1,
    RestoreFromQuarantine = 2,
}

public enum HelperStatus
{
    Ok = 0,

    /// <summary>The request was malformed or isn't allowed. Nothing was touched.</summary>
    Refused = 1,

    /// <summary>The target is not what the request described. Nothing was touched.</summary>
    TargetChanged = 2,

    /// <summary>The operation was tried and failed.</summary>
    Failed = 3,
}

/// <summary>Exactly one item, with the identity it must still have. A path alone is never enough.</summary>
/// <param name="VolumeGuid">Volume.</param>
/// <param name="RelativePath">Volume-relative path.</param>
/// <param name="IsFolder">File or folder.</param>
/// <param name="FileId">File identity as 32 hex digits. Required for any change.</param>
/// <param name="ModifiedTicks">Last-write time (FILETIME). Required for a file; ignored for a folder.</param>
/// <param name="Size">Size of a file; ignored for a folder.</param>
public sealed record HelperTarget(Guid VolumeGuid, string RelativePath, bool IsFolder, string? FileId, long ModifiedTicks, long Size);

/// <param name="Version">Protocol version.</param>
/// <param name="RequestId">Echoed in the response.</param>
/// <param name="Command">What is asked.</param>
/// <param name="Target">The item, for inspecting and operations.</param>
/// <param name="Action">For an approved operation, what to do.</param>
/// <param name="DestinationPath">Volume-relative destination, for quarantine and restore. Always on the target's volume.</param>
public sealed record HelperRequest(int Version, long RequestId, HelperCommand Command, HelperTarget? Target = null, ApprovedAction? Action = null, string? DestinationPath = null);

/// <param name="Version">Protocol version.</param>
/// <param name="RequestId">The request answered.</param>
/// <param name="Status">Outcome.</param>
/// <param name="ErrorCode">Win32 error, or 0.</param>
/// <param name="Message">Plain-language detail.</param>
/// <param name="ContentHash">For a move to quarantine, the content digest as hex.</param>
/// <param name="FileId">For inspecting, the identity found.</param>
public sealed record HelperResponse(int Version, long RequestId, HelperStatus Status, int ErrorCode = 0, string? Message = null, string? ContentHash = null, string? FileId = null);

/// <summary>Framing and limits of the helper channel: a 4-byte length, then JSON.</summary>
public static class HelperProtocol
{
    public const int Version = 1;

    /// <summary>The largest message either side accepts. Paths are at most 32,767 characters.</summary>
    public const int MaxMessageBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        // Unknown fields are refused: a client that sends something else isn't speaking this protocol.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static byte[] Serialize<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    /// <returns>The message, or null when it isn't valid JSON of this shape.</returns>
    public static T? Deserialize<T>(ReadOnlySpan<byte> bytes)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes one length-prefixed message.</summary>
    public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(message.Length, MaxMessageBytes);

        await stream.WriteAsync(BitConverter.GetBytes(message.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message. An oversized or truncated message gives null, and the channel should then be closed.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaxMessageBytes)
        {
            return null;
        }

        var message = new byte[length];
        return await FillAsync(stream, message, cancellationToken).ConfigureAwait(false) ? message : null;
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }
}

/// <summary>
/// Checks a request on its own terms, in the helper, trusting nothing the client decided
/// (docs/08-security-privacy.md §4: validate every argument and policy independently).
/// </summary>
public static class HelperRequestValidator
{
    /// <returns>Null when the request may proceed; otherwise why it is refused.</returns>
    public static string? Validate(HelperRequest? request)
    {
        if (request is null)
        {
            return "The request couldn't be read.";
        }

        if (request.Version != HelperProtocol.Version)
        {
            return "The app and the helper are different versions.";
        }

        if (!Enum.IsDefined(request.Command))
        {
            return "That command isn't allowed.";
        }

        return request.Command switch
        {
            HelperCommand.Hello or HelperCommand.Shutdown => request.Target is null && request.Action is null && request.DestinationPath is null ? null : "This command takes no target.",
            HelperCommand.InspectPath => request.Action is null && request.DestinationPath is null ? CheckPath(request.Target) : "Inspecting takes only a target.",
            _ => CheckOperation(request),
        };
    }

    private static string? CheckPath(HelperTarget? target)
    {
        if (target is null)
        {
            return "A target is required.";
        }

        if (target.VolumeGuid == Guid.Empty)
        {
            return "The volume is missing.";
        }

        return VolumePath.TryNormalize(target.RelativePath, out var path) && string.Equals(path, target.RelativePath, StringComparison.Ordinal) && !VolumePath.IsRoot(path)
            ? null
            : "The path isn't valid.";
    }

    private static string? CheckOperation(HelperRequest request)
    {
        if (CheckPath(request.Target) is { } problem)
        {
            return problem;
        }

        if (request.Action is not { } action || !Enum.IsDefined(action))
        {
            return "That action isn't allowed.";
        }

        var target = request.Target!;
        if (target.FileId is not { Length: 32 } || !target.FileId.All(char.IsAsciiHexDigit))
        {
            return "An operation needs the item's identity; a path alone is never enough.";
        }

        return action switch
        {
            ApprovedAction.Delete => CheckDelete(request, target),
            ApprovedAction.MoveToQuarantine => CheckMoveToQuarantine(request, target),
            _ => CheckRestore(request, target),
        };
    }

    private static string? CheckDelete(HelperRequest request, HelperTarget target) =>
        request.DestinationPath is not null ? "Deleting takes no destination." : CheckAllowed(target.RelativePath);

    private static string? CheckMoveToQuarantine(HelperRequest request, HelperTarget target) =>
        CheckAllowed(target.RelativePath) ?? (IsQuarantineSlot(request.DestinationPath) ? null : "That isn't a place in quarantine.");

    private static string? CheckRestore(HelperRequest request, HelperTarget target)
    {
        if (!IsQuarantineSlot(target.RelativePath))
        {
            return "Only items in quarantine can be restored.";
        }

        return VolumePath.TryNormalize(request.DestinationPath, out var destination) && string.Equals(destination, request.DestinationPath, StringComparison.Ordinal)
            ? CheckAllowed(destination)
            : "The restore destination isn't valid.";
    }

    private static string? CheckAllowed(string relativePath) =>
        ProtectedPathPolicy.Evaluate(relativePath).Level == PolicyLevel.Blocked ? "That item is protected." : null;

    /// <summary><c>\FreeAll Quarantine\{32 hex}\{number}\{name}</c>, at the top of the volume and nowhere else.</summary>
    private static bool IsQuarantineSlot(string? path)
    {
        if (!VolumePath.TryNormalize(path, out var normalized) || !string.Equals(normalized, path, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = normalized[1..].Split('\\');
        return parts.Length == 4
            && parts[0].Equals(ProtectedPathPolicy.QuarantineFolderName, StringComparison.OrdinalIgnoreCase)
            && parts[1].Length == 32 && parts[1].All(char.IsAsciiHexDigit)
            && parts[2].Length is > 0 and <= 9 && parts[2].All(char.IsAsciiDigit);
    }
}
