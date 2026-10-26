using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static string KeyboardFile(TuiFixture fx, string yaml)
    {
        var directory = Path.Combine(fx.Root, "keyboard-config");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "keybindings.yaml");
        File.WriteAllText(file, yaml);
        return file;
    }

    [Fact]
    public void M10Keyboard_user_remap_reloads_without_changing_draft_or_writing_configuration() => RunTuiTest(fx =>
    {
        var file = KeyboardFile(fx, """
            version: 1
            bindings:
              - action: -sidebar.toggle
              - keys: F7
                action: sidebar.toggle
            """);
        var before = File.ReadAllBytes(file);
        fx.StartTui();
        Type(fx, "borrador pendiente");
        var open = fx.Read(() => fx.App.SidebarOpen);
        fx.InjectKey(new Key(KeyCode.F2));
        Assert.Equal(open, fx.Read(() => fx.App.SidebarOpen));
        fx.InjectKey(new Key(KeyCode.F7));
        fx.Wait(() => fx.App.SidebarOpen != open, "F7 usa configuración User");
        Assert.Equal("borrador pendiente", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(before, File.ReadAllBytes(file));
        Type(fx, "/keybindings");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "diagnósticos accesibles");
        fx.Read(() => Assert.Contains("F7 → sidebar.toggle", OverlayText(fx.App.Overlay!)));
        File.WriteAllText(file, File.ReadAllText(file).Replace("F7", "F8", StringComparison.Ordinal));
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "keybindings-reload").InvokeCommand(Terminal.Gui.Input.Command.Accept));
        fx.Read(() => Assert.Contains("F8 → sidebar.toggle", OverlayText(fx.App.Overlay!)));
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.InjectKey(new Key(KeyCode.F7));
        Assert.Equal(!open, fx.Read(() => fx.App.SidebarOpen));
        fx.InjectKey(new Key(KeyCode.F8));
        fx.Wait(() => fx.App.SidebarOpen == open, "recarga activa la nueva tecla");
    });

    [Fact]
    public void M10Keyboard_chord_dispatches_existing_view_once_and_preserves_composer() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - keys: Ctrl+K Ctrl+P
                command: context
                when: input && !overlayOpen
            """);
        var recorded = new KeyboardRecordingClient(fx.Server);
        fx.StartTui(client: recorded);
        Type(fx, "sin enviar al modelo");
        var inputCount = fx.Decoded<UserInputReceived>().Count();
        fx.InjectKey(new Key(KeyCode.K | KeyCode.CtrlMask));
        Assert.Null(fx.Read(() => fx.App.Overlay));
        fx.InjectKey(new Key(KeyCode.P | KeyCode.CtrlMask));
        fx.Wait(() => fx.App.Overlay is not null, "secuencia abre la vista existente");
        Assert.Equal("sin enviar al modelo", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(inputCount, fx.Decoded<UserInputReceived>().Count());
        Assert.Empty(recorded.RunCommands);
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "Esc vuelve al chat");
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
    });

    [Fact]
    public void M10Keyboard_prefix_is_discarded_on_focus_change_and_esc() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - keys: Ctrl+K F7
                action: sidebar.toggle
            """);
        fx.StartTui();
        var open = fx.Read(() => fx.App.SidebarOpen);
        fx.InjectKey(new Key(KeyCode.K | KeyCode.CtrlMask));
        fx.Invoke(() => fx.App.Conversation!.SetFocus());
        fx.InjectKey(new Key(KeyCode.F7));
        Assert.Equal(open, fx.Read(() => fx.App.SidebarOpen));
        fx.Invoke(() => fx.App.Composer!.SetFocus());
        fx.InjectKey(new Key(KeyCode.K | KeyCode.CtrlMask));
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.InjectKey(new Key(KeyCode.F7));
        Assert.Equal(open, fx.Read(() => fx.App.SidebarOpen));
        fx.InjectKey(new Key(KeyCode.K | KeyCode.CtrlMask));
        fx.InjectKey(new Key(KeyCode.F7));
        fx.Wait(() => fx.App.SidebarOpen != open, "secuencia nueva completa");
    });

    [Fact]
    public void M10Keyboard_conflict_reports_sources_and_runs_neither_action() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - keys: F7
                action: sidebar.toggle
              - keys: F7
                action: model.select
            """);
        fx.StartTui();
        Type(fx, "conservar");
        var open = fx.Read(() => fx.App.SidebarOpen);
        fx.InjectKey(new Key(KeyCode.F7));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "conflicto visible");
        fx.Read(() =>
        {
            Assert.Equal(open, fx.App.SidebarOpen);
            var text = OverlayText(fx.App.Overlay!);
            Assert.Contains("Conflicto", text);
            Assert.Contains("keybindings.yaml:", text);
            Assert.Equal("conservar", fx.App.Composer!.Text);
        });
    });

    [Theory]
    [InlineData("es", "Atajos de teclado", "YAML inválido")]
    [InlineData("en", "Keyboard shortcuts", "Invalid YAML")]
    [InlineData("fr", "Raccourcis clavier", "YAML invalide")]
    [InlineData("it", "Scorciatoie da tastiera", "YAML non valido")]
    public void M10Keyboard_bad_yaml_is_localized_and_keeps_default_keys(string locale, string title, string diagnostic) => RunTuiTest(fx =>
    {
        var file = KeyboardFile(fx, "version: 1\nbindings: [\n");
        fx.StartTui(localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.SidebarOpen, "configuración inválida conserva F2");
        Type(fx, "/keybindings");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "vista accesible en cada idioma");
        fx.Read(() =>
        {
            Assert.Contains(title, fx.App.Overlay!.Title);
            var text = OverlayText(fx.App.Overlay);
            Assert.Contains(diagnostic, text);
            Assert.Contains(file, text);
            Assert.DoesNotContain("keybindings.error.", text);
        });
    });

    [Fact]
    public void M10Keyboard_reserved_esc_cannot_remove_protected_cancel_or_dismiss_approval() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - action: -run.cancel
              - keys: Esc
                action: run.cancel
                when: runActive
              - keys: F7
                action: preferences.open
              - keys: F8
                action: sidebar.toggle
              - keys: F8
                action: model.select
            """);
        var interaction = InteractionId.New();
        fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}",
            "[{\"id\":\"deny\",\"label\":\"Denegar\"}]", "deny");
        fx.StartTui();
        fx.Wait(() => fx.App.Overlay is not null, "permiso pendiente");
        var overlay = fx.Read(() => fx.App.Overlay);
        fx.InjectKey(new Key(KeyCode.F7));
        Assert.Same(overlay, fx.Read(() => fx.App.Overlay));
        fx.InjectKey(new Key(KeyCode.F8));
        Assert.Same(overlay, fx.Read(() => fx.App.Overlay));
        Assert.Empty(fx.Decoded<InteractionResolved>());
        Assert.Contains(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "unsupported_terminal_binding");
        Assert.Contains(fx.App.KeyboardConfiguration.Diagnostics, issue => issue.Code == "protected_restored");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.Decoded<InteractionResolved>().Any(evt => evt.InteractionId == interaction), "Esc usa opción predeterminada");
        Assert.Empty(fx.Decoded<RunCancelled>());
    });

    [Fact]
    public void M10Keyboard_interrupt_then_cancel_use_canonical_run_actions_and_keep_draft() => RunTuiTest(fx =>
    {
        var client = new KeyboardRecordingClient(fx.Server);
        fx.StartTui(client: client);
        Type(fx, "texto que no se debe borrar");
        fx.InjectKey(new Key(KeyCode.C | KeyCode.CtrlMask));
        fx.Wait(() => fx.Decoded<RunAwaitingInput>().Any(), "primera interrupción deja Run esperando");
        Assert.Empty(fx.Decoded<RunCancelled>());
        Assert.Equal("texto que no se debe borrar", fx.Read(() => fx.App.Composer!.Text));
        fx.InjectKey(new Key(KeyCode.C | KeyCode.CtrlMask));
        fx.Wait(() => fx.Decoded<RunCancelled>().Any(), "segunda interrupción cancela Run");
        Assert.Equal(new[] { "run.interrupt", "run.cancel" }, client.RunCommands.ToArray());
        Assert.Equal("texto que no se debe borrar", fx.Read(() => fx.App.Composer!.Text));
        Assert.Single(fx.Decoded<RunCancelled>());
    });

    [Fact]
    public void M10Keyboard_busy_turn_is_interrupted_then_run_is_explicitly_cancelled() => RunTuiTest(fx =>
    {
        var host = new KeyboardControlledTurn(fx);
        var client = new KeyboardRecordingClient(fx.Server);
        fx.StartTui(turnHost: host, client: client);
        try
        {
            Type(fx, "inicia turno");
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => host.Started.IsSet, "Turn local activo");
            Type(fx, "siguiente borrador");
            fx.InjectKey(new Key(KeyCode.C | KeyCode.CtrlMask));
            fx.Wait(() => host.Interrupted.IsSet, "primera tecla cancela generación local");
            Assert.Empty(client.RunCommands);
            Assert.Empty(fx.Decoded<RunCancelled>());
            fx.InjectKey(new Key(KeyCode.C | KeyCode.CtrlMask));
            fx.Wait(() => fx.Decoded<RunCancelled>().Any(), "segunda tecla cancela Run aunque el Turn siga deteniéndose");
            Assert.Equal(new[] { "run.cancel" }, client.RunCommands.ToArray());
            Assert.Equal("siguiente borrador", fx.Read(() => fx.App.Composer!.Text));
            Assert.Single(fx.Decoded<RunCancelled>());
        }
        finally { host.Release.TrySetResult(0); }
    });

    [Theory]
    [InlineData(80, 25)]
    [InlineData(130, 38)]
    public void M10Keyboard_report_fits_terminal_and_preserves_real_color(int width, int height) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: height);
        Type(fx, "/keybindings");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "keybindings-screen", "vista de teclado visible");
        fx.Read(() =>
        {
            var overlay = fx.App.Overlay!;
            Assert.True(overlay.Frame.Right <= width);
            Assert.True(overlay.Frame.Bottom <= height);
            foreach (var view in overlay.SubViews)
                Assert.True(view.Frame.Right <= overlay.Viewport.Width && view.Frame.Bottom <= overlay.Viewport.Height,
                    view.Id + " cabe en pantalla");
        });
        ExportPreferenceFrame(fx, width, height, "m10-keybindings", "Ctrl+C");
    }, noColor: false);

    [Fact]
    public void M10Keyboard_pending_chord_cannot_complete_in_another_conversation() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, """
            version: 1
            bindings:
              - keys: Ctrl+K F7
                action: sidebar.toggle
            """);
        fx.StartTui();
        var original = fx.Server.LastSessionId();
        var open = fx.Read(() => fx.App.SidebarOpen);
        fx.InjectKey(new Key(KeyCode.K | KeyCode.CtrlMask));
        fx.Invoke(() =>
        {
            var ack = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            fx.App.PollOnce();
        });
        Assert.NotEqual(original, fx.Server.LastSessionId());
        fx.InjectKey(new Key(KeyCode.F7));
        Assert.Equal(open, fx.Read(() => fx.App.SidebarOpen));
    });

    private sealed class KeyboardControlledTurn(TuiFixture fixture) : ITuiTurnHost
    {
        internal readonly ManualResetEventSlim Started = new();
        internal readonly ManualResetEventSlim Interrupted = new();
        internal readonly TaskCompletionSource<int> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken)
        {
            fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\"," + JsonObj.Field("text", input) + "}"), cancellationToken);
            using var registration = cancellationToken.Register(() => Interrupted.Set());
            Started.Set();
            return await Release.Task;
        }
    }

    private sealed class KeyboardRecordingClient(IOmniClient inner) : IOmniClient
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> RunCommands = new();
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            var fields = JsonObj.Parse(command.PayloadJson);
            if (fields.GetValueOrDefault("cmd") is "run.interrupt" or "run.cancel") RunCommands.Enqueue(fields["cmd"]);
            return inner.Send(command, cancellationToken);
        }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) => inner.Query(name, cancellationToken);
    }
}
