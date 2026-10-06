using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

#pragma warning disable CS0618 // Same supported TextView version as the conversation presenter.
internal sealed class ComposerView : TextView
{
    protected override void OnDrawComplete(Terminal.Gui.ViewBase.DrawContext? context)
    {
        base.OnDrawComplete(context);
        if (HasFocus) Cursor = new Cursor { Position = Cursor.Position, Style = CursorStyle.SteadyBlock };
    }
}
#pragma warning restore CS0618
