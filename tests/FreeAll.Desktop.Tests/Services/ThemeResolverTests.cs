using FreeAll.Application.Preferences;
using FreeAll.Desktop.Services;

namespace FreeAll.Desktop.Tests.Services;

public sealed class ThemeResolverTests
{
    [Fact]
    public void System_preference_follows_windows_app_mode()
    {
        Assert.Equal(EffectiveTheme.Light, ThemeResolver.Resolve(AppTheme.System, systemUsesLightTheme: true, highContrast: false));
        Assert.Equal(EffectiveTheme.Dark, ThemeResolver.Resolve(AppTheme.System, systemUsesLightTheme: false, highContrast: false));
    }

    [Fact]
    public void Explicit_choice_overrides_windows_app_mode()
    {
        Assert.Equal(EffectiveTheme.Dark, ThemeResolver.Resolve(AppTheme.Dark, systemUsesLightTheme: true, highContrast: false));
        Assert.Equal(EffectiveTheme.Light, ThemeResolver.Resolve(AppTheme.Light, systemUsesLightTheme: false, highContrast: false));
    }

    [Fact]
    public void High_contrast_always_wins()
    {
        foreach (var preference in Enum.GetValues<AppTheme>())
        {
            Assert.Equal(EffectiveTheme.HighContrast, ThemeResolver.Resolve(preference, systemUsesLightTheme: true, highContrast: true));
        }
    }
}
