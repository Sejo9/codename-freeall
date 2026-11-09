using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace FreeAll.Desktop.Services;

/// <summary>
/// Follows Windows' Accessibility › Text size setting (docs/02-product-requirements.md §4, docs/07 §7), which WPF
/// doesn't apply by itself. The whole window content is scaled, so text and the layout around it grow together
/// and nothing is clipped; it updates live when the setting changes (TD-051).
/// </summary>
internal sealed class TextScaleService : IDisposable
{
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility";
    private FrameworkElement? _root;
    private double? _override;

    public TextScaleService() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    /// <summary>The scale in use: 1.0 to 2.25.</summary>
    public double Scale { get; private set; } = 1.0;

    /// <summary>Windows' setting as a factor (100 % to 225 %); 1.0 when it can't be read.</summary>
    public static double ReadWindowsTextScale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AccessibilityKey);
        return key?.GetValue("TextScaleFactor") is int percent ? Clamp(percent / 100.0) : 1.0;
    }

    public static double Clamp(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 1.0, 2.25) : 1.0;

    public void Attach(FrameworkElement root)
    {
        _root = root;
        Apply();
    }

    /// <summary>For review captures: use this scale instead of Windows' setting.</summary>
    public void Override(double? scale)
    {
        _override = scale is null ? null : Clamp(scale.Value);
        Apply();
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>
    /// Formats in bindings (dates, numbers) use the user's culture instead of WPF's default of en-US, so
    /// "d" in a binding shows the date the way Windows does for this user (localization readiness, TD-051).
    /// </summary>
    public static void UseCurrentCultureForFormatting()
    {
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(language));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General or UserPreferenceCategory.Window)
        {
            _root?.Dispatcher.BeginInvoke(Apply);
        }
    }

    private void Apply()
    {
        Scale = _override ?? ReadWindowsTextScale();
        if (_root is not null)
        {
            _root.LayoutTransform = Scale > 1.0 ? new ScaleTransform(Scale, Scale) : Transform.Identity;
        }
    }
}
