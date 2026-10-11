using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Small native activity region. It never reloads conversation text or moves focus.</summary>
internal sealed class ActivityView : View
{
    private static readonly string[] Colors = ["#12303D", "#205061", "#30788B", "#4DBAC9", "#83C6DE", "#B47CE7"];
    internal ActivityView()
    {
        CanFocus = false;
        for (var x = 0; x < 11; x++) Add(new Label { X = x, Y = 0, Width = 1, Height = 1, CanFocus = false });
    }

    internal void Render(string effect, long frame, bool moving, bool noColor, bool attention = false, bool ascii = false)
    {
        var cells = AnimationCatalog.Render(effect, frame, SubViews.Count, moving, ascii);
        var x = 0;
        foreach (var view in SubViews)
        {
            var cell = cells[x++];
            var attribute = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(attention ? "#D99955" : Colors[cell.Intensity]),
                noColor ? Color.None : new Color("#061822"));
            if (view.Text == cell.Glyph && view.GetScheme().Normal == attribute) continue;
            view.Text = cell.Glyph;
            view.SetScheme(new Scheme { Normal = attribute, Focus = attribute });
            view.SetNeedsDraw();
        }
    }
}
