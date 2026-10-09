using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Quiet, one-column conversation rail; all navigation stays with the native scrollbar.</summary>
internal static class ConversationScrollBarStyle
{
    internal static void Attach(ScrollBar bar, Func<bool> dimmed, string background = "#061822")
    {
        // Paint after the framework's stippled clear, before its native children.
        // Do not change global Glyphs: menus and other scrollable controls are independent.
        bar.DrawingSubViews += (_, _) => Paint(bar, '│', "#365364", dimmed(), background);
        bar.Slider.DrawingContent += (_, _) => Paint(bar.Slider, '┃', "#67D4D0", dimmed(), background);
        foreach (var button in bar.SubViews.OfType<ScrollButton>())
        {
            var glyph = button.Direction == NavigationDirection.Backward ? '╷' : '╵';
            button.DrawingContent += (_, _) => Paint(button, glyph, "#365364", dimmed(), background);
        }
    }

    private static void Paint(View view, char glyph, string foreground, bool dimmed, string background)
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var previous = view.SetAttribute(new Terminal.Gui.Drawing.Attribute(
            noColor ? Color.None : new Color(dimmed ? "#29404E" : foreground),
            noColor ? Color.None : new Color(background)));
        view.FillRect(view.Viewport, new Rune(glyph));
        view.SetAttribute(previous);
    }
}
