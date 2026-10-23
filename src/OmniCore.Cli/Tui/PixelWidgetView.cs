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
    private IReadOnlyList<IReadOnlyList<PixelCell>>? _lastPixels;
    private string? _lastTitle;
    private string? _lastSummary;
    private string? _lastAction;
    private string? _lastDismissText;
    private bool _lastNoColor;
    private bool _hasRendered;
    private bool _lastLineOnly;
    internal bool LineOnly { get; set; }
    private CompanionImageView? _raster;
    private Action<View>? _retireRaster;
    internal CompanionImageView? Raster => _raster;
    internal void RenderRaster(Color[,] image, int colors, Action<Exception> failed, Action<View> retire)
    {
        if (_raster is null)
        {
            _retireRaster = retire;
            _raster = new() { X = 0, Y = 1, Width = 20, Height = 8, Failed = failed, MaxSixelPaletteColors = colors };
            Add(_raster);
        }
        if (_raster.MaxSixelPaletteColors != colors) { _raster.MaxSixelPaletteColors = colors; RefreshRasterScale(); }
        if (!ReferenceEquals(_raster.Image, image)) _raster.Image = image;
    }
    internal void RefreshRasterScale()
    {
        // The setter invalidates the framework's bounded scale/encode cache after a font-size report.
        if (_raster is not null) _raster.Image = _raster.Image;
    }
    internal void ClearRaster()
    {
        if (_raster is null) return;
        var retired = _raster; _raster = null;
        retired.Visible = false; retired.Image = null; Remove(retired);
        if (_retireRaster is { } retire) retire(retired); else retired.Dispose();
    }
    internal event Action? ActionInvoked;
    internal event Action? DismissInvoked;
    internal string? DismissText { get; set; }
    internal void InvalidateVisualCache() => _hasRendered = false;
    internal PixelWidgetView()
    {
        CanFocus = true; Add(_heading, _summary, _action, _dismiss);
        _action.Accepted += (_, _) => ActionInvoked?.Invoke();
        _dismiss.Accepted += (_, _) => DismissInvoked?.Invoke();
    }
    internal void Render(WidgetModel model, bool noColor = false)
    {
        var lineOnly = LineOnly;
        var pixels = model is PixelWidgetModel pixel ? pixel.Pixels : [];
        var rows = model is PixelWidgetModel picture ? picture.Rows : ((ListWidgetModel)model).Rows;
        var title = model is PixelWidgetModel p ? p.Title : ((ListWidgetModel)model).Title;
        var summary = rows.FirstOrDefault()?.Text ?? "";
        var action = rows.LastOrDefault()?.Text ?? "/pet";
        var dismissText = DismissText ?? "";
        if (_hasRendered && _lastNoColor == noColor && _lastLineOnly == lineOnly
            && string.Equals(_lastTitle, title, StringComparison.Ordinal)
            && string.Equals(_lastSummary, summary, StringComparison.Ordinal)
            && string.Equals(_lastAction, action, StringComparison.Ordinal)
            && string.Equals(_lastDismissText, dismissText, StringComparison.Ordinal)
            && SamePixels(_lastPixels!, pixels)) return;

        _heading.Visible = !lineOnly; _heading.Text = title;
        _summary.Text = summary; _summary.Y = lineOnly ? 0 : pixels.Count + 1;
        _action.Y = lineOnly ? 1 : pixels.Count + 2; _action.Text = action;
        _dismiss.Visible = dismissText.Length != 0;
        _action.Width = _dismiss.Visible ? Dim.Percent(55) : Dim.Fill();
        _dismiss.X = Pos.Percent(55); _dismiss.Y = lineOnly ? 1 : pixels.Count + 2; _dismiss.Text = dismissText;
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
        _lastPixels = pixels;
        _lastTitle = title;
        _lastSummary = summary;
        _lastAction = action;
        _lastDismissText = dismissText;
        _lastNoColor = noColor;
        _lastLineOnly = lineOnly;
        _hasRendered = true;
    }

    private static bool SamePixels(IReadOnlyList<IReadOnlyList<PixelCell>> left,
        IReadOnlyList<IReadOnlyList<PixelCell>> right)
    {
        if (left.Count != right.Count) return false;
        for (var y = 0; y < left.Count; y++)
        {
            if (left[y].Count != right[y].Count) return false;
            for (var x = 0; x < left[y].Count; x++)
                if (left[y][x] != right[y][x]) return false;
        }
        return true;
    }
}
