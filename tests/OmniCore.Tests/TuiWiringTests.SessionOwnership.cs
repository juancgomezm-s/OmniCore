using OmniCore.Host;
using OmniCore.Domain;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class OccupiedSessionTurn(TuiFixture fixture) : ITuiTurnHost
    {
        internal bool Occupied = true;
        internal Exception? AdmissionFailure;
        internal readonly TestTurn Turn = new(fixture);
        public bool TryAcquireSessionWriter() => AdmissionFailure is { } error ? throw error : !Occupied;
        public System.Threading.Tasks.Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken token) =>
            Turn.ExecuteAsync(input, diagnostics, token);
    }

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Occupied_session_keeps_draft_and_chat_alive_and_explicit_retry_submits_once(int width, int height) => RunTuiTest(fx =>
    {
        var host = new OccupiedSessionTurn(fx);
        host.Turn.Release.Set();
        var journalCount = fx.Server.SubscribeSince(0).Count;
        fx.StartTui(turnHost: host, initialColumns: width, initialRows: height);
        const string prompt = "conserva este mensaje";
        Type(fx, prompt);
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "busy notice shown");
        Assert.Equal(prompt, fx.App.Composer!.Text);
        Assert.Contains("Otra ventana", OverlayText(fx.App.Overlay!));
        Assert.DoesNotContain("session_writer_owned", OverlayText(fx.App.Overlay!));
        Assert.Equal(0, host.Turn.Calls);
        Assert.Equal(journalCount, fx.Server.SubscribeSince(0).Count);
        Assert.False(fx.App.Activity!.Visible);
        ExportPreferenceFrame(fx, width, height, "session-writer-busy", "Después vuelve a enviar tu mensaje aquí.");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null, "accepting the notice returns to chat");
        Assert.Equal(prompt, fx.App.Composer.Text);
        Assert.True(fx.Loop.IsAlive);
        Assert.Null(fx.LoopError);
        host.Occupied = false;
        KeyWithEffect(fx, KeyCode.Enter, () => host.Turn.Calls == 1, "explicit retry submits after ownership is available");
        fx.Wait(() => fx.App.Conversation!.Text.Contains("respuesta conectada"), "retry succeeds in the same TUI");
        Assert.Single(fx.Decoded<UserInputReceived>(), input => input.InputPartsJson.Contains(prompt, StringComparison.Ordinal));
        Assert.Equal("", fx.App.Composer.Text);
        Assert.True(fx.Loop.IsAlive);
    }, noColor: false);

    [Fact]
    public void Session_admission_storage_error_preserves_the_draft_and_returns_to_the_running_chat() => RunTuiTest(fx =>
    {
        var host = new OccupiedSessionTurn(fx) { AdmissionFailure = new IOException("La sesión no se pudo leer.") };
        fx.StartTui(turnHost: host);
        Type(fx, "borrador intacto");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "storage error is shown without leaving TUI");
        Assert.Contains("La sesión no se pudo leer.", OverlayText(fx.App.Overlay!));
        Assert.Equal("borrador intacto", fx.App.Composer!.Text);
        Assert.Equal(0, host.Turn.Calls);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null, "accept returns to chat");
        Assert.True(fx.Loop.IsAlive);
        Assert.Null(fx.LoopError);
        Assert.Equal("borrador intacto", fx.App.Composer.Text);
    });
}
