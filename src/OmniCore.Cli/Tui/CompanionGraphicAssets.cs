using StbImageSharp;
using Terminal.Gui.Drawing;
using OmniCore.Client;

namespace OmniCore.Cli;

/// <summary>Embedded, pre-rendered artwork. At most two decoded atlases, loaded off the UI/draw thread.</summary>
internal sealed class CompanionGraphicAssets
{
    private readonly Dictionary<string, Task<Color[][,]>> _atlases = [];
    private readonly Queue<string> _order = new();
    internal int Count => _atlases.Count;
    internal Task<Color[][,]> Load(string? species, string variant = "warm.spots-0", bool legendary = false)
    {
        var id = species ?? "egg";
        var key = id + "/" + variant + "/" + legendary;
        if (_atlases.TryGetValue(key, out var cached)) return cached;
        while (_order.Count >= 2) _atlases.Remove(_order.Dequeue());
        var pending = Task.Run(() => Decode(id, variant, legendary));
        _atlases[key] = pending; _order.Enqueue(key);
        return pending;
    }
    internal void Clear() { _atlases.Clear(); _order.Clear(); }
    private static Color[][,] Decode(string id, string variant, bool legendary)
    {
        using var stream = typeof(CompanionGraphicAssets).Assembly.GetManifestResourceStream("omni.Assets.Companions." + id + ".png")
            ?? typeof(CompanionGraphicAssets).Assembly.GetManifestResourceStream("OmniCore.Cli.Assets.Companions." + id + ".png")
            ?? throw new IOException("Companion sprite unavailable.");
        var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (decoded.Width != 128 * CompanionVisualPolicy.GraphicFrameCount || decoded.Height != 128) throw new IOException("Invalid companion sprite atlas.");
        var frames = new Color[CompanionVisualPolicy.GraphicFrameCount][,];
        var background = new Color(decoded.Data[0], decoded.Data[1], decoded.Data[2]);
        for (var frame = 0; frame < frames.Length; frame++)
        {
            var colors = new Color[128, 128];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
            {
                var offset = (y * decoded.Width + frame * 128 + x) * 4;
                var color = new Color(decoded.Data[offset], decoded.Data[offset + 1], decoded.Data[offset + 2]);
                if (variant.StartsWith("cool", StringComparison.Ordinal) && color != background)
                    color = new Color((byte)(color.R * .83), (byte)(color.G * .96), (byte)Math.Min(255, color.B * 1.18));
                if (variant.EndsWith("1", StringComparison.Ordinal) && x is >= 77 and <= 80 && y is >= 86 and <= 90
                    || variant.EndsWith("2", StringComparison.Ordinal) && (x, y) is (47 or 48, 91 or 92) or (77 or 78, 94 or 95))
                    color = new Color((byte)(color.R * .8), (byte)(color.G * .8), (byte)(color.B * .8));
                if (legendary && x is >= 46 and <= 82 && y is >= 14 and <= 29
                    && (y >= 25 || y >= 14 + Math.Min(Math.Abs(x - 48), Math.Min(Math.Abs(x - 64), Math.Abs(x - 80)))))
                    color = new Color(246, 207, 104);
                colors[x, y] = color;
            }
            frames[frame] = colors;
        }
        return frames;
    }
}
