using System.Collections.Concurrent;
using OmniCore.Domain;
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
