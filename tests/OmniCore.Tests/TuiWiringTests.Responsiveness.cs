using System.Drawing;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.App;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Input_burst_returns_to_the_quiet_loop_rate() => RunTuiTest(fx =>
    {
        fx.StartTui();
        fx.Wait(() => Application.MaximumIterationsPerSecond == Application.DefaultMaximumIterationsPerSecond, "quiet loop");
        fx.InjectKey(new('a'));
        fx.Read(() => Assert.Equal(50, Application.MaximumIterationsPerSecond));
        fx.Wait(() => Application.MaximumIterationsPerSecond == Application.DefaultMaximumIterationsPerSecond, "burst expires");
        Assert.Equal("a", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Explicit_framework_loop_rate_is_preserved_during_input_and_after_exit()
    {
        var previous = Application.MaximumIterationsPerSecond;
        try
        {
            Application.MaximumIterationsPerSecond = 30;
            RunTuiTest(fx =>
            {
                fx.StartTui();
                fx.InjectKey(new('a'));
                fx.Read(() => Assert.Equal(30, Application.MaximumIterationsPerSecond));
            });
            Assert.Equal(30, Application.MaximumIterationsPerSecond);
        }
        finally { Application.MaximumIterationsPerSecond = previous; }
    }

    [Fact]
    public void Conversation_wheel_moves_three_rows_without_changing_draft_caret_selection_or_focus() => RunTuiTest(fx =>
    {
        SeedRecoveryHistory(fx);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        Type(fx, "Borrador intacto");
        fx.Invoke(() =>
        {
            var chat = fx.App.Conversation!;
            chat.SelectAll();
            fx.App.Composer!.SetFocus();
            chat.ScrollTo(new Point(0, 80));
            var selection = chat.SelectedText;
            var caret = fx.App.Composer.InsertionPoint;
            var point = chat.ViewportToScreen(new Point(4, 4));
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.WheeledUp });
            Assert.Equal(77, chat.Viewport.Y);
            Assert.Equal(selection, chat.SelectedText);
            Assert.True(fx.App.Composer.HasFocus);
            Assert.Equal(caret, fx.App.Composer.InsertionPoint);
            Assert.Equal("Borrador intacto", fx.App.Composer.Text);
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.WheeledDown });
            Assert.Equal(80, chat.Viewport.Y);
            fx.App.PollOnce();
            Assert.Equal(80, chat.Viewport.Y);
        });
    });

    [Fact]
    public void Unchanged_companion_is_not_queried_or_rebuilt_on_every_ui_poll() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "ox", 0);
        var client = new CompanionPollCountingClient(fx.Server);
        fx.StartTui(initialColumns: 120, initialRows: 40, client: client);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "pet discovered");
        fx.Invoke(() =>
        {
            // Start at a fresh real status poll; immediate subsequent UI polls reuse it.
            client.Polls = 0;
            for (var i = 0; i < 8; i++) fx.App.PollOnce();
            Assert.InRange(client.Polls, 0, 1);
            CompanionPetPresentation.Execute(fx.Server, "rename Fresco");
        });
        fx.Wait(() => fx.App.CompanionPet?.Name == "Fresco", "external change is still observed");
    });

    private sealed class CompanionPollCountingClient(IOmniClient inner) : IOmniClient
    {
        internal int Polls;
        public CommandAck Send(WireEnvelope command, CancellationToken token) => inner.Send(command, token);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long sequence) => inner.SubscribeSince(sequence);
        public SessionQueryResult? Query(string name, CancellationToken token)
        {
            if (name == "companion.pet:terminal") Polls++;
            return inner.Query(name, token);
        }
    }
}
