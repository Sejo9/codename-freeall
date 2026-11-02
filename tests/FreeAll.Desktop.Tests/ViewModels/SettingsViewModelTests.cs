using System.ComponentModel;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Preferences;
using FreeAll.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeAll.Desktop.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private readonly FakeThemeService _theme = new();
    private readonly FakePreferences _preferences = new();
    private readonly FakeShellLauncher _shell = new();
    private readonly FakeNotifier _notifier = new();

    [Fact]
    public void Initial_selection_matches_the_applied_theme_without_reapplying_it()
    {
        _theme.Apply(AppTheme.Dark);
        _theme.Applied.Clear();

        var settings = Create(new FakeDatabase());

        Assert.Equal(AppTheme.Dark, settings.SelectedThemeOption.Theme);
        Assert.Empty(_theme.Applied);
        Assert.Empty(_preferences.Values);
    }

    [Fact]
    public void Choosing_a_theme_applies_and_saves_it()
    {
        var settings = Create(new FakeDatabase());

        settings.SelectedThemeOption = settings.ThemeOptions.Single(o => o.Theme == AppTheme.Light);

        Assert.Equal([AppTheme.Light], _theme.Applied);
        Assert.Equal("Light", _preferences.Values[PreferenceKeys.Theme]);
        Assert.Null(settings.PreferenceNote);
    }

    [Fact]
    public void Theme_still_applies_when_the_database_is_unavailable_and_the_user_is_told_it_was_not_saved()
    {
        var settings = Create(new FakeDatabase(DatabaseState.Unavailable));

        settings.SelectedThemeOption = settings.ThemeOptions.Single(o => o.Theme == AppTheme.Dark);

        Assert.Equal([AppTheme.Dark], _theme.Applied);
        Assert.Empty(_preferences.Values);
        Assert.Contains("can't be saved", settings.PreferenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_save_is_reported()
    {
        _preferences.FailWrites = true;
        var settings = Create(new FakeDatabase());

        settings.SelectedThemeOption = settings.ThemeOptions.Single(o => o.Theme == AppTheme.Dark);

        Assert.Contains("couldn't be saved", settings.PreferenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_folder_commands_open_the_data_and_logs_folders()
    {
        var settings = Create(new FakeDatabase());

        settings.OpenDataFolderCommand.Execute(null);
        settings.OpenLogsFolderCommand.Execute(null);

        Assert.Equal([new FakePaths().DataDirectory, new FakePaths().LogsDirectory], _shell.Opened);
    }

    [Fact]
    public void Open_folder_failure_is_shown_to_the_user()
    {
        _shell.Failure = new Win32Exception(2, "The system cannot find the file specified.");
        var settings = Create(new FakeDatabase());

        settings.OpenDataFolderCommand.Execute(null);

        Assert.Equal("Couldn't open the folder", Assert.Single(_notifier.Errors).Title);
    }

    private SettingsViewModel Create(FakeDatabase database) =>
        new(_theme, _preferences, database, new FakePaths(), _shell, _notifier, NullLogger<SettingsViewModel>.Instance);
}
