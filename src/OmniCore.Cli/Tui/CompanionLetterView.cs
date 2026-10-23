using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace OmniCore.Cli;

/// <summary>An ephemeral visual copy, outside the editor and absent from its input/selection model.</summary>
internal sealed class CompanionLetterView : View
{
    internal string Glyph { get; set; } = "";
    internal Terminal.Gui.Drawing.Attribute Ink { get; set; }
    internal CompanionLetterView()
    {
        CanFocus = false; Enabled = false; Height = 1;
        ViewportSettings = ViewportSettingsFlags.Transparent | ViewportSettingsFlags.TransparentMouse;
    }
    protected override bool OnDrawingContent(DrawContext? context)
    {
        SetAttribute(Ink); AddStr(0, 0, Glyph);
        context?.AddDrawnRectangle(new Rectangle(ViewportToScreen(Point.Empty), Viewport.Size));
        return true;
    }
}
