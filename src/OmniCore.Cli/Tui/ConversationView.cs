using System.Drawing;
using Terminal.Gui.Input;

namespace OmniCore.Cli;

internal sealed class ConversationView : StyledTextPresenter
{
    protected override bool OnMouseEvent(Mouse mouse)
    {
        // TextView defaults to a single row per notch, which makes long chats feel sluggish.
        // Preserve the caret, selection and composer focus while moving only the viewport.
        var direction = mouse.Flags == MouseFlags.WheeledUp ? -1 : mouse.Flags == MouseFlags.WheeledDown ? 1 : 0;
        if (direction == 0) return base.OnMouseEvent(mouse);
        ScrollTo(new Point(Viewport.X, Viewport.Y + direction * Math.Min(3, Math.Max(1, Viewport.Height - 1))));
        return true;
    }
}
