using System.Xml.Linq;

namespace FreeAll.Architecture.Tests;

/// <summary>
/// Static accessibility rules for the WPF views (docs/07-ui-ux-design.md §7, BLK-006). The capture run's
/// automation audit checks the running pages; these keep the markup from drifting back.
/// </summary>
public sealed class AccessibilityMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XName AutomationName = Presentation + "AutomationProperties.Name";

    public static TheoryData<string> Views() =>
    [
        .. new DirectoryInfo(Path.Combine(RepositoryLayout.SourceDirectory.FullName, "FreeAll.Desktop"))
            .EnumerateFiles("*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepositoryLayout.SourceDirectory.FullName, f.FullName)),
    ];

    [Theory]
    [MemberData(nameof(Views))]
    public void Icon_glyphs_are_hidden_from_screen_readers_unless_they_are_named(string view)
    {
        var document = Load(view);

        // A plain TextBlock exposes the icon font's private-use character as text; Glyph hides it.
        var exposed = document.Descendants(Presentation + "TextBlock")
            .Where(e => IsIconStyle((string?)e.Attribute("Style")) && e.Attribute("AutomationProperties.Name") is null && e.Attribute(AutomationName) is null)
            .Select(e => (string?)e.Attribute("Text"))
            .ToList();

        Assert.True(exposed.Count == 0, $"{view}: icon TextBlocks without an automation name; use controls:Glyph ({string.Join(", ", exposed)}).");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Buttons_without_plain_text_content_have_an_automation_name(string view)
    {
        var document = Load(view);

        var unnamed = document.Descendants(Presentation + "Button")
            .Where(b => b.Attribute("Content") is null && b.Attribute("AutomationProperties.Name") is null && !HasTextChild(b))
            .Select(b => b.ToString().Split('\n')[0])
            .ToList();

        Assert.True(unnamed.Count == 0, $"{view}: buttons with only an icon need AutomationProperties.Name: {string.Join(" | ", unnamed)}");
    }

    private static bool IsIconStyle(string? style) => style is not null && (style.Contains("Typography.Icon", StringComparison.Ordinal) || style.Contains("Icon.Entry", StringComparison.Ordinal));

    /// <summary>
    /// Text content set through a style is the button's name. A TextBlock inside other content is not: the
    /// capture run's automation audit showed WPF leaves such buttons unnamed.
    /// </summary>
    private static bool HasTextChild(XElement button) =>
        button.Descendants(Presentation + "Setter").Any(s => (string?)s.Attribute("Property") == "Content" && s.Attribute("Value") is not null);

    private static XDocument Load(string view) => XDocument.Load(Path.Combine(RepositoryLayout.SourceDirectory.FullName, view));
}
