using System.Collections.Concurrent;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Composer_submits_literal_whitespace_to_the_real_session(bool withTurnHost) => RunTuiTest(fx =>
    {
        var draft = "    int café = 42;" + Environment.NewLine + Environment.NewLine
            + "    // keep indentation" + Environment.NewLine + "  ";
        var client = new ComposerAdmissionClient(fx.Server);
        var turn = withTurnHost ? new TestTurn(fx) : null;
        try
        {
            fx.StartTui(client: client, turnHost: turn);
            fx.Invoke(() => fx.App.Composer!.Text = draft);
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => string.IsNullOrEmpty(fx.App.Composer!.Text), "accepted input clears the composer");
            if (turn is not null)
            {
                fx.Wait(() => turn.Started.IsSet, "the local turn receives the input");
                Assert.Equal(draft, turn.Input);
            }
            else Assert.Equal(draft, Assert.Single(client.Inputs));
            var received = fx.Decoded<UserInputReceived>().Last();
            using var parts = System.Text.Json.JsonDocument.Parse(received.InputPartsJson!);
            Assert.Equal(draft, Assert.Single(parts.RootElement.EnumerateArray()).GetString());
        }
        finally { turn?.Release.Set(); }
    });

    [Fact]
    public void Unavailable_reference_keeps_the_literal_draft_without_submitting() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var inputs = fx.Decoded<UserInputReceived>().Count();
        var draft = "@src/Alpha.cs" + Environment.NewLine + "  explain this file  ";
        fx.Invoke(() => fx.App.Composer!.Text = draft);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "reference unavailability is explained");
        Assert.Equal(draft, fx.App.Composer!.Text);
        Assert.Equal(inputs, fx.Decoded<UserInputReceived>().Count());
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "close the explanation");
        fx.Wait(() => fx.App.Composer!.HasFocus, "return to the editable draft");
        Assert.Equal(draft, fx.App.Composer!.Text);
    });

    [Fact]
    public void Unknown_command_keeps_arguments_for_correction_without_sending_chat() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var inputs = fx.Decoded<UserInputReceived>().Count();
        const string draft = "/missing-homologation-command \"C:/some path/file.cs\"  ";
        fx.Invoke(() => fx.App.Composer!.Text = draft);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "the real Host rejects the unknown command");
        Assert.Equal(draft, fx.App.Composer!.Text);
        Assert.Equal(inputs, fx.Decoded<UserInputReceived>().Count());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejected_chat_keeps_the_draft_and_retry_submits_once(bool typedOutcome) => RunTuiTest(fx =>
    {
        var client = new ComposerAdmissionClient(fx.Server) { RejectInput = true, TypedOutcome = typedOutcome };
        fx.StartTui(client: client);
        const string draft = "  retry my original text  ";
        var inputs = fx.Decoded<UserInputReceived>().Count();
        fx.Invoke(() => fx.App.Composer!.Text = draft);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "a rejected send explains the error");
        Assert.Equal(draft, fx.App.Composer!.Text);
        Assert.Equal(inputs, fx.Decoded<UserInputReceived>().Count());
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "close the error");
        client.RejectInput = false;
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => string.IsNullOrEmpty(fx.App.Composer!.Text), "an accepted retry consumes the draft");
        Assert.Equal(new[] { draft, draft }, client.Inputs.ToArray());
        Assert.Equal(inputs + 1, fx.Decoded<UserInputReceived>().Count());
    });

    [Fact]
    public void Act_attachment_failure_keeps_draft_and_chip_then_success_consumes_both() => RunTuiTest(fx =>
    {
        var path = Path.Combine(fx.Root, "act-brief.txt");
        File.WriteAllText(path, "Nota para ACT.");
        var host = new ActAttachmentDraftTurn(fx);
        fx.StartTui(turnHost: host);
        Type(fx, "/attach \"" + path + "\"");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "el Host importa el adjunto");
        var panel = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-composer");
        var chips = panel.SubViews.Single(view => view.Id == "omni-attachments");
        Assert.Contains("act-brief.txt", chips.Text.ToString());
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "cerrar confirmación del adjunto");

        const string draft = "/act resume la nota adjunta";
        fx.Invoke(() => fx.App.Composer!.Text = draft);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => host.CompletedCalls == 1 && fx.App.Overlay is not null,
            "el primer ACT informa el fallo del turno");
        Assert.Equal(draft, fx.App.Composer!.Text);
        Assert.True(chips.Visible);
        Assert.Equal(new[] { "resume la nota adjunta" }, host.Inputs.ToArray());
        Assert.Equal(new[] { "act-brief.txt" }, host.AttachmentNames.ToArray());

        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "cerrar el error conserva el borrador");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => host.CompletedCalls == 2 && string.IsNullOrEmpty(fx.App.Composer!.Text) && !chips.Visible,
            "un ACT completado consume el borrador y el chip");
        Assert.Equal(new[] { "resume la nota adjunta", "resume la nota adjunta" }, host.Inputs.ToArray());
        Assert.Equal(new[] { "act-brief.txt", "act-brief.txt" }, host.AttachmentNames.ToArray());
    });

    private sealed class ActAttachmentDraftTurn(TuiFixture fixture) : ITuiTurnHost
    {
        private readonly ConcurrentQueue<int> _outcomes = new(new[] { 1, 0 });
        public readonly ConcurrentQueue<string> Inputs = new();
        public readonly ConcurrentQueue<string> AttachmentNames = new();
        public int CompletedCalls;

        public TuiAttachmentReference ImportAttachmentFromPath(string path) => new(
            fixture.Artifacts.PutText(File.ReadAllText(path), "text/plain", ArtifactKind.Other, Sensitivity.Normal)
                with { FileName = Path.GetFileName(path) });

        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken cancellationToken) =>
            Task.FromResult(1);

        public Task<int> ExecuteActAsync(string input, IReadOnlyList<TuiAttachmentReference> attachments,
            Action<string> diagnostics, CancellationToken cancellationToken)
        {
            Inputs.Enqueue(input);
            foreach (var attachment in attachments) AttachmentNames.Enqueue(attachment.FileName ?? "");
            var outcome = _outcomes.TryDequeue(out var next) ? next : 1;
            if (outcome != 0) diagnostics("El turno ACT de fixture falló.");
            Interlocked.Increment(ref CompletedCalls);
            return Task.FromResult(outcome);
        }
    }

    private sealed class ComposerAdmissionClient(IOmniClient inner) : IOmniClient
    {
        public readonly ConcurrentQueue<string> Inputs = new();
        public bool RejectInput;
        public bool TypedOutcome;

        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            var payload = JsonObj.Parse(command.PayloadJson);
            if (payload.TryGetValue("cmd", out var name) && name == "session.input")
            {
                Inputs.Enqueue(payload["text"]!);
                if (RejectInput)
                    return new CommandAck(command.MessageId, TypedOutcome ? "ok" : "error",
                        "command.disabled", RuntimeCommandOutcome.Rejected());
            }
            return inner.Send(command, cancellationToken);
        }

        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string queryName, CancellationToken cancellationToken) => inner.Query(queryName, cancellationToken);
    }
}
