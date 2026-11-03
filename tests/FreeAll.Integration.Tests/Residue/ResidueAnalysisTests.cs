using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Core.Classification;
using FreeAll.Core.Residue;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Infrastructure.Persistence;
using FreeAll.Infrastructure.Persistence.Migrations;
using FreeAll.Residue.Discovery;
using FreeAll.Residue.Evidence;
using FreeAll.Residue.Inventory;
using FreeAll.Residue.Temporary;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Integration.Tests.Residue;

public sealed class ApplicationMatcherTests
{
    private static readonly ApplicationMatcher Matcher = new(
    [
        App("Mozilla Firefox (x64 en-GB)", "Mozilla"),
        App("Microsoft Visual Studio Code", "Microsoft Corporation"),
        App("Adobe Acrobat Reader", "Adobe Systems Incorporated"),
        App("Fabrikam Notes", null, source: ApplicationSource.Msix, key: "FabrikamNotes_abcdefghjkmnp"),
        App("Contoso Studio", "Contoso", @"C:\Program Files\Contoso Studio"),
        App("Old Thing", "Gone Ltd", installed: false),
        App("Windows PC Health Check", "Microsoft Corporation"),
        App("Windows Subsystem for Linux", "Microsoft Corporation"),
        App("Node.js", "Node.js Foundation"),
    ]);

