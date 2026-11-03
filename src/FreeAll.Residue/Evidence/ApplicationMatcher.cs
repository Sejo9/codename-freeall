using System.Text;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Residue;

namespace FreeAll.Residue.Evidence;

public enum MatchKind
{
    None,

    /// <summary>The folder name is too generic ("Data", "Logs") to attribute to anyone.</summary>
    Generic,

    /// <summary>The folder's publisher still has installed applications, but no product name matches.</summary>
    Vendor,

    /// <summary>An application's name matches the folder name.</summary>
    Product,

    /// <summary>The folder is, holds or is inside an application's install folder.</summary>
    InstallFolder,
}

/// <param name="Kind">How the folder matched.</param>
/// <param name="Application">The application matched, when one did.</param>
public sealed record ApplicationMatch(MatchKind Kind, KnownApplication? Application)
{
    public static ApplicationMatch None { get; } = new(MatchKind.None, null);

    public static ApplicationMatch Generic { get; } = new(MatchKind.Generic, null);
}

/// <summary>
/// Attributes folders to applications by install folder, product name and publisher (TD-034). Matching
/// is deliberately generous: a folder that might belong to an installed application is treated as
/// belonging to it, because a missed leftover costs only some space while a false claim could lead
/// someone to remove a program's data. Names are compared without case, spaces or punctuation, and
/// company suffixes ("Inc.", "Corporation", "GmbH" …) are ignored.
/// </summary>
public sealed class ApplicationMatcher
{
    /// <summary>Names that say nothing about who owns a folder.</summary>
    public static readonly IReadOnlySet<string> GenericNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "app", "apps", "application", "applications", "backup", "backups", "bin", "cache", "caches", "common", "config", "configuration",
        "data", "default", "downloads", "documents", "files", "lib", "local", "localstate", "log", "logs", "plugins", "program", "programs",
        "roaming", "runtime", "service", "services", "settings", "setup", "shared", "software", "storage", "system", "temp", "tmp", "tools",
        "update", "updater", "updates", "user", "userdata", "users",
    };

    private static readonly string[] CompanySuffixes =
    [
        "incorporated", "inc", "corporation", "corp", "company", "co", "limited", "ltd", "llc", "gmbh", "ag", "sa", "srl", "bv", "pty", "plc",
        "software", "technologies", "technology", "systems", "studios", "games", "the",
    ];

    private readonly List<Entry> _installed = [];
    private readonly List<Entry> _former = [];

    public ApplicationMatcher(IEnumerable<KnownApplication> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);
        foreach (var known in applications)
        {
            (known.IsInstalled ? _installed : _former).Add(new Entry(known));
        }
    }

    /// <summary>Installed Store package families, lower-case.</summary>
    public IReadOnlySet<string> InstalledFamilies => _installed.Where(e => e.Known.Application.Source == ApplicationSource.Msix).Select(e => e.Known.Application.AppKey.ToLowerInvariant()).ToHashSet();

    /// <summary>Store package families FreeAll saw installed before, lower-case.</summary>
    public IReadOnlyDictionary<string, KnownApplication> FormerFamilies => _former
        .Where(e => e.Known.Application.Source == ApplicationSource.Msix)
        .GroupBy(e => e.Known.Application.AppKey.ToLowerInvariant())
        .ToDictionary(g => g.Key, g => g.First().Known);

    /// <summary>Matches a folder against installed applications.</summary>
    /// <param name="folderName">The folder's own name.</param>
    /// <param name="fullPath">Its full path as Windows shows it (for install-folder matching).</param>
    public ApplicationMatch MatchInstalled(string folderName, string fullPath) => Match(_installed, folderName, fullPath, allowVendor: true);

    /// <summary>Matches a folder against applications that were installed before and aren't any more (by name only).</summary>
    public ApplicationMatch MatchFormer(string folderName, string fullPath) => Match(_former, folderName, fullPath, allowVendor: false);

    /// <summary>Lower-case letters and digits only.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>Lower-case words of letters and digits, without version numbers.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0 && !current.ToString().All(char.IsDigit))
            {
                words.Add(current.ToString());
            }

            current.Clear();
        }

        return words;
    }

    /// <summary>A publisher without company suffixes, normalized ("Adobe Systems Incorporated" → "adobe").</summary>
    public static string NormalizePublisher(string? publisher)
    {
        var words = Words(publisher).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Concat(words);
    }

    private static ApplicationMatch Match(List<Entry> entries, string folderName, string fullPath, bool allowVendor)
    {
        var folder = Normalize(folderName);
        if (folder.Length == 0)
        {
            return ApplicationMatch.None;
        }

        foreach (var entry in entries)
        {
            if (entry.InstallPath is { } install && SameOrNested(fullPath, install))
            {
                return new ApplicationMatch(MatchKind.InstallFolder, entry.Known);
            }
        }

        if (GenericNames.Contains(folder))
        {
            return ApplicationMatch.Generic;
        }

        // Order matters: an exact product name, then the publisher (a vendor folder is looked into, so a
        // leftover product inside it can still be found), then looser product-name matches.
        foreach (var entry in entries)
        {
            if (entry.Names.Contains(folder))
            {
                return new ApplicationMatch(MatchKind.Product, entry.Known);
            }
        }

        if (allowVendor)
        {
            foreach (var entry in entries)
            {
                if (entry.Publisher.Length >= 2 && (entry.Publisher == folder || (folder.Length >= 4 && entry.Publisher.StartsWith(folder, StringComparison.Ordinal))))
                {
                    return new ApplicationMatch(MatchKind.Vendor, entry.Known);
                }
            }
        }

        var folderWords = Words(folderName);
        foreach (var entry in entries)
        {
            if (entry.MatchesProduct(folder, folderWords))
            {
                return new ApplicationMatch(MatchKind.Product, entry.Known);
            }
        }

        // Node.js tools keep their data in "<tool>-nodejs" folders (the env-paths convention).
        if (folderName.EndsWith("-nodejs", StringComparison.OrdinalIgnoreCase) && entries.FirstOrDefault(e => e.Names.Contains("nodejs")) is { } node)
        {
            return new ApplicationMatch(MatchKind.Product, node.Known);
        }

        // "com.adobe.dunamis", "info.contoso.taskmaster": the product is the last part, the vendor the one after the domain.
        if (ReverseDomainParts(folderName) is { } parts)
        {
            var product = Match(entries, parts[^1], fullPath, allowVendor: false);
            if (product.Kind is MatchKind.Product or MatchKind.InstallFolder)
            {
                return product;
            }

            var vendor = Normalize(parts[1]);
            if (vendor.Length >= 3 && entries.FirstOrDefault(e => e.Publisher == vendor || e.Names.Any(n => n.StartsWith(vendor, StringComparison.Ordinal))) is { } owner)
            {
                // The name states the vendor and holds no product folders to look into, so it counts as the vendor's.
                return new ApplicationMatch(MatchKind.Product, owner.Known);
            }
        }

        return ApplicationMatch.None;
    }

    private static readonly HashSet<string> TopLevelDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "io", "info", "ai", "dev", "app", "co", "me", "de", "uk", "fr", "es", "nl", "eu", "us", "tv",
    };

    /// <summary>The dot-separated parts of a reverse domain name such as "com.contoso.app", or null.</summary>
    private static string[]? ReverseDomainParts(string folderName)
    {
        var parts = folderName.Split('.');
        return parts.Length >= 3 && TopLevelDomains.Contains(parts[0]) && parts.All(p => p.Length > 0) ? parts : null;
    }

    private static bool SameOrNested(string path, string other) =>
        path.Equals(other, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase)
        || other.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public Entry(KnownApplication known)
        {
            Known = known;
            var app = known.Application;
            Names = [Normalize(app.Name)];
            NameWords = Words(app.Name).ToHashSet(StringComparer.Ordinal);
            Publisher = NormalizePublisher(app.Publisher);
            if (Publisher.Length > 0 && Names[0].StartsWith(Publisher, StringComparison.Ordinal) && Names[0].Length > Publisher.Length)
            {
                Names.Add(Names[0][Publisher.Length..]); // "Mozilla Firefox" also matches "Firefox"
            }

            if (app.Source == ApplicationSource.Msix)
            {
                // "5319275A.WhatsAppDesktop" → "whatsappdesktop", "whatsapp"
                var packageName = app.AppKey.Split('_')[0];
                foreach (var piece in packageName.Split('.'))
                {
                    var normalized = Normalize(piece);
                    if (normalized.Length >= 4 && !normalized.All(char.IsAsciiHexDigit))
                    {
                        Names.Add(normalized);
                        NameWords.Add(normalized);
                    }
                }
            }
            else if (app.InstallLocation is { Length: > 3 } location)
            {
                InstallPath = location.TrimEnd('\\');
                var leaf = Path.GetFileName(InstallPath);
                if (Normalize(leaf) is { Length: >= 3 } leafName && !GenericNames.Contains(leafName))
                {
                    Names.Add(leafName);
                }
            }

            Names.RemoveAll(n => n.Length < 3);

            // "Windows Subsystem for Linux" → "wsl".
            var initials = Words(app.Name).Where(w => !AcronymSkips.Contains(w)).ToList();
            if (initials.Count is >= 3 and <= 6)
            {
                Acronym = string.Concat(initials.Select(w => w[0]));
            }
        }

        private static readonly HashSet<string> AcronymSkips = new(StringComparer.Ordinal) { "for", "of", "the", "and", "to", "on", "in", "x64", "x86" };

        public string? Acronym { get; }

        public KnownApplication Known { get; }

        public List<string> Names { get; }

        public HashSet<string> NameWords { get; }

        public string Publisher { get; }

        public string? InstallPath { get; }

        public bool MatchesProduct(string folder, IReadOnlyList<string> folderWords)
        {
            foreach (var name in Names)
            {
                if (name == folder
                    || (folder.Length >= 4 && name.StartsWith(folder, StringComparison.Ordinal))
                    || (name.Length >= 4 && folder.StartsWith(name, StringComparison.Ordinal))
                    || (folder.Length >= 6 && name.Contains(folder, StringComparison.Ordinal))) // "PCHealthCheck" in "Windows PC Health Check"
                {
                    return true;
                }
            }

            if (Acronym is not null && folder == Acronym)
            {
                return true;
            }

            // "Code" matches "Microsoft Visual Studio Code"; every word of the folder must be a word of the name.
            return folderWords.Count > 0 && folderWords.All(w => w.Length >= 3 && !GenericNames.Contains(w) && NameWords.Contains(w));
        }
    }
}
