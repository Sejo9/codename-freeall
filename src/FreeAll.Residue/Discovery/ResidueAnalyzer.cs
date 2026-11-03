using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Formatting;
using FreeAll.Application.Scanning;
using FreeAll.Core.Residue;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Residue.Evidence;
using FreeAll.Residue.Inventory;

namespace FreeAll.Residue.Discovery;

/// <summary>Where a candidate folder is. Determines the default risk and the location evidence.</summary>
public enum ResidueLocation
{
    LocalAppData,
    RoamingAppData,
    LocalLowAppData,
    UserPrograms,
    PackageData,
    ProgramData,
    ProgramFiles,
    ProgramFilesX86,
}

/// <param name="Session">The scan of the Windows drive to analyse.</param>
/// <param name="ProfilePath">Volume-relative path of the current user's profile on that drive, or null.</param>
/// <param name="Applications">Every application FreeAll knows, installed now or before.</param>
/// <param name="Decisions">The user's residue decisions.</param>
/// <param name="NowUtc">Current time, for age evidence.</param>
/// <param name="PathFolders">The folders on the PATH (programs run from there count as installed).</param>
public sealed record ResidueContext(
    ScanSessionSummary Session,
    string? ProfilePath,
    IReadOnlyList<KnownApplication> Applications,
    IReadOnlyList<UserDecision> Decisions,
    DateTime NowUtc,
    IReadOnlyList<string>? PathFolders = null);

/// <summary>A folder that may be left over from an application that is no longer installed.</summary>
/// <param name="Folder">The folder as indexed (sizes, times, state).</param>
/// <param name="Location">Where it is.</param>
/// <param name="GroupKey">Application-level decision key (former application, package family or folder name).</param>
/// <param name="GroupName">What the group is called in the UI.</param>
/// <param name="Confidence">How strongly the evidence points to a removed application.</param>
/// <param name="Risk">What could be lost by removing it.</param>
/// <param name="Evidence">Every piece of evidence, for and against.</param>
/// <param name="Summary">One-line reason.</param>
/// <param name="Decision">The user's decision that applies to it, if any (path decisions win over group decisions).</param>
public sealed record ResidueCandidate(
    IndexEntry Folder,
    ResidueLocation Location,
    string GroupKey,
    string GroupName,
    ResidueConfidence Confidence,
    RemovalRisk Risk,
    IReadOnlyList<ResidueEvidence> Evidence,
    string Summary,
    UserDecision? Decision);

/// <param name="Candidates">Candidates, most confident and largest first.</param>
/// <param name="FoldersExamined">Folders looked at.</param>
/// <param name="AttributedToInstalled">Folders attributed to an installed application (not candidates).</param>
/// <param name="Skipped">Folders left out on purpose (Windows, shared, generic and hardware-vendor folders, sandbox profiles).</param>
/// <param name="InUse">Folders without an owner that changed recently, so are taken to be in use and not suggested.</param>
/// <param name="Notes">Limits of this analysis, in words.</param>
public sealed record ResidueReport(IReadOnlyList<ResidueCandidate> Candidates, int FoldersExamined, int AttributedToInstalled, int Skipped, int InUse, IReadOnlyList<string> Notes);

/// <summary>
/// Finds folders that may be left over from removed applications (F-023, TD-034), using the stored scan
/// and the application inventory only: nothing on disk is opened or changed. Looks one level into the
/// application data folders of the current user (Local, Roaming, LocalLow, Programs, Store package data),
/// ProgramData and the Program Files folders, and a second level under a vendor folder whose vendor still
/// has installed applications. Rules are versioned (<see cref="RulesVersion"/>).
/// </summary>
public sealed class ResidueAnalyzer
{
    public const int RulesVersion = 2;

    /// <summary>Unchanged for this long counts as evidence for residue.</summary>
    public static readonly TimeSpan OldAge = TimeSpan.FromDays(180);

