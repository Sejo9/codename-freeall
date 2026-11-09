using System.IO;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;
using FreeAll.Cleanup.Review;
using FreeAll.Core.Residue;
using FreeAll.Core.Review;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;
using FreeAll.Desktop.ViewModels.Residue;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class ResidueViewModelTests
{
    private readonly FakeScanIndex _index = new();
    private readonly FakeInventory _inventory = new();
    private readonly FakeDecisionStore _decisions = new();
    private readonly FakeReviewQueueStore _review = new();

    public ResidueViewModelTests()
    {
        // ProgramData with one folder no installed program matches, and one that Contoso's app owns.
        AddFolder(10, 1, @"\ProgramData");
        AddFolder(11, 10, @"\ProgramData\OldCo", newestDaysAgo: 400);
        AddFolder(12, 10, @"\ProgramData\Contoso");
        AddFolder(13, 10, @"\ProgramData\Microsoft");
        _inventory.Installed.Add(new InstalledApplication(ApplicationSource.Win32Machine, "{C}", "Contoso Writer", "Contoso", "1", null, null, null, false));
    }

    [Fact]
    public async Task Leftovers_are_listed_with_their_evidence_and_owned_or_shared_folders_are_not()
    {
        var page = Create();
        await page.OnNavigatedToAsync(CancellationToken.None);

        var row = Assert.Single(page.Rows);
        Assert.Equal("OldCo", row.Name);
        Assert.Equal("Medium", row.ConfidenceText);
        Assert.Equal("Medium", row.RiskText);
        Assert.Contains(row.Evidence, e => e.Kind == EvidenceKind.NoInstalledMatch);
        Assert.Contains("3 folders examined, 1 belong to installed applications, 1 left out on purpose", page.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Contoso Writer", page.OwnerChoices);
        Assert.Single(page.Applications);
    }

    [Fact]
    public async Task Keep_hides_a_folder_until_decided_folders_are_shown_and_undo_brings_it_back()
    {
        var page = Create();
        await page.OnNavigatedToAsync(CancellationToken.None);
        page.SetSelection([page.Rows[0]]);

        await page.KeepCommand.ExecuteAsync(null);
        Assert.Empty(page.Rows);
        Assert.Equal(DecisionScope.Path, Assert.Single(_decisions.Items).Scope);

        page.ShowDecided = true;
        Assert.Equal("Kept", Assert.Single(page.Rows).DecisionText);

        page.SetSelection([page.Rows[0]]);
        await page.UndoCommand.ExecuteAsync(null);
        Assert.Empty(_decisions.Items);
        Assert.Equal(string.Empty, Assert.Single(page.Rows).DecisionText);
    }

    [Fact]
    public async Task Application_level_and_ownership_decisions_and_review()
    {
        var page = Create();
        await page.OnNavigatedToAsync(CancellationToken.None);
        page.SelectedRow = page.Rows[0];

        await page.AddToReviewCommand.ExecuteAsync(null);
        Assert.Equal(ReviewOrigin.ApplicationResidue, Assert.Single(_review.Items).Origin);

        Assert.False(page.MarkInUseCommand.CanExecute(null));
        page.SelectedOwner = "My backup script";
        page.SelectedRow = page.Rows[0];
        await page.MarkInUseCommand.ExecuteAsync(null);
        Assert.Equal("My backup script", Assert.Single(_decisions.Items).OwnerName);

        page.ShowDecided = true;
        page.SelectedRow = page.Rows[0];
        await page.IgnoreGroupCommand.ExecuteAsync(null);
        Assert.Contains(_decisions.Items, d => d.Scope == DecisionScope.Application && d.Target == "name:oldco" && d.Decision == DecisionKind.Ignore);
        Assert.Equal("In use by My backup script", Assert.Single(page.Rows).DecisionText); // the folder's own decision wins
    }

    [Fact]
    public async Task Without_a_scan_of_the_Windows_drive_the_page_says_so()
    {
        var page = new ResidueViewModel(new FakeSystemDrive(null, null), _inventory, new FakeApplicationStore(), _decisions, _index, TestServices.Actions(), new FakeNavigator(), TestServices.Activity(), NullLogger<ResidueViewModel>.Instance);

        await page.OnNavigatedToAsync(CancellationToken.None);

        Assert.False(page.HasScan);
        Assert.Empty(page.Rows);
        Assert.Contains("Scan the Windows drive", page.SummaryText, StringComparison.Ordinal);
    }

    private ResidueViewModel Create() => new(
        new FakeSystemDrive(_index.Sessions[0] with { ScannerVersion = 3 }, null),
        _inventory,
        new FakeApplicationStore(),
        _decisions,
        _index,
        TestServices.Actions(review: new ReviewQueueService(_review)),
        new FakeNavigator(),
        TestServices.Activity(),
        NullLogger<ResidueViewModel>.Instance);

    private void AddFolder(int key, int parent, string path, int newestDaysAgo = 1) =>
        _index.Folders.Add(new IndexEntry(
            IndexEntryKind.Folder, 1, key, null, VolumePath.NameOf(path), path, string.Empty, 1000, 4096, 1, 0, null, DateTime.UtcNow.AddDays(-newestDaysAgo), null,
            FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0, NewestFileUtc: DateTime.UtcNow.AddDays(-newestDaysAgo)));
}

public sealed class TemporaryFilesViewModelTests
{
    [Fact]
    public async Task Rules_show_what_was_found_and_Windows_managed_locations_cant_be_added_for_review()
    {
        var index = new FakeScanIndex();
        index.Folders.Add(new IndexEntry(IndexEntryKind.Folder, 1, 20, null, "Windows", @"\Windows", string.Empty, 0, 0, 0, 1, null, null, null, FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0));
        index.Folders.Add(new IndexEntry(IndexEntryKind.Folder, 1, 21, null, "Temp", @"\Windows\Temp", string.Empty, 5000, 8192, 3, 0, null, null, null, FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0));
        var decisions = new FakeDecisionStore();
        var page = new TemporaryFilesViewModel(new FakeSystemDrive(index.Sessions[0], null), decisions, index, TestServices.Actions(review: new ReviewQueueService(new FakeReviewQueueStore())), new FakeNavigator(), TestServices.Activity(), NullLogger<TemporaryFilesViewModel>.Instance);

        await page.OnNavigatedToAsync(CancellationToken.None);

        Assert.Equal(Residue.Temporary.TemporaryRules.All.Count, page.Rules.Count);
        Assert.Equal("windows-temp", page.SelectedRule!.Result.Rule.Id); // the first rule with something found
        Assert.Equal("Managed by Windows", page.SelectedRule.StatusText);
        Assert.Contains(page.SelectedRule.Details, d => d.Label == "If it's removed");
        page.SetSelection([page.Matches[0]]);
        Assert.False(page.AddToReviewCommand.CanExecute(null));

        await page.KeepRuleCommand.ExecuteAsync(null);
        Assert.Equal(DecisionScope.Rule, Assert.Single(decisions.Items).Scope);
        Assert.Equal("Kept", page.Rules.Single(r => r.Result.Rule.Id == "windows-temp").DecisionText);
        Assert.Equal("Not found", page.Rules.Single(r => r.Result.Rule.Id == "user-temp").SizeText); // no profile in this scan
    }

    /// <summary>The page says why "Add to review queue" can't be used, and links to Windows' tools for Windows locations.</summary>
    [Fact]
    public async Task The_page_says_what_to_select_and_where_windows_locations_are_cleared()
    {
        var index = new FakeScanIndex();
        var key = 20;
        foreach (var path in new[] { @"\Windows", @"\Windows\Temp", @"\Users", @"\Users\me", @"\Users\me\AppData", @"\Users\me\AppData\Local", @"\Users\me\AppData\Local\Temp" })
        {
            index.Folders.Add(new IndexEntry(IndexEntryKind.Folder, 1, key++, null, VolumePath.NameOf(path), path, string.Empty, 5000, 8192, 3, 0, null, null, null, FileAttributes.Directory, 0, null, null, DirectoryScanState.Scanned, 0));
        }

        var navigator = new FakeNavigator();
        var page = new TemporaryFilesViewModel(new FakeSystemDrive(index.Sessions[0], @"\Users\me"), new FakeDecisionStore(), index, TestServices.Actions(review: new ReviewQueueService(new FakeReviewQueueStore())), navigator, TestServices.Activity(), NullLogger<TemporaryFilesViewModel>.Instance);
        await page.OnNavigatedToAsync(CancellationToken.None);

        page.SelectedRule = page.Rules.Single(r => r.Result.Rule.Id == "windows-temp");
        Assert.True(page.IsManagedByWindows);
        Assert.Contains("Windows manages this location", page.AddHint, StringComparison.Ordinal);
        page.OpenWindowsStorageCommand.Execute(null);
        Assert.Equal("windows", Assert.Single(navigator.Visited));

        page.SelectedRule = page.Rules.Single(r => r.Result.Rule.Id == "user-temp");
        Assert.False(page.IsManagedByWindows);
        Assert.StartsWith("Select one or more folders below", page.AddHint, StringComparison.Ordinal);
        Assert.False(page.AddToReviewCommand.CanExecute(null));

        page.SetSelection([page.Matches[0]]);
        Assert.Null(page.AddHint);
        Assert.True(page.AddToReviewCommand.CanExecute(null));
    }
}
