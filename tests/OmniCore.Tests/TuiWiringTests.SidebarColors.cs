using OmniCore.Client;
using OmniCore.Cli;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sidebar_preserves_role_color_across_wrapped_rows_after_focus_refresh_and_resize(bool noColor) => RunTuiTest(fx =>
    {
        File.WriteAllText(Path.Combine(fx.Root, "tui-animations.theme"), "omni-llama");
        fx.StartTui(initialColumns: 80, initialRows: 30);
        var sidebar = fx.App.SidebarContent!;
        KeyWithEffect(fx, KeyCode.F2, () => fx.App.Sidebar!.Visible, "sidebar visible");
        var marker = "LANE_COLOR_START";
        var text = marker + " " + string.Join(' ', Enumerable.Range(0, 4)
            .Select(index => $"1/1-{index:D2}-0-lectura")) + " LANE_COLOR_END";
        var attentionMarker = "ATTENTION_COLOR_START";
        var attentionText = attentionMarker + " " + string.Join(' ', Enumerable.Range(0, 2)
            .Select(index => $"0/1-{index:D2}-1-espera")) + " ATTENTION_COLOR_END";
        var widget = new PlanSidebarWidget(new PlanWidgetData("WRAP_COLOR_HEADER",
            [new WidgetRowModel(text, ThemeRole.Info), new WidgetRowModel(attentionText, ThemeRole.Attention)]));
        var body = new[] { (Widget: (ISidebarWidget)widget, Model: widget.Build(null!, WidgetSize.Expanded)) };

        void Render() => fx.Invoke(() => sidebar.Render(body));
        Terminal.Gui.Drawing.Cell[,] Capture(string requiredMarker)
        {
            using var captured = new ManualResetEventSlim();
            Terminal.Gui.Drawing.Cell[,]? result = null;
            EventHandler<EventArgs> handler = (_, _) =>
            {
                var contents = fx.Application.Driver!.Contents!;
                var frameText = string.Join("\n", Enumerable.Range(0, contents.GetLength(0)).Select(y =>
                    string.Concat(Enumerable.Range(0, contents.GetLength(1)).Select(x => contents[y, x].Grapheme))));
                if (frameText.Contains(requiredMarker, StringComparison.Ordinal))
                {
                    result = (Terminal.Gui.Drawing.Cell[,])contents.Clone();
                    captured.Set();
                }
            };
            fx.Application.LayoutAndDrawComplete += handler;
            try
            {
                // ScrollTo can synchronously draw the new viewport before this capture
                // subscribes. Invalidate the owning window after subscribing so the
                // next event is a fresh frame for the current viewport, not an event
                // that may already have happened or a stale driver buffer.
                fx.Invoke(() =>
                {
                    fx.App.MainWindow!.SetNeedsLayout();
                    fx.App.MainWindow.SetNeedsDraw();
                });
                Assert.True(captured.Wait(TimeSpan.FromSeconds(10)), $"marcador {requiredMarker} del sidebar dibujado");
                return Assert.IsType<Terminal.Gui.Drawing.Cell[,]>(result);
            }
            finally { fx.Application.LayoutAndDrawComplete -= handler; }
        }

        void AssertWrappedRoleColor(string startMarker, string endMarker, string sourceColor)
        {
            var cells = Capture(startMarker);
            var lines = Enumerable.Range(0, cells.GetLength(0)).Select(y =>
                string.Concat(Enumerable.Range(0, cells.GetLength(1)).Select(x => cells[y, x].Grapheme))).ToArray();
            var first = Array.FindIndex(lines, line => line.Contains(startMarker, StringComparison.Ordinal));
            var last = Array.FindIndex(lines, line => line.Contains(endMarker, StringComparison.Ordinal));
            Assert.True(first >= 0 && last > first, $"la fila coloreada se parte en líneas: {string.Join(" | ", lines.Where(line => line.Contains("COLOR", StringComparison.Ordinal)))}");

            var expected = noColor ? Color.None : sidebar.Theme.Map(
                new Terminal.Gui.Drawing.Attribute(new Color(sourceColor), new Color("#202C3B"))).Foreground;
            var origin = sidebar.ViewportToScreen(System.Drawing.Point.Empty);
            var textRight = origin.X + sidebar.Viewport.Width - 1; // the final column is the native scrollbar
            var mismatches = new List<string>();
            for (var y = first; y <= last; y++)
            for (var x = origin.X; x < textRight; x++)
            {
                var glyph = cells[y, x].Grapheme;
                if (string.IsNullOrWhiteSpace(glyph)) continue;
                if (!expected.Equals(cells[y, x].Attribute?.Foreground))
                    mismatches.Add($"'{glyph}'@{x},{y}:{cells[y, x].Attribute?.Foreground}");
            }
            Assert.True(mismatches.Count == 0,
                $"Color {expected} mismatches for {startMarker}: {string.Join(", ", mismatches.Take(16))}; focus={sidebar.HasFocus}, insertion={sidebar.InsertionPoint}, selection='{sidebar.SelectedText}'");
        }

        Render();
        fx.Wait(() => sidebar.Text.Contains(marker), "fila de lanes cargada");
        AssertWrappedRoleColor(marker, "LANE_COLOR_END", "#85D6F5");
        AssertWrappedRoleColor(attentionMarker, "ATTENTION_COLOR_END", "#F1D58A");

        fx.Invoke(() => sidebar.SetFocus());
        Render();
        AssertWrappedRoleColor(marker, "LANE_COLOR_END", "#85D6F5");
        AssertWrappedRoleColor(attentionMarker, "ATTENTION_COLOR_END", "#F1D58A");

        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(100, 35));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 100, "resize de driver aplicado");
        Render();
        AssertWrappedRoleColor(marker, "LANE_COLOR_END", "#85D6F5");
        AssertWrappedRoleColor(attentionMarker, "ATTENTION_COLOR_END", "#F1D58A");

        var tailMarker = "TAIL";
        var tailText = "LONG_TAIL_START " + string.Join(' ', Enumerable.Range(0, 100)
            .Select(index => $"lane-{index:D2}-status")) + " " + tailMarker;
        var wrappedTail = Terminal.Gui.Text.TextFormatter.Format(tailText, sidebar.Viewport.Width,
            Terminal.Gui.ViewBase.Alignment.Start, true, preserveTrailingSpaces: true,
            tabWidth: sidebar.TabWidth, preserveTabs: true);
        var finalFragmentLength = System.Globalization.StringInfo.ParseCombiningCharacters(wrappedTail[^1]).Length;
        Assert.True(wrappedTail.Count - 1 >= finalFragmentLength,
            "the final wrapped fragment must be fully attribute-less in Terminal.Gui's current word-wrap implementation");
        var tailWidget = new PlanSidebarWidget(new PlanWidgetData("LONG_TAIL_HEADER",
            [new WidgetRowModel(tailText, ThemeRole.Info)]));
        body = [(Widget: (ISidebarWidget)tailWidget, Model: tailWidget.Build(null!, WidgetSize.Expanded))];
        Render();
        fx.Invoke(() => sidebar.ScrollTo(new System.Drawing.Point(0, Math.Max(0, sidebar.Lines - sidebar.Viewport.Height))));
        var tailCells = Capture(tailMarker);
        var tailLines = Enumerable.Range(0, tailCells.GetLength(0)).Select(y =>
            string.Concat(Enumerable.Range(0, tailCells.GetLength(1)).Select(x => tailCells[y, x].Grapheme))).ToArray();
        var tailRow = Array.FindIndex(tailLines, line => line.Contains(tailMarker, StringComparison.Ordinal));
        Assert.True(tailRow >= 0, "the all-attribute-less final fragment is visible after scrolling to the tail");
        var tailColumn = tailLines[tailRow].IndexOf(tailMarker, StringComparison.Ordinal);
        var tailExpected = noColor ? Color.None : sidebar.Theme.Map(
            new Terminal.Gui.Drawing.Attribute(new Color("#85D6F5"), new Color("#202C3B"))).Foreground;
        for (var x = tailColumn; x < tailColumn + tailMarker.Length; x++)
            Assert.True(tailExpected.Equals(tailCells[tailRow, x].Attribute?.Foreground),
                $"fully attribute-less fragment glyph '{tailCells[tailRow, x].Grapheme}' at ({x},{tailRow}) expected {tailExpected} but got {tailCells[tailRow, x].Attribute?.Foreground}");
    }, noColor: noColor);
}
