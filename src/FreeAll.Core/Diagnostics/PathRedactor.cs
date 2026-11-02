using System.Security.Cryptography;
using System.Text;

namespace FreeAll.Core.Diagnostics;

/// <summary>
/// Redacts file-system paths before they are written to logs (docs/08-security-privacy.md §7).
/// A redacted path keeps its root and a short stable fingerprint so that repeated log entries about
/// the same path can be correlated locally without revealing file or folder names.
/// </summary>
public sealed class PathRedactor
{
    private const int FingerprintHexLength = 12;

    /// <param name="includeFullPaths">
    /// True only when the user has explicitly enabled detailed local troubleshooting logs.
    /// </param>
    public PathRedactor(bool includeFullPaths = false) => IncludeFullPaths = includeFullPaths;

    public bool IncludeFullPaths { get; }

    public string Redact(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "[no path]";
        }

        if (IncludeFullPaths)
        {
            return path;
        }

        var root = SafeRoot(path);
        var normalized = path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var fingerprint = Convert.ToHexStringLower(hash)[..FingerprintHexLength];

        return $"{root}[path:{fingerprint}]";
    }

    private static string SafeRoot(string path)
    {
        // Only a drive-letter root is kept. Volume GUID roots, UNC server/share names and
        // anything else are fully redacted because they can identify machines or users.
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            ? char.ToUpperInvariant(path[0]) + @":\"
            : string.Empty;
    }
}
