using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Small native activity region. It never reloads conversation text or moves focus.</summary>
internal sealed class ActivityView : View
{
    private static readonly string[] Colors = ["#12303D", "#205061", "#30788B", "#4DBAC9", "#83C6DE", "#B47CE7"];
    internal TuiTheme Theme { get; set; } = TuiTheme.All[0];
    internal ActivityView()
    {
        CanFocus = false;
        for (var x = 0; x < 11; x++) Add(new Label { X = x, Y = 0, Width = 1, Height = 1, CanFocus = false });
    }

    internal void Render(string effect, long frame, bool moving, bool noColor, bool attention = false, bool ascii = false,
        string? label = null)
    {
        var width = label is null ? Math.Clamp(Viewport.Width > 0 ? Viewport.Width - 1 : 11, 1, 80) : Math.Clamp(Viewport.Width, 0, 80);
        var height = label is null ? Math.Clamp(Viewport.Height, 1, 5) : 1;
        var rows = label is null
            ? AnimationCatalog.IsText(effect) ? AnimationCatalog.RenderFrame("omega-pulse", 0, width, moving: false, ascii: ascii)
                : AnimationCatalog.RenderFrame(effect, frame, width, height, moving: moving, ascii: ascii)
            : new[] { AnimationCatalog.RenderText(effect, label, frame, width, moving, ascii) };
        var wanted = rows.Sum(row => row.Count);
        while (SubViews.Count < wanted) Add(new Label { Width = 1, Height = 1, CanFocus = false });
        var views = SubViews.ToArray();
        var index = 0;
        var labelOffsets = label is null ? null : System.Globalization.StringInfo.ParseCombiningCharacters(label);
        Text = label ?? ""; // Accessible, stable phase text; never the transient scrambled characters.
        var palette = effect is "fire" or "fire3" ? new[] { "#281A16", "#663321", "#A34D25", "#D96F2C", "#F6B44A", "#FFE3A3" }
            : effect is "matrix" or "matrix3" ? new[] { "#10251C", "#1C4D33", "#297A4B", "#47AC68", "#83DBA0", "#CDF4DC" } : Colors;
        for (var y = 0; y < rows.Count; y++)
        {
            var column = 0;
            foreach (var cell in rows[y])
            {
                var view = views[index++];
                var originalGlyph = labelOffsets is not null ? System.Globalization.StringInfo.GetNextTextElement(label!, labelOffsets[index - 1]) : cell.Glyph;
                var columns = Math.Max(1, Terminal.Gui.Text.StringExtensions.GetColumns(originalGlyph, false));
                view.Visible = column + columns <= width; view.X = column; view.Y = y; view.Width = columns; column += columns;
                var color = attention ? "#D99955" : label is not null && cell.Intensity == 2 ? Theme.Foreground
                    : effect == "braille-spinner" && index == 3 ? "#38BDF8" : palette[cell.Intensity];
                var attribute = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : Theme.Map(new Color(color)),
                    noColor ? Color.None : Theme.Map(new Color("#061822")));
                if (view.Text == cell.Glyph && view.GetScheme().Normal == attribute) continue;
                view.Text = cell.Glyph;
                view.SetScheme(new Scheme { Normal = attribute, Focus = attribute });
                view.SetNeedsDraw();
            }
        }
        foreach (var unused in SubViews.Skip(index)) unused.Visible = false;
    }
}
