using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static void OpenGlobalPalette(TuiFixture fx)
    {
        fx.InjectKey(new Key(KeyCode.P | KeyCode.CtrlMask | KeyCode.ShiftMask));
        fx.Wait(() => fx.App.Overlay?.Id == "global-command-palette", "atajo abre paleta global");
    }
    private static void PaletteSearch(TuiFixture fx, string text) => fx.Invoke(() =>
        Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "palette-search").Text = text);

    [Theory]
    [InlineData("es", 80, 25, "Paleta de comandos")]
    [InlineData("en", 130, 38, "Command palette")]
    [InlineData("fr", 80, 25, "Palette de commandes")]
    [InlineData("it", 130, 38, "Tavolozza dei comandi")]
    public void Global_palette_search_is_read_only_localized_and_Enter_dispatches_existing_action(string locale, int width, int height, string title) => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded, initialColumns: width, initialRows: height,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "borrador sin enviar");
        OpenGlobalPalette(fx);
        fx.Read(() => Assert.Contains(title, fx.App.Overlay!.Title));
        PaletteSearch(fx, "model.select");
        fx.Read(() =>
        {
            var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "palette-list");
            Assert.Equal(1, list.Source!.Count);
            Assert.Contains("F3", list.Source.ToList()[0]!.ToString());
            Assert.Empty(recorded.Commands);
            Assert.Equal("borrador sin enviar", fx.App.Composer!.Text);
            Assert.True(Descendants(fx.App.Overlay!).Single(view => view.Id == "palette-search").HasFocus);
        });
        ExportPreferenceFrame(fx, width, height, "global-palette-" + locale, title);
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null && fx.App.Composer!.HasFocus, "Escape vuelve al borrador");
        Assert.Equal("borrador sin enviar", fx.Read(() => fx.App.Composer!.Text));
        OpenGlobalPalette(fx);
        PaletteSearch(fx, "model.select");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "omni-model-palette", "Enter usa el selector F3 existente");
        Assert.Equal("borrador sin enviar", fx.Read(() => fx.App.Composer!.Text));
        Assert.Empty(recorded.Commands);
    }, noColor: false);

    [Fact]
    public void Global_palette_User_remap_and_effective_shortcut_hint_use_the_keyboard_engine() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - action: -palette.open
              - keys: F9
                action: palette.open
                when: input && !overlayOpen
              - action: -model.select
              - keys: F7
                action: model.select
                when: input
            """);
        fx.StartTui();
        fx.InjectKey(new Key(KeyCode.P | KeyCode.CtrlMask | KeyCode.ShiftMask));
        Assert.Null(fx.Read(() => fx.App.Overlay));
        fx.InjectKey(new Key(KeyCode.F9));
        fx.Wait(() => fx.App.Overlay?.Id == "global-command-palette", "reasignación User abre paleta");
        Assert.DoesNotContain(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "unsupported_terminal_binding");
        PaletteSearch(fx, "model.select");
        fx.Read(() => { var text = OverlayText(fx.App.Overlay!); Assert.Contains("F7", text); Assert.Contains("User", text); });
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "omni-model-palette", "acción reasignada conserva destino canónico");
    });

    [Fact]
    public void Global_palette_Host_command_uses_qualified_ID_literal_argv_and_preserves_draft() => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded);
        Type(fx, "otro mensaje pendiente");
        var before = fx.Decoded<UserInputReceived>().Count();
        OpenGlobalPalette(fx); PaletteSearch(fx, "core:explain");
        fx.Invoke(() =>
        {
            var args = Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "palette-arguments");
            args.Text = "\"src/Alpha.cs\""; args.SetFocus();
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => recorded.Commands.Any(command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke"), "comando Host enviado");
        var fields = JsonObj.Parse(Assert.Single(recorded.Commands, command => JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "command.invoke").PayloadJson);
        Assert.Equal("core:explain", fields["name"]); Assert.Equal("Palette", fields["origin"]);
        Assert.Equal("[\"src/Alpha.cs\"]", fields["arguments"]);
        fx.Wait(() => fx.Decoded<UserInputReceived>().Count() == before + 1, "expansión Host usa el pipeline existente");
        Assert.Equal("otro mensaje pendiente", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Global_palette_does_not_replace_permission_or_execute_after_catalog_change() => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded); OpenGlobalPalette(fx); PaletteSearch(fx, "core:explain");
        recorded.Catalog = "{\"catalog\":{\"commands\":[]}}";
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.Empty(recorded.Commands);
        fx.Read(() => Assert.Contains("Cambió", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc));
        var interaction = InteractionId.New();
        fx.Invoke(() =>
        {
            fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}", "[{\"id\":\"deny\"}]", "deny");
            fx.App.PollOnce();
        });
        var overlay = fx.Read(() => fx.App.Overlay);
        fx.InjectKey(new Key(KeyCode.P | KeyCode.CtrlMask | KeyCode.ShiftMask));
        Assert.Same(overlay, fx.Read(() => fx.App.Overlay));
        Assert.Empty(fx.Decoded<InteractionResolved>());
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.Decoded<InteractionResolved>().Any(evt => evt.InteractionId == interaction), "Esc conserva denegación predeterminada");
    });

    [Fact]
    public void Global_palette_cancel_uses_the_same_run_action_and_never_clears_composer() => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded); Type(fx, "conservar después de cancelar");
        OpenGlobalPalette(fx); PaletteSearch(fx, "run.cancel");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.Decoded<RunCancelled>().Any(), "cancelación canónica");
        Assert.Single(fx.Decoded<RunCancelled>());
        Assert.Equal("run.cancel", JsonObj.Parse(Assert.Single(recorded.Commands).PayloadJson)["cmd"]);
        Assert.Equal("conservar después de cancelar", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Global_palette_rejects_a_changed_conversation_and_disabled_or_empty_results() => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded); OpenGlobalPalette(fx); PaletteSearch(fx, "run.cancel");
        fx.Invoke(() =>
        {
            var ack = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.Empty(recorded.Commands);
        fx.Read(() => Assert.Contains("Cambió", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc));
        OpenGlobalPalette(fx); PaletteSearch(fx, "run.cancel");
        fx.Read(() => Assert.False(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "palette-execute").Enabled));
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.Empty(recorded.Commands);
        PaletteSearch(fx, "zzzzzzzzzzzzzzzzzzzzzzzzzzzz");
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.Empty(recorded.Commands);
        fx.Read(() => Assert.Contains("Sin coincidencias", OverlayText(fx.App.Overlay!)));
    });

    [Fact]
    public void Global_palette_arguments_are_not_sent_when_quotes_are_unfinished() => RunTuiTest(fx =>
    {
        var recorded = new PaletteRecordingClient(fx.Server);
        fx.StartTui(client: recorded); OpenGlobalPalette(fx); PaletteSearch(fx, "core:explain");
        fx.Invoke(() =>
        {
            var field = Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "palette-arguments");
            field.Text = "\"argumento incompleto"; field.SetFocus();
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.Empty(recorded.Commands);
        fx.Read(() => Assert.Contains("comillas", OverlayText(fx.App.Overlay!)));
    });

    private sealed class PaletteRecordingClient(IOmniClient inner) : IOmniClient, ITrustedUserActionClient
    {
        internal string? Catalog;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<WireEnvelope> Commands = new();
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        { Commands.Enqueue(command); return inner.Send(command, cancellationToken); }
        public CommandAck SendUserAction(WireEnvelope command, CancellationToken cancellationToken) =>
            ((ITrustedUserActionClient)inner).SendUserAction(command, cancellationToken);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) =>
            name == "commands" && Catalog is not null ? new(name, Catalog) : inner.Query(name, cancellationToken);
    }
}
