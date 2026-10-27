using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static void SeedSearchText(TuiFixture fx)
    {
        var root = fx.Decoded<RunCreated>().Last().RootTask;
        var lane = fx.Decoded<LaneCreated>().Single(created => created.TaskId == root).LaneId;
        using var scope = ExecutionScope.Begin(new(fx.Server.LastRunId()!, root, lane));
        // Root-level inputs are projected through the same Host visibility filter as ordinary input.
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new UserInputReceived(fx.Server.LastRunId()!, "\"Buscar aguja ÚNICA 😀 en el texto original\"", null));
    }
    private static void OpenConversationSearch(TuiFixture fx)
    {
        KeyWithEffect(fx, KeyCode.F | KeyCode.CtrlMask, () => fx.App.Overlay?.Id == "conversation-search", "Ctrl+F abre búsqueda");
    }
    private static void SearchConversation(TuiFixture fx, string query) => fx.Invoke(() =>
        Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "conversation-search-query").Text = query);

    [Theory]
    [InlineData("es", 80, 25, "Buscar en esta conversación")]
    [InlineData("en", 130, 38, "Search this conversation")]
    [InlineData("fr", 80, 25, "Rechercher dans cette conversation")]
    [InlineData("it", 130, 38, "Cerca in questa conversazione")]
    public void Conversation_search_is_localized_read_only_and_navigates_to_the_original_message(string locale, int width, int height, string title) => RunTuiTest(fx =>
    {
        SeedSearchText(fx);
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded, initialColumns: width, initialRows: height,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "borrador que no se debe enviar");
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        OpenConversationSearch(fx); SearchConversation(fx, "aguja");
        fx.Read(() =>
        {
            Assert.Contains(title, fx.App.Overlay!.Title);
            Assert.Single(Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.ToList());
            Assert.Contains("Buscar aguja ÚNICA 😀 en el texto original", OverlayText(fx.App.Overlay));
            Assert.Contains(fx.Server.LastSessionId()!.ToString(), OverlayText(fx.App.Overlay));
            Assert.True(Descendants(fx.App.Overlay).Single(view => view.Id == "conversation-search-query").HasFocus);
        });
        ExportPreferenceFrame(fx, width, height, "conversation-search-" + locale, title);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null && fx.App.Conversation!.HasFocus, "Enter navega al mensaje");
        var block = fx.Read(() => fx.App.ProjectionState.Conversation.Blocks.ToList().FindIndex(item => item.Text.Contains("aguja")));
        Assert.True(block >= 0);
        fx.Read(() => Assert.Contains("Buscar aguja", string.Concat(fx.App.Conversation!.GetAllLines()[fx.App.Conversation.CurrentRow + 1].Select(cell => cell.Grapheme))));
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
        Assert.Empty(recorded.Commands);
        Assert.Equal("borrador que no se debe enviar", fx.Read(() => fx.App.Composer!.Text));
        OpenConversationSearch(fx); fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null && fx.App.Composer!.HasFocus, "Esc conserva borrador");
    }, noColor: false);

    [Fact]
    public void Conversation_search_is_reachable_from_palette_and_User_remap_with_empty_and_long_queries() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, "version: 1\nbindings:\n  - action: -session.search\n  - keys: F9\n    action: session.search\n    when: '!overlayOpen'\n");
        fx.StartTui(); Type(fx, "borrador intacto");
        fx.InjectKey(new Key(KeyCode.F | KeyCode.CtrlMask)); Assert.Null(fx.Read(() => fx.App.Overlay));
        OpenGlobalPalette(fx); PaletteSearch(fx, "session.search");
        fx.Read(() => Assert.Contains("F9", Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.ToList()[0]!.ToString()));
        fx.InjectKey(new Key(KeyCode.Enter)); fx.Wait(() => fx.App.Overlay?.Id == "conversation-search", "paleta abre búsqueda");
        SearchConversation(fx, "sin-coincidencia-123456"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Read(() => Assert.Contains("Sin coincidencias", OverlayText(fx.App.Overlay!)));
        SearchConversation(fx, new string('a', 257));
        fx.Read(() => Assert.Contains("256", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc)); fx.InjectKey(new Key(KeyCode.F9));
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-search", "F9 reasignado");
        Assert.Equal("borrador intacto", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Conversation_search_revalidates_session_and_cannot_replace_permission() => RunTuiTest(fx =>
    {
        SeedSearchText(fx); fx.StartTui(); OpenConversationSearch(fx); SearchConversation(fx, "aguja");
        fx.Invoke(() => Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status));
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Read(() => Assert.Contains("Cambió", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc));
        var interaction = InteractionId.New();
        fx.Invoke(() => fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}", "[{\"id\":\"deny\"}]", "deny"));
        fx.Wait(() => fx.App.Overlay is not null, "permiso pendiente");
        var overlay = fx.Read(() => fx.App.Overlay);
        fx.InjectKey(new Key(KeyCode.F | KeyCode.CtrlMask));
        Assert.Same(overlay, fx.Read(() => fx.App.Overlay));
    });

    [Fact]
    public void Conversation_search_keeps_attachments_and_draft_and_handles_a_growing_transcript() => RunTuiTest(fx =>
    {
        SeedSearchText(fx); SeedSearchText(fx);
        var path = Path.Combine(fx.Root, "search-note.txt"); File.WriteAllText(path, "attachment fixture");
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded, turnHost: new AttachmentLayoutTurn(fx));
        Type(fx, "/attach \"" + path + "\""); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "adjunto preparado");
        fx.InjectKey(new Key(KeyCode.Esc)); Type(fx, "borrador con adjunto");
        var chip = fx.Read(() => Descendants(fx.App.MainWindow!).Single(view => view.Id == "omni-attachments").Text);
        OpenConversationSearch(fx); SearchConversation(fx, "aguja");
        fx.Read(() => Assert.Equal(2, Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.Count));
        fx.InjectKey(new Key(KeyCode.CursorDown));
        fx.Read(() => Assert.Equal(1, Descendants(fx.App.Overlay!).OfType<ListView>().Single().SelectedItem));
        fx.InjectKey(new Key(KeyCode.CursorUp));
        fx.Read(() => Assert.Equal(0, Descendants(fx.App.Overlay!).OfType<ListView>().Single().SelectedItem));
        fx.Invoke(() => { SeedSearchText(fx); fx.App.PollOnce(); });
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        fx.InjectKey(new Key(KeyCode.Enter)); fx.Wait(() => fx.App.Overlay is null, "mensaje original sigue vigente");
        Assert.Equal("borrador con adjunto", fx.Read(() => fx.App.Composer!.Text));
        fx.Read(() =>
        {
            var chips = Descendants(fx.App.MainWindow!).Single(view => view.Id == "omni-attachments");
            Assert.True(chips.Visible); Assert.Equal(chip, chips.Text);
        });
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
        Assert.Empty(recorded.Commands);
    });
}
