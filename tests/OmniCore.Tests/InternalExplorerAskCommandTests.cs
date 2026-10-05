using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class InternalExplorerAskCommandTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null, new[]
    {
        new QuestionField("choice", "Choice", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("yes", "Yes", null), new QuestionOption("no", "No", null) },
            null, true, null, null, null),
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-internal-ask-" + Guid.NewGuid().ToString("N"));
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public InMemoryEventStore Store { get; }
        public EventCodecs Codecs { get; }
        public int ProviderCalls;
        public ExplorerTurn Turn { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "blobs"));
            Server = OmniHost.CreateInMemoryServer();
            var input = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "session.input")
                + "," + JsonObj.Field("text", "ask objective") + "," + JsonObj.Field("mode", "plan") + "}"),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", input.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Lane = Assert.IsType<LaneId>(Server.LastLaneId());
            Store = Assert.IsType<InMemoryEventStore>(Server.AcquireStore());
            Codecs = EventCodecs.Create();

            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), Root);
            var artifacts = new FileArtifactStore(Path.Combine(Root, "blobs"));
            var questionnaires = new QuestionnaireInteractionService(Store, Codecs, artifacts);
            Turn = new ExplorerTurn((_, _) =>
            {
                ProviderCalls++;
                return new ModelResponse(new ContentBlock[]
                {
                    new ToolCallBlock(ToolCallId.New(), "provider-ask", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema)),
                }, StopReason.ToolUse, new TokenUsage(2, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "test", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M5.5"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                Store, Codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaires);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Fact]
    public void Ask_that_suspends_for_questionnaire_has_one_internal_causation_and_accepted_ack()
    {
        using var fx = new Fixture();
        var before = fx.Store.CurrentSequence(fx.Session);

        var execution = fx.Server.ExecuteAskTurn(fx.Session, fx.Run,
            token => fx.Turn.Ask("ask the user", "system", fx.Session, fx.Run, fx.Lane, "", token),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.Ack.Outcome?.Kind);
        Assert.Equal("ok", execution.Ack.Status);
        Assert.Null(execution.Ack.Error);
        Assert.NotNull(execution.Ack.FirstSeq);
        Assert.NotNull(execution.Ack.LastSeq);
        Assert.Equal(StopReason.InputRequired, execution.Result?.StopReason);
        Assert.Equal(1, fx.ProviderCalls);
        Assert.Null(CausationScope.Current);

        var commandId = Guid.Parse(execution.Ack.CommandId);
        var events = fx.Store.ReadFrom(fx.Session, before + 1);
        Assert.NotEmpty(events);
        Assert.All(events, evt =>
        {
            var causation = Assert.IsType<CommandCausation>(evt.Causation);
            Assert.Equal(commandId, causation.CommandId.Value);
        });
        Assert.Equal(events.Min(evt => evt.Sequence), execution.Ack.FirstSeq);
        Assert.Equal(events.Max(evt => evt.Sequence), execution.Ack.LastSeq);
        Assert.Contains(events, evt => evt.Type.ToString() == "interaction.requested");
        Assert.Contains(events, evt => evt.Type.ToString() == "run.awaiting_input");
        Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(fx.Session, fx.Run, fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1)).State);
    }

    [Fact]
    public void Ambient_command_causation_is_preserved_and_restored()
    {
        using var fx = new Fixture();
        var commandId = new CommandId(Guid.NewGuid());
        var cause = new CommandCausation(commandId);
        using (CausationScope.Begin(cause))
        {
            var execution = fx.Server.ExecuteAskTurn(fx.Session, fx.Run,
                token =>
                {
                    Assert.Equal(cause, CausationScope.Current);
                    Assert.Equal(TestContext.Current.CancellationToken, token);
                    return new ExplorerTurn.TurnResult("done", StopReason.EndTurn, 0,
                        new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
                }, TestContext.Current.CancellationToken);

            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.Ack.Outcome?.Kind);
            Assert.Equal(commandId.Value.ToString(), execution.Ack.CommandId);
            Assert.Null(execution.Ack.FirstSeq);
            Assert.Null(execution.Ack.LastSeq);
            Assert.Equal(cause, CausationScope.Current);
        }

        Assert.Null(CausationScope.Current);
    }

    [Fact]
    public void Existing_command_scope_owns_appended_event_and_ack_range()
    {
        using var fx = new Fixture();
        var before = fx.Store.CurrentSequence(fx.Session);
        var commandId = new CommandId(Guid.NewGuid());
        var cause = new CommandCausation(commandId);

        using (CausationScope.Begin(cause))
        {
            var execution = fx.Server.ExecuteAskTurn(fx.Session, fx.Run, _ =>
            {
                new EventStream(fx.Store, fx.Codecs, fx.Session).Append(
                    new UserInputReceived(fx.Run, "[\"visible\"]", null));
                return new ExplorerTurn.TurnResult("done", StopReason.EndTurn, 0,
                    new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
            }, TestContext.Current.CancellationToken);

            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, execution.Ack.Outcome?.Kind);
            Assert.Equal(commandId.Value.ToString(), execution.Ack.CommandId);
            Assert.Equal(before + 1, execution.Ack.FirstSeq);
            Assert.Equal(before + 1, execution.Ack.LastSeq);
            Assert.Equal(cause, CausationScope.Current);
        }

        var written = Assert.Single(fx.Store.ReadFrom(fx.Session, before + 1));
        Assert.Equal(cause, written.Causation);
        Assert.Null(CausationScope.Current);
    }

    [Fact]
    public void Mismatched_session_run_or_terminal_run_is_rejected_without_callback()
    {
        using var fx = new Fixture();
        var calls = 0;
        var rejected = fx.Server.ExecuteAskTurn(SessionId.New(), fx.Run, _ =>
        {
            calls++;
            return new ExplorerTurn.TurnResult("should not execute", StopReason.EndTurn, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
        }, TestContext.Current.CancellationToken);

        Assert.Null(rejected.Result);
        Assert.Equal("error", rejected.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, rejected.Ack.Outcome?.Kind);
        Assert.Null(rejected.Ack.FirstSeq);
        Assert.Null(rejected.Ack.LastSeq);
        Assert.Equal(0, calls);
        Assert.Null(CausationScope.Current);

        var wrongRun = fx.Server.ExecuteAskTurn(fx.Session, RunId.New(), _ =>
        {
            calls++;
            return new ExplorerTurn.TurnResult("should not execute", StopReason.EndTurn, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongRun.Ack.Outcome?.Kind);
        Assert.Null(wrongRun.Ack.FirstSeq);
        Assert.Null(wrongRun.Ack.LastSeq);
        Assert.Equal(0, calls);

        var cancelPayload = "{" + JsonObj.Field("cmd", "run.cancel") + ","
            + JsonObj.Field("runId", fx.Run.Value.ToString()) + "}";
        Assert.Equal("ok", fx.Server.Send(WireEnvelope.Command(Ids.NewV7(), cancelPayload),
            TestContext.Current.CancellationToken).Status);
        var terminal = fx.Server.ExecuteAskTurn(fx.Session, fx.Run, _ =>
        {
            calls++;
            return new ExplorerTurn.TurnResult("should not execute", StopReason.EndTurn, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, terminal.Ack.Outcome?.Kind);
        Assert.Null(terminal.Ack.FirstSeq);
        Assert.Null(terminal.Ack.LastSeq);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Callback_exception_and_cancellation_are_not_converted_to_success_and_scope_restores()
    {
        using var fx = new Fixture();
        var before = fx.Store.CurrentSequence(fx.Session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var parentCause = new CommandCausation(new CommandId(Guid.NewGuid()));
        using (CausationScope.Begin(parentCause))
        {
            var exception = Assert.Throws<OperationCanceledException>(() =>
                fx.Server.ExecuteAskTurn(fx.Session, fx.Run, token =>
            {
                Assert.Equal(cancellation.Token, token);
                new EventStream(fx.Store, fx.Codecs, fx.Session).Append(
                    new UserInputReceived(fx.Run, "[\"before failure\"]", null));
                throw new OperationCanceledException(token);
                }, cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(before + 1, fx.Store.CurrentSequence(fx.Session));
            var appended = Assert.Single(fx.Store.ReadFrom(fx.Session, before + 1));
            Assert.Equal(parentCause, appended.Causation);
            Assert.Equal(parentCause, CausationScope.Current);
        }

        Assert.Null(CausationScope.Current);
    }
}
