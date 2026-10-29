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
    private static void SeedRecoveryHistory(TuiFixture fx)
    {
        var lane = fx.Decoded<LaneCreated>().Last().LaneId;
        var content = fx.Artifacts.PutText(string.Join('\n', Enumerable.Range(0, 160)
            .Select(index => "Línea del historial " + index)) + "\nULTIMO_MENSAJE",
            "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
        new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!)
            .Append(new AssistantMessageRecorded(fx.Server.LastRunId()!, lane, TurnId.New(), content));
    }

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Recovered_conversation_starts_at_end_without_an_extra_message(int width, int height) => RunTuiTest(fx =>
    {
        SeedRecoveryHistory(fx);
        fx.StartTui(initialColumns: width, initialRows: height);
        fx.Wait(() => fx.App.Conversation!.Text.Contains("ULTIMO_MENSAJE"), "historial recuperado");
        fx.Read(() => Assert.True(fx.App.Conversation!.Viewport.Bottom >= fx.App.Conversation.Lines - 1,
            $"viewport={fx.App.Conversation.Viewport}; lines={fx.App.Conversation.Lines}"));
        Assert.True(fx.App.Composer!.HasFocus);
        Assert.True(fx.Loop.IsAlive);
        ExportPreferenceFrame(fx, width, height, "recovered-tail", "ULTIMO_MENSAJE");
    }, noColor: false);

    [Fact]
    public void Resume_after_reading_older_messages_returns_to_end_and_resize_keeps_it_visible() => RunTuiTest(fx =>
    {
        SeedRecoveryHistory(fx);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Invoke(() => { fx.App.Conversation!.SetFocus(); fx.App.Conversation.MoveHome(); });
        fx.InjectKey(new Key(KeyCode.PageUp));
        fx.Invoke(() => fx.App.Composer!.SetFocus());
        Type(fx, "/retomar " + fx.Server.LastSessionId());
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer!.Text.Length == 0, "sesión retomada");
        fx.Wait(() => fx.Read(() => fx.App.Conversation!.Viewport.Bottom >= fx.App.Conversation.Lines - 1), "último mensaje visible");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 80, "reflow estrecho");
        fx.Wait(() => fx.Read(() => fx.App.Conversation!.Viewport.Bottom >= fx.App.Conversation.Lines - 1), "último mensaje tras resize");
    });

    [Fact]
    public void Hola_then_repeated_model_switch_and_escape_keeps_the_same_chat_alive() => RunTuiTest(fx =>
    {
        var models = new[] { new ModelRegistryModelDescriptor("model-a", "local", 8192, 8192, 2048),
            new ModelRegistryModelDescriptor("model-b", "local", 8192, 8192, 2048) };
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "switch-policies"), models);
        var turn = new TestTurn(fx);
        turn.Release.Set();
        fx.StartTui(policies: policies, turnHost: turn, initialColumns: 120, initialRows: 40);
        var session = fx.Server.LastSessionId();
        Type(fx, "hola");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Conversation!.Text.Contains("respuesta conectada"), "respuesta a hola");
        for (var cycle = 0; cycle < 8; cycle++)
        {
            KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "selector");
            var model = cycle % 2 == 0 ? "model-b" : "model-a";
            fx.Invoke(() =>
            {
                var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
                list.SelectedItem = list.Source!.ToList().Cast<object>().ToList().FindIndex(row => row.ToString()!.Contains(model));
                list.SetFocus();
            });
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.Overlay?.Id == "reasoning-preferences" && fx.App.Header!.Text.Contains(model),
                "modelo cambiado y esfuerzo ofrecido");
            fx.InjectKey(new Key(KeyCode.Esc));
            fx.Wait(() => fx.App.Overlay is null, "omitir esfuerzo conserva el chat");
            // Esc after selecting must not fall through to Terminal.Gui's default Quit.
            fx.InjectKey(new Key(KeyCode.Esc));
            fx.Invoke(() => Assert.True(fx.App.PollOnce()));
            Assert.True(fx.Loop.IsAlive);
            Assert.Null(fx.LoopError);
            Assert.Equal(session, fx.Server.LastSessionId());
            Assert.Contains("respuesta conectada", fx.App.Conversation!.Text);
            if (cycle == 0) ExportPreferenceFrame(fx, 120, 40, "model-switch-alive", "model-b");
        }
        Assert.Equal(1, turn.Calls);
        Assert.Single(fx.Decoded<UserInputReceived>(), input => input.InputPartsJson.Contains("hola"));
    }, noColor: false);

    [Fact]
    public void Escape_interrupts_generation_without_exiting_and_explicit_exit_still_works() => RunTuiTest(fx =>
    {
        var turn = new TestTurn(fx);
        fx.StartTui(turnHost: turn);
        Type(fx, "hola");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => turn.Started.IsSet, "generación en curso");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Status!.Text.Contains(new Localization("es").Resolve("shell.cancelled")), "generación interrumpida");
        fx.Wait(() => fx.App.Conversation!.Text.Contains("hola", StringComparison.Ordinal), "el input cancelado llega a la proyección de conversación");
        Assert.True(fx.Loop.IsAlive);
        Assert.Null(fx.LoopError);
        Assert.Contains("hola", fx.App.Conversation!.Text);
        Type(fx, "/exit");
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.True(fx.Loop.Join(10000), "solo salir explícitamente debe terminar la TUI");
        Assert.Null(fx.LoopError);
    });

    private sealed class DelayedSwitchCatalog : ITuiAccountHost
    {
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IReadOnlyList<AvailableChatGptModel>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChatGptSessionStatus Status() => new(true, "***fixture", null, false);
        public Task<ChatGptSessionStatus> LoginAsync(bool deviceCode, Action<string> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Logout() => throw new NotSupportedException();
        public Task<IReadOnlyList<AvailableChatGptModel>> ListModelsAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            // Deliberately emulate a provider returning after cancellation.
            return Release.Task;
        }
    }

    [Fact]
    public void Registered_model_switch_does_not_wait_for_network_discovery_or_apply_a_late_result() => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "slow-discovery"));
        policies.RegisterChatGptModel("cached-a", 8192, 2048);
        policies.RegisterChatGptModel("cached-b", 8192, 2048);
        var account = new DelayedSwitchCatalog();
        fx.StartTui(policies: policies, account: account);
        try
        {
            KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "selector");
            fx.Wait(() => account.Started.Task.IsCompleted, "descubrimiento pendiente");
            fx.Invoke(() =>
            {
                var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
                list.SelectedItem = list.Source!.ToList().Cast<object>().ToList().FindIndex(row => row.ToString()!.Contains("cached-b"));
                list.SetFocus();
            });
            fx.InjectKey(new Key(KeyCode.Enter));
            ConfirmSelectedModelPreferences(fx, "cached-b");
            fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.Contains("cached-b"), "selección sin esperar la red");
            Assert.False(account.Release.Task.IsCompleted);
            account.Release.SetResult([new("cached-a", "A"), new("cached-b", "B")]);
            Thread.Sleep(1100); // Two real production polls deliver/ignore the late callback.
            fx.Invoke(() => Assert.Contains("cached-b", fx.App.Header!.Text));
            Assert.Null(fx.App.Overlay);
            Assert.True(fx.Loop.IsAlive);
            Assert.Null(fx.LoopError);
        }
        finally { account.Release.TrySetResult([]); }
    });
}
