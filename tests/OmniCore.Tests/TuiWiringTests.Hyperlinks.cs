using System.Drawing;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class RecordingWebLinkHost : IWebLinkHost
    {
        internal List<string> Opened { get; } = [];
        public Task<bool> OpenAsync(string target, CancellationToken cancellationToken)
        { Opened.Add(target); return Task.FromResult(true); }
    }

    [Fact]
    public void Local_HTML_report_opens_only_on_explicit_Enter_or_click_in_the_real_conversation() => RunTuiTest(fx =>
    {
        var report = Path.Combine(fx.Workspace, "informe local.html");
        File.WriteAllText(report, "<!doctype html><title>Informe local</title>");
        var uri = new Uri(report).AbsoluteUri;
        var browser = new FakeBrowserLauncher();
        fx.StartTui();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = new WebLinkHost(browser, fx.Workspace);
            var view = fx.App.Conversation!;
            view.WordWrap = false;
            view.LoadStyled(MarkdownRenderer.Render("[Abrir informe](" + uri + ")"), 80, false);
            Assert.Empty(browser.Launched);
            view.SetFocus();
            view.InsertionPoint = System.Drawing.Point.Empty;
            view.NewKeyDownEvent(Key.Enter);
            var point = view.ViewportToScreen(new System.Drawing.Point(2, 0));
            view.NewMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked, View = view });
            Assert.Equal(2, browser.Launched.Count);
            Assert.All(browser.Launched, target => Assert.Equal(uri, target.AbsoluteUri));
        });
    });

    [Fact]
    public void Hyperlink_Enter_and_click_use_the_production_view_and_host_wiring() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var host = new RecordingWebLinkHost();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = host;
            var view = fx.App.Conversation!;
            view.WordWrap = false;
            view.LoadStyled([[new ConversationSpan("界é ", ConversationStyle.Text),
                new ConversationSpan("Guía", ConversationStyle.Heading, LinkTarget: "https://example.com/help?q=1&b=2")]], 80, false);
            Assert.Empty(host.Opened);
            view.SetFocus();
            view.InsertionPoint = new Point(3, 0);
            view.NewKeyDownEvent(Key.Enter);
            Assert.Single(host.Opened);
            var point = new Point(4, 0); // Two columns for 界, one for é, then the space.
            point = view.ViewportToScreen(in point);
            view.NewMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked, View = view });
            Assert.Equal(2, host.Opened.Count);
            Assert.All(host.Opened, target => Assert.Equal("https://example.com/help?q=1&b=2", target));
        });
    });

    [Fact]
    public void Hyperlink_Enter_resolves_the_link_after_native_wrap_and_scroll() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var host = new RecordingWebLinkHost();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = host;
            var view = fx.App.Conversation!;
            view.WordWrap = true;
            view.LoadStyled([[new ConversationSpan(new string('x', 1000), ConversationStyle.Heading,
                LinkTarget: "https://example.com/wrapped")]], 120, true);
            view.SetFocus();
            view.MoveEnd();
            view.NewKeyDownEvent(Key.CursorLeft);
            view.NewKeyDownEvent(Key.Enter);
            Assert.True(view.Lines > 1);
            Assert.Equal("https://example.com/wrapped", Assert.Single(host.Opened));
        });
    });

    [Fact]
    public void Hyperlink_copy_and_replacement_do_not_open_or_retain_old_destinations() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var host = new RecordingWebLinkHost();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = host;
            var view = fx.App.Conversation!;
            view.WordWrap = false;
            view.LoadStyled([[new ConversationSpan("Guía", ConversationStyle.Heading, LinkTarget: "https://example.com")]], 80, false);
            view.SetFocus();
            view.InsertionPoint = Point.Empty;
            view.NewKeyDownEvent(Key.CursorRight.WithShift);
            view.Copy();
            Assert.Empty(host.Opened);
            view.InsertionPoint = Point.Empty;
            view.LoadStyled([[new ConversationSpan("Guía", ConversationStyle.Text)]], 80, false);
            view.NewKeyDownEvent(Key.Enter);
            Assert.Empty(host.Opened);
            view.LoadStyled([[new ConversationSpan("Guía", ConversationStyle.Heading, LinkTarget: "https://example.com")]], 80, false);
            view.Text = "Otro contenido";
            view.NewKeyDownEvent(Key.Enter);
            Assert.Empty(host.Opened);
        });
    });

    [Fact]
    public void Hyperlink_drag_selection_does_not_launch_the_browser() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var host = new RecordingWebLinkHost();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = host;
            var view = fx.App.Conversation!;
            view.WordWrap = false;
            view.LoadStyled([[new ConversationSpan("Guía larga", ConversationStyle.Heading, LinkTarget: "https://example.com")]], 80, false);
            var start = view.ViewportToScreen(new Point(0, 0));
            var end = view.ViewportToScreen(new Point(4, 0));
            view.NewMouseEvent(new Mouse { ScreenPosition = start, Flags = MouseFlags.LeftButtonPressed, View = view });
            view.NewMouseEvent(new Mouse { ScreenPosition = end, Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, View = view });
            view.NewMouseEvent(new Mouse { ScreenPosition = end, Flags = MouseFlags.LeftButtonReleased, View = view });
            view.NewMouseEvent(new Mouse { ScreenPosition = end, Flags = MouseFlags.LeftButtonClicked, View = view });
            Assert.Empty(host.Opened);
        });
    });
}
