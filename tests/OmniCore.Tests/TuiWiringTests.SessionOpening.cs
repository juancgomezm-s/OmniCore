using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void SessionOpening_requires_choice_shows_title_and_date_then_explicitly_continues(int columns, int rows) => RunTuiTest(fx =>
    {
        var current = fx.Server.LastSessionId()!;
        var before = fx.Server.AcquireStore().CurrentSequence(current);
        fx.StartTui(initialColumns: columns, initialRows: rows, selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "opening choice visible");
        fx.Read(() =>
        {
            Assert.True(fx.App.ConversationSelectionPending);
            Assert.Empty(fx.App.ProjectionState.Conversation.Blocks);
            Assert.Contains("Nueva conversación", fx.App.Overlay!.Title);
            Assert.True(Descendants(fx.App.Overlay).OfType<Button>().Single(view => view.Id == "conversation-new").HasFocus);
            var list = Descendants(fx.App.Overlay).OfType<ListView>().Single();
            var label = Assert.Single(list.Source!.ToList().Cast<object>()).ToString()!;
            Assert.Contains("semilla de prueba", label);
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", label);
            foreach (var button in Descendants(fx.App.Overlay).OfType<Button>())
                Assert.True(button.Frame.Bottom <= fx.App.Overlay.Viewport.Height, button.Id);
        });
        Assert.Equal(current, fx.Server.LastSessionId());
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
        ExportPreferenceFrame(fx, columns, rows, "session-opening", "Continuar la última");
        ClickId(fx, "conversation-continue-last");
        fx.Wait(() => fx.App.Overlay is null && !fx.App.ConversationSelectionPending, "explicit continue accepted");
        Assert.NotEmpty(fx.Read(() => fx.App.ProjectionState.Conversation.Blocks));
        Assert.Equal(current, fx.Server.LastSessionId());
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
    }, noColor: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionOpening_cancel_keeps_history_hidden_draft_unsent_and_selection_unchanged(bool escape) => RunTuiTest(fx =>
    {
        var host = new RouteResumeTurnHost();
        var current = fx.Server.LastSessionId()!;
        var before = fx.Server.AcquireStore().CurrentSequence(current);
        fx.StartTui(turnHost: host, selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "opening choice visible");
        if (escape) KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "cancel opening with Escape");
        else { ClickId(fx, "conversation-close"); fx.Wait(() => fx.App.Overlay is null, "cancel opening with close"); }
        Assert.True(fx.Read(() => fx.App.ConversationSelectionPending));
        Assert.Empty(fx.Read(() => fx.App.ProjectionState.Conversation.Blocks));
        fx.Invoke(() => fx.App.Composer!.SetFocus());
        Type(fx, "este borrador aún no se envía");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "conversation-selector", "sending first requires a choice");
        Assert.Equal(0, host.ExecuteCount);
        ExportPreferenceFrame(fx, fx.App.MainWindow!.Frame.Width, fx.App.MainWindow.Frame.Height, "session-cancel-unsent", "Continuar la última");
        Assert.Equal("este borrador aún no se envía", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(current, fx.Server.LastSessionId());
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "cancel without a selection");
        KeyWithEffect(fx, KeyCode.F6, () => fx.App.Overlay?.Id == "conversation-selector", "F6 reopens choice");
        Assert.True(fx.Read(() => fx.App.ConversationSelectionPending));
        ClickId(fx, "conversation-continue-last");
        fx.Wait(() => !fx.App.ConversationSelectionPending, "explicit choice keeps unsent draft");
        Assert.Equal("este borrador aún no se envía", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(0, host.ExecuteCount);
    }, noColor: false);

    [Fact]
    public void SessionOpening_new_creates_second_conversation_and_resume_restores_first_in_same_directory() => RunTuiTest(fx =>
    {
        var original = fx.Server.LastSessionId()!;
        fx.StartTui(initialColumns: 120, initialRows: 40, selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "opening choice visible");
        ClickId(fx, "conversation-new");
        fx.Wait(() => fx.App.Overlay is null && fx.Server.LastSessionId() != original, "explicit new creates independent session");
        var second = fx.Server.LastSessionId()!;
        Assert.False(fx.Read(() => fx.App.ConversationSelectionPending));
        Assert.Empty(fx.Read(() => fx.App.ProjectionState.Conversation.Blocks));
        ExportPreferenceFrame(fx, 120, 40, "session-new", "Listo para trabajar");
        var catalog = ConversationJson.Decode(fx.Server.Query("conversations", CancellationToken.None)!.Json)!;
        Assert.Equal(2, catalog.Conversations.Count);
        Assert.Contains(catalog.Conversations, row => row.Id == original.ToString());
        Assert.Contains(catalog.Conversations, row => row.Id == second.ToString());
        Type(fx, "/retomar");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "conversation-selector", "resume list visible");
        ExportPreferenceFrame(fx, 120, 40, "session-two-existing", "semilla de prueba");
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text = "semilla de prueba");
        fx.Wait(() => fx.Read(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.ToList().Count) == 1, "title search narrows choice");
        ClickId(fx, "conversation-open");
        fx.Wait(() => fx.App.Overlay is null && fx.Server.LastSessionId() == original, "selected history resumed");
        Assert.NotEmpty(fx.Read(() => fx.App.ProjectionState.Conversation.Blocks));
        ExportPreferenceFrame(fx, 120, 40, "session-resumed", "semilla de prueba");
        Assert.Equal(2, ConversationJson.Decode(fx.Server.Query("conversations", CancellationToken.None)!.Json)!.Conversations.Count);
    }, noColor: false);

    [Fact]
    public void SessionOpening_reopening_same_client_requires_a_fresh_choice_without_mutating_recovered_session() => RunTuiTest(fx =>
    {
        fx.StartTui(selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "initial selector");
        ClickId(fx, "conversation-continue-last");
        fx.Wait(() => !fx.App.ConversationSelectionPending, "initial choice accepted");
        var current = fx.Server.LastSessionId()!;
        var before = fx.Server.AcquireStore().CurrentSequence(current);
        var reopened = new TuiApp(fx.Server, policies: fx.App.Policies, account: new TestAccount(),
            providerConnections: EmptyTuiProviderConnectionHost.Instance,
            animationSettingsPath: Path.Combine(fx.Root, "reopened-animations.json"), selectConversationOnOpen: true);
        Assert.True(reopened.ConversationSelectionPending);
        Assert.Empty(reopened.ProjectionState.Conversation.Blocks);
        Assert.Equal(current, fx.Server.LastSessionId());
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
    });

    [Fact]
    public void SessionOpening_durable_open_turn_blocks_new_but_explicit_continue_preserves_turn() => RunTuiTest(fx =>
    {
        var current = fx.Server.LastSessionId()!;
        var lane = fx.Decoded<LaneCreated>().First().LaneId;
        var turn = TurnId.New();
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), current).Append(new TurnStarted(turn, lane));
        var before = fx.Server.AcquireStore().CurrentSequence(current);
        fx.StartTui(selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "opening choice visible");
        ClickId(fx, "conversation-new");
        fx.Wait(() => fx.Read(() => Descendants(fx.App.Overlay!).OfType<Label>().Single(view => view.Id == "conversation-notice").Text.Length > 0), "active turn rejection shown");
        Assert.True(fx.Read(() => fx.App.ConversationSelectionPending));
        Assert.Equal(current, fx.Server.LastSessionId());
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
        ClickId(fx, "conversation-continue-last");
        fx.Wait(() => !fx.App.ConversationSelectionPending, "continue existing recovered turn is presentation only");
        Assert.Equal(before, fx.Server.AcquireStore().CurrentSequence(current));
        Assert.Contains(fx.Decoded<TurnStarted>(), item => item.TurnId == turn);
        Assert.DoesNotContain(fx.Decoded<TurnCompleted>(), item => item.TurnId == turn);
        Assert.DoesNotContain(fx.Decoded<TurnAbandoned>(), item => item.TurnId == turn);
    });
}