    [Theory]
    [InlineData("Mozilla", MatchKind.Vendor)]
    [InlineData("Firefox", MatchKind.Product)]
    [InlineData("Code", MatchKind.Product)]
    [InlineData("Adobe", MatchKind.Vendor)]
    [InlineData("Logs", MatchKind.Generic)]
    [InlineData("Old Thing", MatchKind.None)]
    [InlineData("Zebra Paint", MatchKind.None)]
    [InlineData("PCHealthCheck", MatchKind.Product)]        // inside the product name
    [InlineData("WSL", MatchKind.Product)]                  // initials, without "for"
    [InlineData("nextjs-nodejs", MatchKind.Product)]        // Node.js tool data
    [InlineData("org.mozilla.firefox", MatchKind.Product)]  // reverse domain: product
    [InlineData("com.adobe.dunamis", MatchKind.Product)]    // reverse domain: installed vendor
    [InlineData("com.fabrikam.sync", MatchKind.Product)]    // reverse domain: vendor known only from a package name
    [InlineData("com.zebra.paint", MatchKind.None)]
    [InlineData("LNX", MatchKind.None)]
    public void Folders_are_attributed_by_name_and_publisher(string folder, MatchKind expected) =>
        Assert.Equal(expected, Matcher.MatchInstalled(folder, @"C:\Users\a\AppData\Local\" + folder).Kind);

    [Fact]
    public void Install_folders_win_and_former_applications_are_recognized()
    {
        Assert.Equal(MatchKind.InstallFolder, Matcher.MatchInstalled("Bin", @"C:\Program Files\Contoso Studio\Bin").Kind);
        Assert.Equal(MatchKind.InstallFolder, Matcher.MatchInstalled("Program Files", @"C:\Program Files").Kind); // holds an installed app
        Assert.Equal("Old Thing", Matcher.MatchFormer("OldThing", @"C:\ProgramData\OldThing").Application!.Application.Name);
    }

    [Theory]
    [InlineData("Adobe Systems Incorporated", "adobe")]
    [InlineData("Microsoft Corporation", "microsoft")]
    [InlineData("JetBrains s.r.o.", "jetbrainssro")]
    [InlineData("The Document Foundation", "thedocumentfoundation")]
    [InlineData(null, "")]
    public void Publishers_lose_company_suffixes(string? publisher, string expected) =>
        Assert.Equal(expected, ApplicationMatcher.NormalizePublisher(publisher));

    internal static KnownApplication App(string name, string? publisher, string? location = null, bool installed = true, ApplicationSource source = ApplicationSource.Win32Machine, string? key = null) =>
        new(new InstalledApplication(source, key ?? name, name, publisher, "1.0", location, null, null, false), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), installed);
}

public sealed class RegistryInventoryTests
{
    [Theory]
    [InlineData("Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("GIMP.43237F745459_3.2.0.0_x64__nq49gba4h4mx8", "GIMP.43237F745459_nq49gba4h4mx8")]
    [InlineData("cr.sb.cdm3E4D1A088C1F6D498C84F3C86DE73CE49F82A104", null)]
    [InlineData("windows_ie_ac_001", null)]
    public void Full_names_give_family_names(string fullName, string? family) =>
        Assert.Equal(family, RegistryApplicationInventory.FamilyFromFullName(fullName));

    [Theory]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", true)]
    [InlineData("adobe.acrobatreaderdc.protectedmode", false)]
    [InlineData("windows_ie_ac_001", false)]
    [InlineData("Name_ABCDEFGHJKMNP", false)]
    public void Only_package_family_names_count_as_package_data(string name, bool isFamily) =>
        Assert.Equal(isFamily, RegistryApplicationInventory.IsFamilyName(name));

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\",0", @"C:\Program Files\App")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App")]
    [InlineData(@"C:\Windows\System32\msiexec.exe,0", null)]
    [InlineData(@"%ProgramFiles%\App\app.exe", null)]
    [InlineData("", null)]
    public void Install_folders_are_derived_only_from_plain_icon_paths(string icon, string? folder) =>
        Assert.Equal(folder, RegistryApplicationInventory.FolderOfIcon(icon));

    [Fact]
    public async Task The_inventory_reads_this_PC_without_failing()
    {
        var (applications, _) = await new RegistryApplicationInventory().ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(applications);
        Assert.All(applications, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(applications.Count, applications.Select(a => (a.Source, a.AppKey.ToUpperInvariant())).Distinct().Count());
    }
}

/// <summary>Residue and temporary-file analysis over a real SQLite index of a synthetic profile.</summary>
public sealed class ResidueAnalysisTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly VolumeInfo Volume = new(VolumeId.FromGuid(Guid.NewGuid()), "System", "NTFS", [@"C:\"], DriveKind.Fixed, true, 1_000_000, 400_000);

    private readonly TempDirectory _temp = new();
    private readonly SqliteAppDatabase _database;
    private readonly SqliteScanStore _store;
    private int _key;

    public ResidueAnalysisTests()
    {
        _database = new SqliteAppDatabase(_temp.Combine("residue.db"), EmbeddedMigrations.Load(), NullLogger<SqliteAppDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new SqliteScanStore(_database);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Candidates_carry_evidence_confidence_and_risk_and_shared_folders_are_left_out()
    {
        var session = await ScanAsync(SampleTree());
        var apps = new[]
        {
            ApplicationMatcherTests.App("Mozilla Firefox", "Mozilla"),
            ApplicationMatcherTests.App("Google Chrome", "Google LLC"),
            ApplicationMatcherTests.App("Adobe Acrobat", "Adobe Inc."),
            ApplicationMatcherTests.App("Contoso App", null, @"C:\Program Files\WindowsApps\Contoso.App", source: ApplicationSource.Msix, key: "Contoso.App_abcdefghjkmnp"),
            ApplicationMatcherTests.App("Former Editor", "Former", installed: false),
            ApplicationMatcherTests.App("Registered Tool", "Tools Co", @"C:\Program Files\Registered Tool"),
            ApplicationMatcherTests.App("Windows Subsystem for Linux", "Microsoft Corporation"),
            ApplicationMatcherTests.App("Taskmaster", "Contoso"),
        };

        string[] path = [@"C:\ProgramData\chocolatey\bin", @"C:\Users\alice\AppData\Roaming\npm", @"E:\Tools\nodejs\", @"C:\Windows\System32", "relative"];

        var report = await new ResidueAnalyzer(_store).AnalyzeAsync(new ResidueContext(session, @"\Users\alice", apps, [], Now, path), Ct);
        ResidueCandidate Get(string name) => Assert.Single(report.Candidates, c => c.Folder.Name == name);
        string[] names = [.. report.Candidates.Select(c => c.Folder.Name).Order(StringComparer.Ordinal)];

        // Owned, shared, generic, driver-vendor, PATH and sandbox folders never appear, nor FreeAll's own data or recently used
        // folders without other evidence.
        Assert.Equal(
            ["Flash", "FormerEditor", "LeftoverCo", "Old.App_abcdefghjkmnp", "OldTool", "Photos2", "Unregistered", "com.zebra.paint", "contoso.app_0.9.0.0_x64__abcdefghjkmnp"],
            names);
        Assert.Equal(1, report.InUse); // RecentTool
        Assert.Contains(report.Notes, n => n.Contains("changed in the last 30 days", StringComparison.Ordinal));

        Assert.Equal(ResidueConfidence.High, Get("Old.App_abcdefghjkmnp").Confidence);
        Assert.Equal(EvidenceKind.PackageNotInstalled, Get("Old.App_abcdefghjkmnp").Evidence[0].Kind);
        Assert.Equal(ResidueConfidence.Medium, Get("contoso.app_0.9.0.0_x64__abcdefghjkmnp").Confidence);
        Assert.Equal(ResidueConfidence.High, Get("FormerEditor").Confidence);
        Assert.Equal("Former Editor", Get("FormerEditor").GroupName);
        Assert.Equal(ResidueConfidence.Medium, Get("OldTool").Confidence);        // no match, untouched for years
        Assert.Equal(ResidueConfidence.Medium, Get("com.zebra.paint").Confidence); // no match, untouched for a year
        Assert.Equal(ResidueConfidence.Low, Get("Flash").Confidence);            // vendor still installed
        Assert.Equal(EvidenceKind.VendorInstalledProductNot, Get("Flash").Evidence[0].Kind);
        Assert.Equal(RemovalRisk.High, Get("Photos2").Risk);                     // holds pictures
        Assert.Equal(RemovalRisk.High, Get("Unregistered").Risk);                // Program Files
        Assert.Equal(ResidueConfidence.Low, Get("Unregistered").Confidence);     // capped: may be portable
        Assert.Equal(ResidueConfidence.Unknown, Get("LeftoverCo").Confidence);   // couldn't be read
        Assert.Equal(RemovalRisk.Unknown, Get("LeftoverCo").Risk);
        Assert.True(report.Skipped >= 6);
        Assert.Contains(report.Notes, n => n.Contains("Other users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Path_decisions_win_over_application_decisions()
    {
        var session = await ScanAsync(SampleTree());
        var decisions = new[]
        {
            new UserDecision(1, DecisionFeature.ApplicationResidue, DecisionScope.Application, "name:oldtool", DecisionKind.Ignore, null, null, "OldTool", null, Now),
            new UserDecision(2, DecisionFeature.ApplicationResidue, DecisionScope.Path, DecisionTargets.ForPath(Volume.Id, @"\Users\alice\AppData\Local\OldTool"), DecisionKind.Owned, Volume.Id, @"\Users\alice\AppData\Local\OldTool", "OldTool", "My script", Now),
            new UserDecision(3, DecisionFeature.ApplicationResidue, DecisionScope.Application, "name:comzebrapaint", DecisionKind.Keep, null, null, "com.zebra.paint", null, Now),
        };

        var report = await new ResidueAnalyzer(_store).AnalyzeAsync(new ResidueContext(session, @"\Users\alice", [], decisions, Now), Ct);

        Assert.Equal(DecisionKind.Owned, report.Candidates.Single(c => c.Folder.Name == "OldTool").Decision!.Decision);
        Assert.Equal(DecisionKind.Keep, report.Candidates.Single(c => c.Folder.Name == "com.zebra.paint").Decision!.Decision);
        Assert.Null(report.Candidates.Single(c => c.Folder.Name == "FormerEditor").Decision);
    }

    [Fact]
    public async Task Temporary_rules_find_their_folders_and_count_what_hasnt_changed()
    {
        var session = await ScanAsync(SampleTree());
        var decisions = new[] { new UserDecision(1, DecisionFeature.TemporaryFiles, DecisionScope.Rule, "error-reports", DecisionKind.Keep, null, null, "Errors", null, Now) };

        var results = await new TemporaryAnalyzer(_store).AnalyzeAsync(session, @"\Users\alice", decisions, Now, Ct);
        TemporaryRuleResult Rule(string id) => results.Single(r => r.Rule.Id == id);

        var temp = Assert.Single(Rule("user-temp").Matches);
        Assert.Equal(@"\Users\alice\AppData\Local\Temp", temp.Folder.RelativePath);
        Assert.Equal(1000 + 4000, temp.OldBytes); // old.tmp and the old setup folder, not new.tmp
        Assert.Equal(2, temp.OldItems);

        Assert.Equal(2, Rule("chrome-cache").Matches.Count); // Default and Profile 1
        Assert.Null(Rule("chrome-cache").OldBytes);         // caches have no age rule
        Assert.Empty(Rule("firefox-cache").Matches);
        Assert.True(Rule("windows-temp").Rule.ManagedByWindows);
        Assert.Equal(DecisionKind.Keep, Rule("error-reports").Decision!.Decision);
        Assert.All(TemporaryRules.All, r => Assert.False(string.IsNullOrWhiteSpace(r.Consequence)));
        Assert.Equal(TemporaryRules.All.Count, TemporaryRules.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Folders_record_the_newest_file_and_their_documents_and_media_recursively()
    {
        var session = await ScanAsync(SampleTree());

        var roaming = (await _store.GetFolderByPathAsync(session.Id, @"\Users\alice\AppData\Roaming", Ct))!;
        Assert.Equal(8192, roaming.UserContentBytes); // the pictures in Photos2
        Assert.Equal(Now.AddDays(-400), roaming.NewestFileUtc);
        Assert.NotNull(await _store.GetFolderByPathAsync(session.Id, @"\USERS\ALICE\appdata", Ct)); // any case
        Assert.Null(await _store.GetFolderByPathAsync(session.Id, @"\Users\bob", Ct));
    }

    [Fact]
    public async Task Inventory_history_marks_removed_applications_and_keeps_when_they_were_first_seen()
    {
        var store = new SqliteApplicationStore(_database);
        InstalledApplication App(string key) => new(ApplicationSource.Win32Machine, key, key + " app", "Pub", "1", null, null, null, false);

        var first = await store.RecordAsync([App("A"), App("B")], Ct);
        var second = await store.RecordAsync([App("B"), App("C")], Ct);

        Assert.Equal(2, first.Count(a => a.IsInstalled));
        Assert.False(second.Single(a => a.Application.AppKey == "A").IsInstalled);
        Assert.True(second.Single(a => a.Application.AppKey == "C").IsInstalled);
        Assert.Equal(first.Single(a => a.Application.AppKey == "B").FirstSeenUtc, second.Single(a => a.Application.AppKey == "B").FirstSeenUtc);
    }

    [Fact]
    public async Task Decisions_are_replaced_per_target_and_removed()
    {
        var store = new SqliteDecisionStore(_database);
        var path = DecisionTargets.ForPath(Volume.Id, @"\ProgramData\X");
        await store.SetAsync(new UserDecision(0, DecisionFeature.ApplicationResidue, DecisionScope.Path, path, DecisionKind.Keep, Volume.Id, @"\ProgramData\X", @"C:\ProgramData\X", null, default), Ct);
        await store.SetAsync(new UserDecision(0, DecisionFeature.ApplicationResidue, DecisionScope.Path, path, DecisionKind.Owned, Volume.Id, @"\ProgramData\X", @"C:\ProgramData\X", "Tool", default), Ct);
        await store.SetAsync(new UserDecision(0, DecisionFeature.TemporaryFiles, DecisionScope.Rule, "user-temp", DecisionKind.Keep, null, null, "Temp", null, default), Ct);

        var residue = Assert.Single(await store.GetAllAsync(DecisionFeature.ApplicationResidue, Ct));
        Assert.Equal(DecisionKind.Owned, residue.Decision);
        Assert.Equal("Tool", residue.OwnerName);
        Assert.Equal(Volume.Id, residue.Volume);

        await store.RemoveAsync(residue.Id, Ct);
        Assert.Empty(await store.GetAllAsync(DecisionFeature.ApplicationResidue, Ct));
        Assert.Single(await store.GetAllAsync(DecisionFeature.TemporaryFiles, Ct));
    }

    private ScanBatch SampleTree()
    {
        _key = 0;
        var batch = new ScanBatch();
        var root = Dir(batch, null, @"\", 0);
        var users = Dir(batch, root, @"\Users", 0);
        var alice = Dir(batch, users, @"\Users\alice", 0);
        var appData = Dir(batch, alice, @"\Users\alice\AppData", 0);
        var local = Dir(batch, appData, @"\Users\alice\AppData\Local", 0);
        var roaming = Dir(batch, appData, @"\Users\alice\AppData\Roaming", 0);

        Dir(batch, local, @"\Users\alice\AppData\Local\Microsoft", 3);
        Dir(batch, local, @"\Users\alice\AppData\Local\Mozilla", 3);
        Dir(batch, local, @"\Users\alice\AppData\Local\Logs", 400);
        Dir(batch, local, @"\Users\alice\AppData\Local\OldTool", 900);
        Dir(batch, local, @"\Users\alice\AppData\Local\RecentTool", 5);
        Dir(batch, local, @"\Users\alice\AppData\Local\FormerEditor", 60);
        var adobe = Dir(batch, local, @"\Users\alice\AppData\Local\Adobe", 10);
        Dir(batch, adobe, @"\Users\alice\AppData\Local\Adobe\Acrobat", 10);
        Dir(batch, adobe, @"\Users\alice\AppData\Local\Adobe\Flash", 700);
        var packages = Dir(batch, local, @"\Users\alice\AppData\Local\Packages", 2);
        Dir(batch, packages, @"\Users\alice\AppData\Local\Packages\Contoso.App_abcdefghjkmnp", 2);
        Dir(batch, packages, @"\Users\alice\AppData\Local\Packages\Old.App_abcdefghjkmnp", 300);
        Dir(batch, packages, @"\Users\alice\AppData\Local\Packages\contoso.app_0.9.0.0_x64__abcdefghjkmnp", 300);
        Dir(batch, packages, @"\Users\alice\AppData\Local\Packages\cr.sb.cdm3E4D1A088C1F6D498C84F3C86DE73CE49F82A104", 300);
        var temp = Dir(batch, local, @"\Users\alice\AppData\Local\Temp", 1);
        batch.Files.Add(FileRow(temp, "old.tmp", 1000, 30));
        batch.Files.Add(FileRow(temp, "new.tmp", 2000, 1));
        var setup = Dir(batch, temp, @"\Users\alice\AppData\Local\Temp\setup-123", 20, bytes: 4000);
        _ = setup;
        var chrome = Dir(batch, Dir(batch, Dir(batch, local, @"\Users\alice\AppData\Local\Google", 1), @"\Users\alice\AppData\Local\Google\Chrome", 1), @"\Users\alice\AppData\Local\Google\Chrome\User Data", 1);
        Dir(batch, Dir(batch, chrome, @"\Users\alice\AppData\Local\Google\Chrome\User Data\Default", 1), @"\Users\alice\AppData\Local\Google\Chrome\User Data\Default\Cache", 1, bytes: 50_000);
        Dir(batch, Dir(batch, chrome, @"\Users\alice\AppData\Local\Google\Chrome\User Data\Profile 1", 1), @"\Users\alice\AppData\Local\Google\Chrome\User Data\Profile 1\Cache", 1, bytes: 20_000);
        Dir(batch, Dir(batch, local, @"\Users\alice\AppData\Local\CrashDumps", 50), @"\Users\alice\AppData\Local\CrashDumps\x", 50);

        // Found in the false-positive review on the development PC (TD-034): none of these may be suggested.
        Dir(batch, local, @"\Users\alice\AppData\Local\FreeAll", 400);
        Dir(batch, local, @"\Users\alice\AppData\Local\com.contoso.taskmaster", 400);
        Dir(batch, local, @"\Users\alice\AppData\Local\com.zebra.paint", 400);
        Dir(batch, roaming, @"\Users\alice\AppData\Roaming\npm", 400);
        Dir(batch, roaming, @"\Users\alice\AppData\Roaming\nextjs-nodejs", 400);

        var photos = Dir(batch, roaming, @"\Users\alice\AppData\Roaming\Photos2", 400);
        batch.Files.Add(FileRow(photos, "holiday.jpg", 8192, 400, FileCategory.Images));

        var programData = Dir(batch, root, @"\ProgramData", 1);
        Dir(batch, programData, @"\ProgramData\Microsoft", 1);
        Dir(batch, programData, @"\ProgramData\Package Cache", 1);
        Dir(batch, programData, @"\ProgramData\LeftoverCo", 900, DirectoryScanState.AccessDenied);
        Dir(batch, Dir(batch, programData, @"\ProgramData\chocolatey", 400), @"\ProgramData\chocolatey\bin", 400);
        Dir(batch, programData, @"\ProgramData\ChocolateyHttpCache", 400);
        Dir(batch, programData, @"\ProgramData\Realtek", 400);

        var programFiles = Dir(batch, root, @"\Program Files", 1);
        Dir(batch, programFiles, @"\Program Files\Windows Defender", 1);
        Dir(batch, programFiles, @"\Program Files\Registered Tool", 1);
        Dir(batch, programFiles, @"\Program Files\Unregistered", 800);
        Dir(batch, programFiles, @"\Program Files\WSL", 400);
        Dir(batch, root, @"\Windows", 1);
        Dir(batch, Dir(batch, root, @"\Windows\Temp", 1, DirectoryScanState.AccessDenied), @"\Windows\Temp\x", 1);
        return batch;
    }

    /// <summary>Adds a folder whose newest file changed <paramref name="daysAgo"/> days ago.</summary>
    private int Dir(ScanBatch batch, int? parent, string path, int daysAgo, DirectoryScanState state = DirectoryScanState.Scanned, long bytes = 0)
    {
        var key = ++_key;
        var depth = VolumePath.IsRoot(path) ? 0 : path.Count(c => c == '\\');
        var modified = Now.AddDays(-daysAgo);
        batch.Directories.Add(new ScannedDirectory(
            key, parent, VolumePath.NameOf(path), path, depth, state, FileAttributes.Directory, 0, new UInt128(3, (ulong)key), modified, modified,
            bytes > 0 ? 1 : 0, bytes, bytes, bytes > 0 ? modified : null, 0));
        return key;
    }

    private static ScannedFile FileRow(int directory, string name, long size, int daysAgo, FileCategory category = FileCategory.Temporary) =>
        new(directory, name, FileClassifier.ExtensionOf(name), size, size, null, Now.AddDays(-daysAgo), null, FileAttributes.Archive, 0, null, new FileClassification(category, ClassificationReason.Extension));

    private async Task<ScanSessionSummary> ScanAsync(ScanBatch batch)
    {
        // Folder figures for the files added directly (the scanner computes these while listing).
        foreach (var group in batch.Files.GroupBy(f => f.DirectoryKey))
        {
            var index = batch.Directories.FindIndex(d => d.Key == group.Key);
            var folder = batch.Directories[index];
            batch.Directories[index] = folder with
            {
                FileCount = folder.FileCount + group.Count(),
                LogicalBytes = folder.LogicalBytes + group.Sum(f => f.LogicalSize),
                AllocatedBytes = folder.AllocatedBytes + group.Sum(f => f.AllocatedSize),
                NewestFileUtc = group.Max(f => f.ModifiedUtc),
                UserContentBytes = group.Where(f => f.Classification.Category.IsUserContent()).Sum(f => f.AllocatedSize),
            };
        }

        var id = await _store.StartSessionAsync(new NewScanSession(Volume, @"\", [], 3, FileClassifier.Version), Ct);
        await _store.WriteBatchesAsync(id, [batch], Ct);
        return await _store.CompleteSessionAsync(id, new SessionCompletion(ScanState.Partial, null, 2, 2), Ct);
    }
}
