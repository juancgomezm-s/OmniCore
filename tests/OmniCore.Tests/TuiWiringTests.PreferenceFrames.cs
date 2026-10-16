using System.Security;
using System.Text;
using Terminal.Gui.Drawing;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    // Optional evidence from actual driver cells; synthetic fixtures only, never user content.
    private static void ExportPreferenceFrame(TuiFixture fx, int columns, int rows, string scene, string marker)
    {
        var destination = Environment.GetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(destination)) return;
        using var captured = new ManualResetEventSlim();
        string? svgText = null;
        string? frameText = null;
        string? lastDrawn = null;
        Exception? captureError = null;
        EventHandler<EventArgs> capture = (_, _) =>
        {
            if (captured.IsSet) return;
            var cells = fx.Application.Driver!.Contents!;
            if (cells.GetLength(0) != rows || cells.GetLength(1) != columns) return;
            var drawn = string.Join("\n", Enumerable.Range(0, rows).Select(y => string.Concat(
                Enumerable.Range(0, columns).Select(x => cells[y, x].Grapheme))));
            lastDrawn = drawn;
            if (!drawn.Contains(marker, StringComparison.Ordinal)
                || fx.App.Overlay is { } overlay && !drawn.Contains(overlay.Title!.Trim(), StringComparison.Ordinal)) return;
            try
            {
                // Serialize while the driver buffer is stable, not a shallow Cell array
                // clone being read from the test thread during a subsequent draw.
                var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{columns * 12}\" height=\"{rows * 24}\">");
                static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                for (var y = 0; y < rows; y++)
                for (var x = 0; x < columns; x++)
                {
                    var cell = cells[y, x];
                    var attribute = cell.Attribute ?? fx.App.MainWindow!.GetScheme().Normal;
                    var reverse = attribute.Style.HasFlag(TextStyle.Reverse);
                    var foreground = reverse ? attribute.Background : attribute.Foreground;
                    var background = reverse ? attribute.Foreground : attribute.Background;
                    svg.Append($"<rect x=\"{x * 12}\" y=\"{y * 24}\" width=\"12\" height=\"24\" fill=\"{Hex(background)}\"/>");
                    if (string.IsNullOrWhiteSpace(cell.Grapheme)) continue;
                    var weight = attribute.Style.HasFlag(TextStyle.Bold) ? "bold" : "normal";
                    var italic = attribute.Style.HasFlag(TextStyle.Italic) ? "italic" : "normal";
                    var decoration = attribute.Style.HasFlag(TextStyle.Strikethrough) ? "line-through"
                        : attribute.Style.HasFlag(TextStyle.Underline) ? "underline" : "none";
                    svg.Append($"<text x=\"{x * 12}\" y=\"{y * 24 + 19}\" font-family=\"Consolas, monospace\" font-size=\"20\" font-weight=\"{weight}\" font-style=\"{italic}\" text-decoration=\"{decoration}\" fill=\"{Hex(foreground)}\">{SecurityElement.Escape(cell.Grapheme)}</text>");
                }
                svg.Append("</svg>");
                svgText = svg.ToString();
                frameText = drawn;
            }
            catch (Exception error) { captureError = error; }
            finally { captured.Set(); }
        };
        fx.Application.LayoutAndDrawComplete += capture;
        try
        {
            fx.Invoke(() => { fx.App.MainWindow!.SetNeedsLayout(); fx.App.MainWindow.SetNeedsDraw(); });
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)), "complete preference frame drawn\n" + lastDrawn);
        }
        finally { fx.Application.LayoutAndDrawComplete -= capture; }
        if (captureError is not null) throw captureError;
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, $"settings-{scene}-{columns}x{rows}.svg"), svgText);
        File.WriteAllText(Path.Combine(destination, $"settings-{scene}-{columns}x{rows}.txt"),
            frameText);
    }
}
