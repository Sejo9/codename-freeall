using FreeAll.Core.Classification;

namespace FreeAll.Scanner.Enumeration;

/// <summary>
/// Per-worker cache of lowercase extensions, so a million <c>.dll</c> files share one string.
/// Bounded; once full, new extensions are allocated without being cached.
/// </summary>
internal sealed class ExtensionCache
{
    private const int MaxEntries = 4096;
    private const int MaxCachedLength = 32;

    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public ExtensionCache() => _lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();

    public string Get(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return string.Empty;
        }

        var raw = fileName[(dot + 1)..];
        if (raw.Length > MaxCachedLength)
        {
            return FileClassifier.ExtensionOf(fileName);
        }

        Span<char> lower = stackalloc char[raw.Length];
        raw.ToLowerInvariant(lower);

        if (_lookup.TryGetValue(lower, out var cached))
        {
            return cached;
        }

        var extension = lower.ToString();
        if (_entries.Count < MaxEntries)
        {
            _entries[extension] = extension;
        }

        return extension;
    }
}