    /// <summary>Changed within this long counts as evidence of use.</summary>
    public static readonly TimeSpan RecentAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Folders in application data that belong to Windows, are shared by many programs, or are covered by
    /// Temporary Files. They are never suggested.
    /// </summary>
    private static readonly HashSet<string> SharedAppData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Programs", "Temp", "Publishers", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps",
        "ElevatedDiagnostics", "PeerDistRepub", "PlaceholderTileLogoFolder", "VirtualStore", "IsolatedStorage", "Application Data",
        "History", "Temporary Internet Files", "PackageManagement", "SquirrelTemp", "npm-cache", "pip", "NuGet", "Yarn",
        "Microsoft_Corporation", "Sun", "fontconfig", "Downloaded Installations", "Package Cache", "CEF",
    };

    /// <summary>
    /// Hardware and driver vendors. Drivers and their helpers often install without an uninstall entry, so a missing entry
    /// says nothing about whether the hardware still uses them.
    /// </summary>
    private static readonly HashSet<string> HardwareVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Acer", "Alps", "AMD", "ASRock", "ASUS", "Broadcom", "Conexant", "Dell", "Dolby", "DTS", "Elan", "Fujitsu", "Gigabyte", "HP", "Hewlett-Packard",
        "Huawei", "Intel", "Killer Networking", "Lenovo", "Logitech", "MediaTek", "MSI", "NVIDIA", "NVIDIA Corporation", "Qualcomm", "Razer", "Realtek",
        "Rivet Networks", "Samsung", "SoundResearch", "Synaptics", "Toshiba", "Waves",
    };

    /// <summary>FreeAll's own data folder.</summary>
    private const string OwnFolder = "FreeAll";

    /// <summary>Trailing PATH segments that don't name a program ("…\nodejs\bin" is Node.js).</summary>
    private static readonly HashSet<string> PathSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "sbin", "cmd", "scripts", "links", "latest", "resources", "tools", "cmdline-tools", "platform-tools", "sdk", "x64", "x86",
    };

    private static readonly string[] ProfileRoots = [@"\AppData\Local\Programs", @"\AppData\Local", @"\AppData\Roaming", @"\AppData\LocalLow"];

    private static readonly HashSet<string> SharedProgramData = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Package Cache", "ssh", "USOPrivate", "USOShared", "SoftwareDistribution", "WindowsHolographicDevices",
        "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "Application Data", "Comms", "regid.1991-06.com.microsoft",
        "Windows App Certification Kit", "dbg", "Microsoft OneDrive", "boost_interprocess",
    };

    private static readonly HashSet<string> SharedProgramFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "WindowsApps", "ModifiableWindowsApps", "WindowsPowerShell", "Reference Assemblies", "MSBuild",
        "Uninstall Information", "dotnet", "Windows Kits", "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Sidebar", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Security",
        "Windows Multimedia Platform", "Microsoft.NET", "Microsoft SDKs", "Microsoft Update Health Tools", "PackageManagement",
    };

    private readonly IScanIndex _index;

    public ResidueAnalyzer(IScanIndex index) => _index = index;

    public async Task<ResidueReport> AnalyzeAsync(ResidueContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = new Run(context, new ApplicationMatcher(context.Applications.Concat(CommandPathApplications(context))));
        if (context.ProfilePath is { } profile)
        {
            await ExamineAppDataAsync(run, profile + @"\AppData\Local", ResidueLocation.LocalAppData, cancellationToken).ConfigureAwait(false);
            await ExamineAppDataAsync(run, profile + @"\AppData\Roaming", ResidueLocation.RoamingAppData, cancellationToken).ConfigureAwait(false);
            await ExamineAppDataAsync(run, profile + @"\AppData\LocalLow", ResidueLocation.LocalLowAppData, cancellationToken).ConfigureAwait(false);
            await ExamineChildrenAsync(run, profile + @"\AppData\Local\Programs", ResidueLocation.UserPrograms, SharedAppData, cancellationToken).ConfigureAwait(false);
            await ExaminePackagesAsync(run, profile + @"\AppData\Local\Packages", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            run.Notes.Add("Your user folder isn't in this scan, so leftovers in your application data weren't looked for.");
        }

        await ExamineChildrenAsync(run, @"\ProgramData", ResidueLocation.ProgramData, SharedProgramData, cancellationToken).ConfigureAwait(false);
        await ExamineChildrenAsync(run, @"\Program Files", ResidueLocation.ProgramFiles, SharedProgramFiles, cancellationToken).ConfigureAwait(false);
        await ExamineChildrenAsync(run, @"\Program Files (x86)", ResidueLocation.ProgramFilesX86, SharedProgramFiles, cancellationToken).ConfigureAwait(false);

        if (context.Session.ScannerVersion < 3)
        {
            run.Notes.Add("This scan was made before FreeAll recorded when folders last changed or whether they hold documents. Scan the drive again for that evidence.");
        }

        if (!context.Applications.Any(a => !a.IsInstalled))
        {
            run.Notes.Add("FreeAll hasn't yet seen an application being uninstalled, so it can't recognize leftovers by a former application's name. It can from now on.");
        }

        if (run.InUse > 0)
        {
            run.Notes.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} folders with no installed owner changed in the last {1:N0} days, so a program probably still uses them; they aren't suggested.",
                run.InUse,
                RecentAge.TotalDays));
        }

        run.Notes.Add("Other users' folders can't be read with your permissions and weren't examined.");

        var candidates = run.Candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.Folder.AllocatedSize)
            .ToList();
        return new ResidueReport(candidates, run.Examined, run.Attributed, run.Skipped, run.InUse, run.Notes);
    }

    private async Task ExamineAppDataAsync(Run run, string path, ResidueLocation location, CancellationToken cancellationToken) =>
        await ExamineChildrenAsync(run, path, location, SharedAppData, cancellationToken).ConfigureAwait(false);

    private async Task ExamineChildrenAsync(Run run, string path, ResidueLocation location, HashSet<string> shared, CancellationToken cancellationToken)
    {
        var parent = await _index.GetFolderByPathAsync(run.Context.Session.Id, path, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return;
        }

        foreach (var folder in await _index.GetSubfoldersAsync(run.Context.Session.Id, parent.DirectoryKey, cancellationToken).ConfigureAwait(false))
        {
            run.Examined++;
            if (shared.Contains(folder.Name)
                || HardwareVendors.Contains(folder.Name)
                || (location is not (ResidueLocation.ProgramFiles or ResidueLocation.ProgramFilesX86) && folder.Name.Equals(OwnFolder, StringComparison.OrdinalIgnoreCase))
                || IsWindowsComponent(folder.Name, location)
                || folder.FolderState is DirectoryScanState.ReparsePoint or DirectoryScanState.Excluded)
            {
                run.Skipped++;
                continue;
            }

            var fullPath = run.Context.Session.DisplayPath(folder.RelativePath);
            var match = run.Matcher.MatchInstalled(folder.Name, fullPath);
            switch (match.Kind)
            {
                case MatchKind.InstallFolder or MatchKind.Product:
                    run.Attributed++;
                    break;
                case MatchKind.Generic:
                    run.Skipped++;
                    break;
                case MatchKind.Vendor:
                    // The vendor still has software here; look at its product folders one level down.
                    run.Attributed++;
                    await ExamineVendorAsync(run, folder, location, match.Application!, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    AddCandidate(run, folder, location, Attribute(run, folder, fullPath));
                    break;
            }
        }
    }

    private async Task ExamineVendorAsync(Run run, IndexEntry vendor, ResidueLocation location, KnownApplication vendorApplication, CancellationToken cancellationToken)
    {
        if (vendor.FolderState != DirectoryScanState.Scanned)
        {
            return;
        }

        foreach (var product in await _index.GetSubfoldersAsync(run.Context.Session.Id, vendor.DirectoryKey, cancellationToken).ConfigureAwait(false))
        {
            run.Examined++;
            var fullPath = run.Context.Session.DisplayPath(product.RelativePath);
            var match = run.Matcher.MatchInstalled(product.Name, fullPath);
            if (match.Kind is MatchKind.InstallFolder or MatchKind.Product)
            {
                run.Attributed++;
                continue;
            }

            if (match.Kind == MatchKind.Generic || product.FolderState is DirectoryScanState.ReparsePoint or DirectoryScanState.Excluded)
            {
                // Generic names under a vendor folder ("Logs", "Shared") most likely belong to the vendor's installed software.
                run.Skipped++;
                continue;
            }

            var former = run.Matcher.MatchFormer(product.Name, fullPath);
            var attribution = former.Kind is MatchKind.Product or MatchKind.InstallFolder
                ? FormerAttribution(former.Application!)
                : new Attribution(
                    new ResidueEvidence(EvidenceKind.VendorInstalledProductNot, $"{Publisher(vendorApplication)} still has software installed (for example {vendorApplication.Application.Name}), but no installed program matches \"{product.Name}\".", true),
                    ResidueConfidence.Low,
                    "vendor:" + ApplicationMatcher.Normalize(vendor.Name) + "/" + ApplicationMatcher.Normalize(product.Name),
                    $"{vendor.Name} \\ {product.Name}");
            AddCandidate(run, product, location, attribution);
        }
    }

    private async Task ExaminePackagesAsync(Run run, string path, CancellationToken cancellationToken)
    {
        var parent = await _index.GetFolderByPathAsync(run.Context.Session.Id, path, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return;
        }

        var installed = run.Matcher.InstalledFamilies;
        var former = run.Matcher.FormerFamilies;
        foreach (var folder in await _index.GetSubfoldersAsync(run.Context.Session.Id, parent.DirectoryKey, cancellationToken).ConfigureAwait(false))
        {
            run.Examined++;
            var name = folder.Name.ToLowerInvariant();
            Attribution attribution;
            if (RegistryApplicationInventory.IsFamilyName(name))
            {
                if (installed.Contains(name))
                {
                    run.Attributed++;
                    continue;
                }

                attribution = former.TryGetValue(name, out var app)
                    ? FormerAttribution(app)
                    : new Attribution(
                        new ResidueEvidence(EvidenceKind.PackageNotInstalled, $"This is the data folder of the Store app package \"{folder.Name.Split('_')[0]}\", which isn't installed for you.", true),
                        ResidueConfidence.High,
                        "pkg:" + name,
                        folder.Name.Split('_')[0]);
            }
            else if (RegistryApplicationInventory.FamilyFromFullName(name) is { } family)
            {
                attribution = installed.Contains(family)
                    ? new Attribution(
                        new ResidueEvidence(EvidenceKind.PackageOlderVersion, $"This folder is named after version {folder.Name.Split('_')[1]} of \"{folder.Name.Split('_')[0]}\", which is now installed in another version.", true),
                        ResidueConfidence.Medium,
                        "pkg:" + family,
                        folder.Name.Split('_')[0])
                    : new Attribution(
                        new ResidueEvidence(EvidenceKind.PackageNotInstalled, $"This folder is named after the Store app package \"{folder.Name.Split('_')[0]}\", which isn't installed for you.", true),
                        ResidueConfidence.High,
                        "pkg:" + family,
                        folder.Name.Split('_')[0]);
            }
            else
            {
                // Sandbox profiles that desktop programs create here (Chrome "cr.sb.*", "*.protectedmode", …): not packages.
                run.Skipped++;
                continue;
            }

            AddCandidate(run, folder, ResidueLocation.PackageData, attribution);
        }
    }

    private static Attribution Attribute(Run run, IndexEntry folder, string fullPath)
    {
        var former = run.Matcher.MatchFormer(folder.Name, fullPath);
        return former.Kind is MatchKind.Product or MatchKind.InstallFolder
            ? FormerAttribution(former.Application!)
            : new Attribution(
                new ResidueEvidence(EvidenceKind.NoInstalledMatch, $"No installed program's name, publisher or install folder matches \"{folder.Name}\".", true),
                ResidueConfidence.Low,
                "name:" + ApplicationMatcher.Normalize(folder.Name),
                folder.Name);
    }

    private static Attribution FormerAttribution(KnownApplication app)
    {
        var culture = CultureInfo.CurrentCulture;
        return new Attribution(
            new ResidueEvidence(
                EvidenceKind.FormerlyInstalled,
                string.Format(culture, "FreeAll saw \"{0}\" installed until {1:d}; it isn't installed now.", app.Application.Name, app.LastSeenUtc.ToLocalTime()),
                true),
            ResidueConfidence.High,
            "app:" + app.Application.Source + ":" + app.Application.AppKey,
            app.Application.Name);
    }

    private static void AddCandidate(Run run, IndexEntry folder, ResidueLocation location, Attribution attribution)
    {
        var culture = CultureInfo.CurrentCulture;
        var now = run.Context.NowUtc;
        var evidence = new List<ResidueEvidence> { attribution.Evidence };
        var confidence = attribution.Confidence;
        var risk = BaseRisk(location);

        var newest = folder.NewestFileUtc ?? folder.ModifiedUtc;
        var age = newest is { } time ? now - time : (TimeSpan?)null;
        if (age is null)
        {
            evidence.Add(new ResidueEvidence(EvidenceKind.NotModifiedRecently, "When anything inside last changed isn't known.", false));
        }
        else if (age >= OldAge)
        {
            evidence.Add(new ResidueEvidence(EvidenceKind.NotModifiedRecently, string.Format(culture, "Nothing inside has changed since {0:d} ({1:N0} days).", newest!.Value.ToLocalTime(), age.Value.TotalDays), true));
            if (attribution.Evidence.Kind == EvidenceKind.NoInstalledMatch)
            {
                confidence = ResidueConfidence.Medium;
            }
        }
        else if (age <= RecentAge)
        {
            if (attribution.Evidence.Kind is EvidenceKind.NoInstalledMatch or EvidenceKind.VendorInstalledProductNot)
            {
                // Nothing names a removed application and something changed recently: most likely in use (false-positive review, TD-034).
                run.InUse++;
                return;
            }

            evidence.Add(new ResidueEvidence(EvidenceKind.ModifiedRecently, string.Format(culture, "Something inside changed on {0:d}, so a program may still use it.", newest!.Value.ToLocalTime()), false));
            confidence = Min(confidence, ResidueConfidence.Low);
            risk = Max(risk, RemovalRisk.Medium);
        }

        if (folder.UserContentBytes > 0)
        {
            evidence.Add(new ResidueEvidence(EvidenceKind.ContainsUserContent, string.Format(culture, "It holds {0} of documents, pictures, music or videos.", ByteSizeFormatter.Format(folder.UserContentBytes, culture)), false));
            risk = RemovalRisk.High;
        }

        evidence.Add(new ResidueEvidence(EvidenceKind.Location, LocationText(location), false));
        if (location is ResidueLocation.ProgramFiles or ResidueLocation.ProgramFilesX86 or ResidueLocation.UserPrograms)
        {
            evidence.Add(new ResidueEvidence(EvidenceKind.ProgramFiles, "Programs can run without a registry entry (portable programs, drivers, tools), and other software may depend on them.", false));
            confidence = attribution.Evidence.Kind == EvidenceKind.FormerlyInstalled ? Min(confidence, ResidueConfidence.Medium) : Min(confidence, ResidueConfidence.Low);
        }

        if (folder.FolderState != DirectoryScanState.Scanned || folder.IsIncomplete)
        {
            evidence.Add(new ResidueEvidence(EvidenceKind.IncompleteScan, "Part of it couldn't be read, so its contents and size are incomplete.", false));
            confidence = ResidueConfidence.Unknown;
            risk = RemovalRisk.Unknown;
        }

        var target = DecisionTargets.ForPath(run.Context.Session.Volume, folder.RelativePath);
        var decision = run.Context.Decisions.FirstOrDefault(d => d.Scope == DecisionScope.Path && string.Equals(d.Target, target, StringComparison.OrdinalIgnoreCase))
            ?? run.Context.Decisions.FirstOrDefault(d => d.Scope == DecisionScope.Application && string.Equals(d.Target, attribution.GroupKey, StringComparison.OrdinalIgnoreCase));

        run.Candidates.Add(new ResidueCandidate(folder, location, attribution.GroupKey, attribution.GroupName, confidence, risk, evidence, attribution.Evidence.Text, decision));
    }

    /// <summary>
    /// Programs on the PATH count as installed: package managers and developer tools (Chocolatey, npm, JetBrains Toolbox …)
    /// run from folders without an uninstall entry. A PATH folder inside an examined location makes the folder one level
    /// below that location an install folder ("C:\ProgramData\chocolatey\bin" → "C:\ProgramData\chocolatey"); elsewhere,
    /// only its name is used ("E:\Tools\nodejs").
    /// </summary>
    private static List<KnownApplication> CommandPathApplications(ResidueContext context)
    {
        var session = context.Session;
        var roots = new List<string> { session.DisplayPath(@"\ProgramData"), session.DisplayPath(@"\Program Files"), session.DisplayPath(@"\Program Files (x86)") };
        if (context.ProfilePath is { } profile)
        {
            roots.AddRange(ProfileRoots.Select(p => session.DisplayPath(profile + p)));
        }

        roots = [.. roots.Select(r => r.TrimEnd('\\')).OrderByDescending(r => r.Length)];
        var windows = session.DisplayPath(@"\Windows").TrimEnd('\\');
        var applications = new List<KnownApplication>();
        foreach (var raw in context.PathFolders ?? [])
        {
            var entry = raw.Trim().Trim('"').TrimEnd('\\');
            if (entry.Length < 4 || !Path.IsPathFullyQualified(entry) || entry.Equals(windows, StringComparison.OrdinalIgnoreCase) || entry.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? install = null;
            if (roots.FirstOrDefault(r => entry.StartsWith(r + @"\", StringComparison.OrdinalIgnoreCase)) is { } root)
            {
                install = root + @"\" + entry[(root.Length + 1)..].Split('\\')[0];
            }

            var name = install is null ? ProgramName(entry) : Path.GetFileName(install);
            if (name is null)
            {
                continue;
            }

            applications.Add(new KnownApplication(
                new InstalledApplication(ApplicationSource.CommandPath, entry, name, null, null, install, null, null, false),
                context.NowUtc,
                context.NowUtc,
                true));
        }

        return applications;
    }

    /// <summary>The program a PATH folder belongs to, by name ("E:\Tools\nodejs\bin" → "nodejs"), or null.</summary>
    private static string? ProgramName(string pathFolder)
    {
        var segments = pathFolder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList(); // not the drive
        while (segments.Count > 0 && PathSuffixes.Contains(segments[^1]))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return segments.Count > 0 && ApplicationMatcher.Normalize(segments[^1]) is { Length: >= 3 } normalized && !ApplicationMatcher.GenericNames.Contains(normalized)
            ? segments[^1]
            : null;
    }

    private static bool IsWindowsComponent(string name, ResidueLocation location) =>
        location is ResidueLocation.ProgramFiles or ResidueLocation.ProgramFilesX86
        && (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase));

    private static RemovalRisk BaseRisk(ResidueLocation location) => location switch
    {
        ResidueLocation.LocalLowAppData => RemovalRisk.Low,
        ResidueLocation.ProgramFiles or ResidueLocation.ProgramFilesX86 or ResidueLocation.UserPrograms => RemovalRisk.High,
        _ => RemovalRisk.Medium,
    };

    public static string LocationText(ResidueLocation location) => location switch
    {
        ResidueLocation.LocalAppData => "In your local application data (AppData\\Local): programs keep caches here, but also profiles, databases and settings.",
        ResidueLocation.RoamingAppData => "In your roaming application data (AppData\\Roaming): programs usually keep settings here, and sometimes documents or licences.",
        ResidueLocation.LocalLowAppData => "In AppData\\LocalLow: used by programs running with low privileges, mostly caches and settings.",
        ResidueLocation.UserPrograms => "In your own programs folder (AppData\\Local\\Programs): programs installed for you only.",
        ResidueLocation.PackageData => "In Store app data (AppData\\Local\\Packages): an app's settings, caches and saved data.",
        ResidueLocation.ProgramData => "In ProgramData: data shared by all users, such as settings, licences and databases.",
        _ => "In a Program Files folder: program files for all users.",
    };

    private static string Publisher(KnownApplication app) => app.Application.Publisher ?? app.Application.Name;

    private static ResidueConfidence Min(ResidueConfidence a, ResidueConfidence b) => a < b ? a : b;

    private static RemovalRisk Max(RemovalRisk a, RemovalRisk b) => a > b ? a : b;

    private sealed record Attribution(ResidueEvidence Evidence, ResidueConfidence Confidence, string GroupKey, string GroupName);

    private sealed class Run(ResidueContext context, ApplicationMatcher matcher)
    {
        public ResidueContext Context { get; } = context;

        public ApplicationMatcher Matcher { get; } = matcher;

        public List<ResidueCandidate> Candidates { get; } = [];

        public List<string> Notes { get; } = [];

        public int Examined { get; set; }

        public int Attributed { get; set; }

        public int Skipped { get; set; }

        public int InUse { get; set; }
    }
}
