using System.ComponentModel;
using System.Windows;
using FreeAll.Application.Preferences;
using Microsoft.Win32;

namespace FreeAll.Desktop.Services;

internal enum EffectiveTheme
{
    Light,
    Dark,
    HighContrast,
}

internal static class ThemeResolver
{
    /// <summary>A Windows contrast theme always wins over the user's FreeAll theme choice.</summary>
    public static EffectiveTheme Resolve(AppTheme preference, bool systemUsesLightTheme, bool highContrast)
    {
        if (highContrast)
        {
            return EffectiveTheme.HighContrast;
        }

        return preference switch
        {
            AppTheme.Light => EffectiveTheme.Light,
            AppTheme.Dark => EffectiveTheme.Dark,
            _ => systemUsesLightTheme ? EffectiveTheme.Light : EffectiveTheme.Dark,
        };
    }
}

internal interface IThemeService
{
    AppTheme Preference { get; }

    EffectiveTheme Effective { get; }

    void Apply(AppTheme preference);
}

/// <summary>
/// Applies light, dark or high-contrast styling: the WPF Fluent theme for standard controls and the
/// matching FreeAll token dictionary for project-owned surfaces (TD-011). Follows Windows changes live.
/// </summary>
internal sealed class ThemeService : IThemeService, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string TokenDictionaryMarker = "Themes/Tokens.";

    private readonly System.Windows.Application _application;

    public ThemeService(System.Windows.Application application)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public AppTheme Preference { get; private set; } = AppTheme.System;

    public EffectiveTheme Effective { get; private set; } = EffectiveTheme.Light;

    public void Apply(AppTheme preference)
    {
        Preference = preference;
        Refresh();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private void Refresh()
    {
        var effective = ThemeResolver.Resolve(Preference, ReadSystemUsesLightTheme(), SystemParameters.HighContrast);

        // High contrast uses the classic theme, which renders with the user's contrast colours.
        _application.ThemeMode = effective switch
        {
            EffectiveTheme.Light => ThemeMode.Light,
            EffectiveTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.None,
        };

        ReplaceTokenDictionary(effective);
        Effective = effective;
    }

    private void ReplaceTokenDictionary(EffectiveTheme theme)
    {
        var dictionaries = _application.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/FreeAll;component/Themes/Tokens.{theme}.xaml", UriKind.Absolute),
        };

        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Source?.OriginalString.Contains(TokenDictionaryMarker, StringComparison.OrdinalIgnoreCase) == true)
            {
                dictionaries[i] = replacement;
                return;
            }
        }

        dictionaries.Insert(0, replacement);
    }

    private static bool ReadSystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _application.Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            _application.Dispatcher.BeginInvoke(Refresh);
        }
    }
}
