using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Autocomplete_queries_only_the_source_used_by_the_current_prefix_and_refreshes_next_composition() => RunTuiTest(fx =>
    {
        var client = new CompletionQueryClient(fx.Server);
        fx.StartTui(client: client);

        Type(fx, "/model");
        Assert.Equal(1, client.Queries.Count(query => query == "commands"));
        Assert.DoesNotContain(client.Queries, query => query.StartsWith("complete:", StringComparison.Ordinal));

        fx.Invoke(() => fx.App.Composer!.Text = "");
        fx.Wait(() => fx.App.Composer!.Text?.ToString() == "", "composer cleared for path completion");
        client.Queries.Clear();
        Type(fx, "@src/");
        Assert.DoesNotContain("commands", client.Queries);
        Assert.Contains(client.Queries, query => query.StartsWith("complete:", StringComparison.Ordinal));

        fx.Invoke(() => fx.App.Composer!.Text = "");
        fx.Wait(() => fx.App.Composer!.Text?.ToString() == "", "composer cleared for fresh command catalog");
        client.Queries.Clear();
        Type(fx, "/model");
        Assert.Equal(1, client.Queries.Count(query => query == "commands"));
    });

    private sealed class CompletionQueryClient(IOmniClient inner) : IOmniClient
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Queries = new();
        public CommandAck Send(WireEnvelope command, CancellationToken token) => inner.Send(command, token);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken token)
        {
            if (name == "commands" || name.StartsWith("complete:", StringComparison.Ordinal)) Queries.Enqueue(name);
            return inner.Query(name, token);
        }
    }

    [Fact]
    public void Identical_pixel_widget_frames_do_not_redraw_but_changed_pixels_do() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        var model = new PixelWidgetModel("egg", [[new PixelCell("·", "#23C9EE", "#000000")]],
            [new WidgetRowModel("idle", ThemeRole.Primary), new WidgetRowModel("pet", ThemeRole.Info)]);
        var rendererType = typeof(TuiApp).Assembly.GetType("OmniCore.Cli.PixelWidgetView", throwOnError: true)!;
        var renderer = (View)Activator.CreateInstance(rendererType, nonPublic: true)!;
        renderer.X = 1; renderer.Y = 1; renderer.Width = 20; renderer.Height = 8;
        var render = rendererType.GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var initialFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawCount = 0;
        EventHandler<EventArgs> onInitialDraw = (_, _) =>
        {
            Interlocked.Increment(ref drawCount);
            initialFrame.TrySetResult();
        };
        fx.Application.LayoutAndDrawComplete += onInitialDraw;
        var drawBaseline = 0;
        try
        {
            fx.Invoke(() =>
            {
                fx.App.MainWindow!.Add(renderer);
                render.Invoke(renderer, [model, false]);
                renderer.SetNeedsDraw();
                drawBaseline = Volatile.Read(ref drawCount);
            });
            fx.Wait(() => Volatile.Read(ref drawCount) > drawBaseline, "fresh initial pixel frame drawn");
        }
        finally { fx.Application.LayoutAndDrawComplete -= onInitialDraw; }

        fx.Invoke(() =>
        {
            var equivalent = model with
            {
                Pixels = [[new PixelCell("·", "#23C9EE", "#000000")]],
                Rows = [new WidgetRowModel("idle", ThemeRole.Primary), new WidgetRowModel("pet", ThemeRole.Info)],
            };
            var needsBefore = (renderer.NeedsLayout, renderer.NeedsDraw);
            Assert.False(needsBefore.Item1);
            Assert.False(needsBefore.Item2);
            render.Invoke(renderer, [equivalent, false]);
            Assert.Equal(needsBefore, (renderer.NeedsLayout, renderer.NeedsDraw));
        });

        var changed = model with { Pixels = [[new PixelCell("#", "#23C9EE", "#000000")]] };
        fx.Invoke(() =>
        {
            render.Invoke(renderer, [changed, false]);
            Assert.True(renderer.NeedsLayout || renderer.NeedsDraw
                || renderer.SubViews.OfType<Label>().Any(label => label.NeedsDraw));
        });
        fx.Wait(() => renderer.SubViews.OfType<Label>().Any(label => label.Text == "#"), "changed pixel rendered");

        fx.Invoke(() =>
        {
            var theme = typeof(TuiApp).GetMethod("ApplyTheme", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            theme.Invoke(fx.App, [fx.App.MainWindow!, "#061822", false, false]);
            render.Invoke(renderer, [changed, false]);
        });
        var pixelLabel = renderer.SubViews.OfType<Label>().Single(label => label.Text == "#");
        var expected = new Terminal.Gui.Drawing.Attribute(new Terminal.Gui.Drawing.Color("#23C9EE"), new Terminal.Gui.Drawing.Color("#000000"));
        Assert.Equal(expected, pixelLabel.GetScheme().Normal);
        fx.Invoke(() => render.Invoke(renderer, [changed, true]));
        Assert.Equal(new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.None, Terminal.Gui.Drawing.Color.None),
            pixelLabel.GetScheme().Normal);
    });
}
