using FreeAll.Application.Abstractions;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public void Starts_on_the_first_page_with_navigation_expanded()
    {
        var shell = CreateShell(out var pages);

        Assert.Same(pages[0], shell.CurrentPage);
        Assert.True(shell.IsNavigationExpanded);
        Assert.Equal(1, pages[0].Activations);
    }

    [Fact]
    public void Navigating_activates_the_page_each_time_and_reuses_the_instance()
    {
        var shell = CreateShell(out var pages);

        shell.NavigateTo("b");
        shell.NavigateTo("a");
        shell.NavigateTo("b");

        Assert.Same(pages[1], shell.CurrentPage);
        Assert.Equal(2, pages[1].Activations);
    }

    [Fact]
    public void Unknown_key_keeps_the_current_page()
    {
        var shell = CreateShell(out var pages);

        shell.NavigateTo("missing");

        Assert.Same(pages[0], shell.CurrentPage);
    }

    [Fact]
    public void A_failing_page_does_not_break_navigation()
    {
        var shell = CreateShell(out var pages);
        pages[1].Fail = true;

        shell.NavigateTo("b");

        Assert.Same(pages[1], shell.CurrentPage);
    }

    [Fact]
    public void Toggle_command_flips_navigation_expansion()
    {
        var shell = CreateShell(out _);

        shell.ToggleNavigationCommand.Execute(null);

        Assert.False(shell.IsNavigationExpanded);
    }

    [Fact]
    public void Status_reflects_database_health_after_refresh()
    {
        var database = new FakeDatabase(DatabaseState.NotInitialized);
        var shell = new ShellViewModel([new NavigationItem("a", "A", "x", () => new TestPage())], database, TestServices.Activity(), NullLogger<ShellViewModel>.Instance);
        Assert.Equal("Database starting", shell.DatabaseStatusText);

        database.Health = new DatabaseHealth(DatabaseState.Unavailable, "Broken.", null);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.RefreshStatus();

        Assert.Equal("Database unavailable", shell.DatabaseStatusText);
        Assert.False(shell.IsDatabaseReady);
        Assert.Equal("Broken.", shell.DatabaseProblem);
        Assert.Contains(nameof(ShellViewModel.DatabaseStatusText), changed);
    }

    private static ShellViewModel CreateShell(out TestPage[] pages)
    {
        var created = new[] { new TestPage(), new TestPage() };
        pages = created;
        return new ShellViewModel(
            [
                new NavigationItem("a", "A", "", () => created[0]),
                new NavigationItem("b", "B", "", () => created[1]),
            ],
            new FakeDatabase(),
            TestServices.Activity(),
            NullLogger<ShellViewModel>.Instance);
    }

    private sealed class TestPage : PageViewModel
    {
        public int Activations { get; private set; }

        public bool Fail { get; set; }

        public override string Title => "Test";

        public override Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            Activations++;
            return Fail ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
        }
    }
}
