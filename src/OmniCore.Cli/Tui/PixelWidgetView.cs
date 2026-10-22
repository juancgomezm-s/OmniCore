using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Native renderer for declarative pixel widgets. Invalidates only changed cells.</summary>
internal sealed class PixelWidgetView : View
{
    private readonly Label _heading = new() { X = 0, Y = 0, Height = 1, Width = Dim.Fill(), CanFocus = false };
    private readonly Label _summary = new() { X = 0, Height = 1, Width = Dim.Fill(), CanFocus = false };
    private readonly Button _action = new() { Id = "pixel-widget-action", X = 0, Height = 2, Width = Dim.Fill(),
        TextAlignment = Alignment.Start, NoDecorations = true, ShadowStyle = ShadowStyles.None };
    private readonly Button _dismiss = new() { Id = "pixel-widget-hide", Height = 2, Width = Dim.Fill(),
        TextAlignment = Alignment.Start, NoDecorations = true, ShadowStyle = ShadowStyles.None, Visible = false };
    private readonly List<Label> _pixels = [];
    internal event Action? ActionInvoked;
    internal event Action? DismissInvoked;
    internal string? DismissText { get; set; }
    internal PixelWidgetView()
    {
        CanFocus = true; Add(_heading, _summary, _action, _dismiss);
        _action.Accepted += (_, _) => ActionInvoked?.Invoke();
        _dismiss.Accepted += (_, _) => DismissInvoked?.Invoke();
    }
    internal void Render(WidgetModel model, bool noColor = false)
    {
        var pixels = model is PixelWidgetModel pixel ? pixel.Pixels : [];
        var rows = model is PixelWidgetModel picture ? picture.Rows : ((ListWidgetModel)model).Rows;
        _heading.Text = model is PixelWidgetModel p ? p.Title : ((ListWidgetModel)model).Title;
        _summary.Text = rows.FirstOrDefault()?.Text ?? ""; _summary.Y = pixels.Count + 1;
        _action.Y = pixels.Count + 2; _action.Text = rows.LastOrDefault()?.Text ?? "/pet";
        _dismiss.Visible = !string.IsNullOrEmpty(DismissText);
        _action.Width = _dismiss.Visible ? Dim.Percent(55) : Dim.Fill();
        _dismiss.X = Pos.Percent(55); _dismiss.Y = pixels.Count + 2; _dismiss.Text = DismissText ?? "";
        var wanted = pixels.Sum(row => row.Count);
        while (_pixels.Count < wanted)
        {
            var label = new Label { Width = 1, Height = 1, CanFocus = false };
            label.MouseEvent += (_, mouse) =>
            {
                if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)) { ActionInvoked?.Invoke(); mouse.Handled = true; }
            };
            _pixels.Add(label); Add(label);
        }
        var index = 0;
        for (var y = 0; y < pixels.Count; y++)
        for (var x = 0; x < pixels[y].Count; x++)
        {
            var cell = pixels[y][x]; var view = _pixels[index++];
            view.Visible = true; view.X = x; view.Y = y + 1;
            var attr = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(cell.Foreground),
                noColor ? Color.None : new Color(cell.Background));
            if (view.Text == cell.Glyph && view.GetScheme().Normal == attr) continue;
            view.Text = cell.Glyph; view.SetScheme(new Scheme { Normal = attr, Focus = attr }); view.SetNeedsDraw();
        }
        foreach (var unused in _pixels.Skip(index)) unused.Visible = false;
    }
}
