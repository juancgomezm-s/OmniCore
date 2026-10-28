using System.Drawing;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

#pragma warning disable CS0618 // Same supported TextView version as the conversation presenter.
internal sealed class ComposerView : TextView
{
    protected override bool OnDrawingContent(Terminal.Gui.ViewBase.DrawContext? context)
    {
        var handled = base.OnDrawingContent(context);
        if (!HasFocus || Cursor?.Position is not { } screen) return handled;
        var local = ScreenToViewport(screen);
        if (local.X < 0 || local.Y < 0 || local.X >= Viewport.Width || local.Y >= Viewport.Height) return handled;
        var previous = GetCurrentAttribute();
        SetAttribute(new Attribute(previous.Background, previous.Foreground));
        Move(local.X, local.Y);
        var lines = GetAllLines();
        var glyph = CurrentRow >= 0 && CurrentRow < lines.Count
            && CurrentColumn >= 0 && CurrentColumn < lines[CurrentRow].Count
            ? lines[CurrentRow][CurrentColumn].Grapheme : " ";
        AddStr(glyph);
        SetAttribute(previous);
        return handled;
    }

    protected override void OnDrawComplete(Terminal.Gui.ViewBase.DrawContext? context)
    {
        base.OnDrawComplete(context);
        if (HasFocus && Cursor is { } cursor) Cursor = cursor with { Style = CursorStyle.Hidden };
    }

    protected override void OnUnwrappedCursorPositionChanged(Point newUnwrappedCursorPosition)
    {
        base.OnUnwrappedCursorPositionChanged(newUnwrappedCursorPosition);
        SetNeedsDraw();
    }
}
#pragma warning restore CS0618
