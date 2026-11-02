namespace FreeAll.Application.Preferences;

/// <summary>User theme choice (docs/07-ui-ux-design.md §4).</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

public static class PreferenceKeys
{
    public const string Theme = "ui.theme";
}
