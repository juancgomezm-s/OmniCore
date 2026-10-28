using System.Drawing;
using System.Text;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Color = Terminal.Gui.Drawing.Color;

namespace OmniCore.Cli;

/// <summary>Opaque sprite over chat; untouched cells show through and every mouse event passes to the chat.</summary>
internal sealed class CompanionRunnerView : View
{
    private sealed record Pixel(Point Position, string Glyph, Terminal.Gui.Drawing.Attribute Attribute);
    private List<Pixel> _pixels = [];
    private readonly HashSet<Point> _drawnOffsets = [];
    private Rectangle? _lastScreenBounds;
    private Terminal.Gui.Drawing.Attribute _erase;
    private (CompanionPetSnapshot State, CompanionPlayPose Pose, TuiTheme Theme, bool NoColor)? _rendered;
    internal Func<Rectangle>? ClipScreen { get; init; }
    internal Func<Rectangle, IReadOnlyList<Rectangle>>? UnderlyingWideGlyphs { get; init; }
    internal IReadOnlyList<Point> PaintedOffsets => _drawnOffsets.ToArray();
    internal CompanionRunnerView()
    {
        CanFocus = false; Enabled = false;
        ViewportSettings = ViewportSettingsFlags.Transparent | ViewportSettingsFlags.TransparentMouse;
        Width = CompanionPetPlayground.CanvasWidth; Height = CompanionPetPlayground.SpriteHeight;
    }
    internal void Render(CompanionPetSnapshot state, CompanionPlayPose pose, TuiTheme theme, bool noColor)
    {
        var key = (state, pose, theme, noColor);
        if (_rendered == key) return;
        _rendered = key;
        var background = noColor ? Color.None : theme.Map(new Color(theme.Background));
        _erase = new(background, background);
        var pixels = CompanionSpriteCatalog.RenderMini(state, pose.Animation, pose.Frame, pose.FacingLeft);
        var offset = pose.FacingLeft ? 0 : 2;
        var painted = new List<Pixel>();
        for (var y = 0; y < CompanionPetPlayground.SpriteHeight; y++)
        for (var x = 0; x < CompanionPetPlayground.SpriteWidth + 2; x++)
        {
            var cell = x >= offset && x < offset + CompanionPetPlayground.SpriteWidth ? pixels[y][x - offset] : null;
            var effect = cell is null ? pose.Animation switch
            {
                "dance" when y == 0 => pose.Frame % 2 == 0 ? "♪" : "♫",
                "pounce" when y == 1 && pose.Frame % 2 == 1 => "*",
                "peek" or "hide" when y == 0 && pose.Frame % 3 == 0 => "?",
                "run" or "dash" or "roll" when y == CompanionPetPlayground.SpriteHeight - 1 && pose.Frame % 2 == 0 => "·",
                _ => " ",
            } : " ";
            var glyph = cell?.Glyph ?? effect;
            if (string.IsNullOrWhiteSpace(glyph)) continue;
            var attr = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : theme.Map(new Color(cell?.Foreground ?? theme.Accent)),
                noColor ? Color.None : theme.Map(new Color(cell?.Background ?? theme.Background)));
            painted.Add(new(new(x + 1, y), glyph, attr));
        }
        if (_pixels.SequenceEqual(painted)) return;
        RestoreUnderlay(); _drawnOffsets.Clear();
        _pixels = painted; SetNeedsDraw();
    }
    private void RestoreUnderlay()
    {
        if (_lastScreenBounds is not { } region || SuperView is not { } parent) return;
        // A wide grapheme can straddle the old silhouette. Restore both cells,
        // including when turning in place; invalidate only this small chat region.
        region.Inflate(1, 0);
        if (ClipScreen?.Invoke() is { } clip) region.Intersect(clip);
        if (region.IsEmpty) return;
        var origin = parent.ViewportToScreen(Point.Empty);
        region.Offset(-origin.X, -origin.Y);
        parent.SetNeedsDraw(region);
    }
    protected override void OnFrameChanged(in Rectangle frame)
    {
        RestoreUnderlay(); _drawnOffsets.Clear();
        base.OnFrameChanged(frame);
    }
    protected override void OnVisibleChanged()
    {
        if (!Visible) { RestoreUnderlay(); _drawnOffsets.Clear(); }
        base.OnVisibleChanged();
    }
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var clip = ClipScreen?.Invoke();
        var currentBounds = new Rectangle(ViewportToScreen(Point.Empty), Viewport.Size);
        if (clip is { } visible) currentBounds.Intersect(visible);
        _lastScreenBounds = currentBounds;
        _drawnOffsets.Clear();
        // Transparent siblings may draw before the underlying chat. Read its model,
        // not last frame's output buffer, and occlude complete wide graphemes.
        var wideGlyphs = UnderlyingWideGlyphs?.Invoke(currentBounds) ?? [];
        var wideToClear = new HashSet<Rectangle>();
        var drawable = new List<Pixel>(_pixels.Count);
        foreach (var pixel in _pixels)
        {
            var screen = ViewportToScreen(pixel.Position);
            if (clip is { } area && !area.Contains(screen)) continue;
            var affected = new Rectangle(screen, new Size(1, 1));
            foreach (var glyph in wideGlyphs)
                if (glyph.Contains(screen)) { affected = glyph; break; }
            if (clip is { } bounds && !bounds.Contains(affected)) continue;
            if (!currentBounds.Contains(affected)) continue;
            if (affected.Width == 2) wideToClear.Add(affected);
            for (var x = affected.Left; x < affected.Right; x++)
                _drawnOffsets.Add(new(pixel.Position.X + x - screen.X, pixel.Position.Y));
            context?.AddDrawnRectangle(affected);
            drawable.Add(pixel);
        }
        SetAttribute(_erase);
        var screenOrigin = ViewportToScreen(Point.Empty);
        foreach (var glyph in wideToClear)
            for (var x = glyph.Left; x < glyph.Right; x++)
                AddRune(x - screenOrigin.X, glyph.Y - screenOrigin.Y, new Rune(' '));
        foreach (var pixel in drawable)
        {
            SetAttribute(pixel.Attribute);
            AddRune(pixel.Position.X, pixel.Position.Y, Rune.GetRuneAt(pixel.Glyph, 0));
        }
        return true;
    }
}
