using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace FreeAll.Desktop.Controls;

/// <summary>
/// A decorative icon glyph (Segoe Fluent Icons). WPF has no <c>AccessibilityView</c>, so a plain
/// <see cref="TextBlock"/> exposes the glyph's private-use character to screen readers as text. This peer
/// keeps the element out of the control and content views of UI Automation; the meaning must come from
/// the text or the automation name next to it (BLK-006, docs/07-ui-ux-design.md §7).
/// </summary>
internal sealed class Glyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GlyphAutomationPeer(this);

    private sealed class GlyphAutomationPeer : TextBlockAutomationPeer
    {
        public GlyphAutomationPeer(Glyph owner)
            : base(owner)
        {
        }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
