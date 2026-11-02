using System.Diagnostics.CodeAnalysis;

namespace FreeAll.Core.Volumes;

/// <summary>
/// Stable volume identity based on the Windows volume GUID path (<c>\\?\Volume{GUID}\</c>).
/// Drive letters are mount attributes and must never be used as the persistent key
/// (docs/04-functional-specification.md §3).
/// </summary>
public readonly record struct VolumeId
{
    private const string Prefix = @"\\?\Volume{";
    private const string Suffix = @"}\";

    private VolumeId(Guid volumeGuid) => VolumeGuid = volumeGuid;

    public Guid VolumeGuid { get; }

    /// <summary>The canonical volume GUID path, e.g. <c>\\?\Volume{0a1b...}\</c>.</summary>
    public string Path => Prefix + VolumeGuid.ToString("D") + Suffix;

    public static VolumeId FromGuid(Guid volumeGuid)
    {
        if (volumeGuid == Guid.Empty)
        {
            throw new ArgumentException("A volume GUID cannot be empty.", nameof(volumeGuid));
        }

        return new VolumeId(volumeGuid);
    }

    /// <summary>Parses a volume GUID path. The trailing backslash is optional; comparison is case-insensitive.</summary>
    public static bool TryParse([NotNullWhen(true)] string? volumePath, out VolumeId volumeId)
    {
        volumeId = default;
        if (string.IsNullOrEmpty(volumePath) || !volumePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = volumePath.AsSpan(Prefix.Length);
        var close = rest.IndexOf('}');
        if (close < 0)
        {
            return false;
        }

        var tail = rest[(close + 1)..];
        if (!(tail.IsEmpty || tail is "\\"))
        {
            return false;
        }

        if (!Guid.TryParseExact(rest[..close], "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        volumeId = new VolumeId(parsed);
        return true;
    }

    public static VolumeId Parse(string volumePath) =>
        TryParse(volumePath, out var id)
            ? id
            : throw new FormatException("The value is not a volume GUID path.");

    public override string ToString() => Path;
}
