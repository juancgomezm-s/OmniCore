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
    private (CompanionPetSnapshot State, CompanionPlayPose Pose, TuiTheme Theme, bool NoColor)? _rendered;
    internal Func<Rectangle>? ClipScreen { get; init; }
    internal IReadOnlyList<Point> PaintedOffsets => _pixels.Select(pixel => pixel.Position).ToArray();
    internal CompanionRunnerView()
    {
        CanFocus = false; Enabled = false;
        ViewportSettings = ViewportSettingsFlags.Transparent | ViewportSettingsFlags.TransparentMouse;
        Width = CompanionPetPlayground.SpriteWidth + 2; Height = CompanionPetPlayground.SpriteHeight;
    }
    internal void Render(CompanionPetSnapshot state, CompanionPlayPose pose, TuiTheme theme, bool noColor)
    {
        var key = (state, pose, theme, noColor);
        if (_rendered == key) return;
        _rendered = key;
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
            painted.Add(new(new(x, y), glyph, attr));
        }
        if (_pixels.SequenceEqual(painted)) return;
        _pixels = painted; SetNeedsDraw();
    }
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var clip = ClipScreen?.Invoke();
        foreach (var pixel in _pixels)
        {
            var screen = ViewportToScreen(pixel.Position);
            if (clip is { } area && !area.Contains(screen)) continue;
            SetAttribute(pixel.Attribute);
            AddRune(pixel.Position.X, pixel.Position.Y, Rune.GetRuneAt(pixel.Glyph, 0));
            context?.AddDrawnRectangle(new Rectangle(screen, new Size(1, 1)));
        }
        return true;
    }
}
