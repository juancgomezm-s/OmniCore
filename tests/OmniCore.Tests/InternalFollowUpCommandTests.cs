using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class InternalFollowUpCommandTests
{
    [Fact]
    public void Open_turn_queue_is_causal_and_acknowledges_only_its_event()
    {
        using var setup = new Setup();
        var turn = setup.OpenTurn();
        var before = setup.Store.CurrentSequence(setup.Session);

        var result = setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, setup.Lane,
            "follow-up text", "composer");

        Assert.True(result.Queued);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        var written = setup.Store.ReadFrom(setup.Session, before + 1);
        var queuedEvent = Assert.Single(written, evt => evt.Type.ToString() == "followup.queued");
        var queued = Assert.IsType<FollowUpQueued>(setup.Codecs.Decode(queuedEvent));
        Assert.Equal(turn, queued.TurnId);
        Assert.Equal(setup.Run, written[0].RunId);
        Assert.Equal(setup.Task, written[0].TaskId);
        Assert.Equal(setup.Lane, written[0].LaneId);
        Assert.Equal(result.Ack.FirstSeq, written[0].Sequence);
        Assert.Equal(result.Ack.LastSeq, written[0].Sequence);
        Assert.Equal(Guid.Parse(result.Ack.CommandId),
            Assert.IsType<CommandCausation>(written[0].Causation).CommandId.Value);
        Assert.Single(FollowUpQueue.Pending(setup.Store, setup.Codecs, setup.Session, setup.Run, setup.Lane));
    }

    [Fact]
    public void Pending_interaction_without_open_turn_keeps_null_source_turn()
    {
        using var setup = new Setup();
        var interaction = InteractionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(setup.Run, setup.Task, setup.Lane)))
        {
            new EventStream(setup.Store, setup.Codecs, setup.Session).AppendBatch(new DomainEventPayload[]
            {
                new InteractionRequested(interaction, InteractionKind.PlanApproval,
                    "{}", "[]", "reject", null, setup.Lane, setup.Task, null, 0, 1),
                new RunAwaitingInput(setup.Run, setup.Lane),
            }, DurabilityClass.Standard);
        }

        var result = setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, setup.Lane,
            "after approval", null);

        Assert.True(result.Queued);
        var queuedEvent = Assert.Single(setup.Store.ReadFrom(setup.Session, 1), evt =>
            evt.Type.ToString() == "followup.queued");
        Assert.Null(Assert.IsType<FollowUpQueued>(setup.Codecs.Decode(queuedEvent)).TurnId);
    }

    [Fact]
    public void No_open_turn_is_a_noop_but_invalid_pairs_and_terminal_run_are_rejected()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);

        var noOp = setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, setup.Lane, "prompt", null);

        Assert.False(noOp.Queued);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, noOp.Ack.Outcome?.Kind);
        Assert.Null(noOp.Ack.FirstSeq);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
        AssertRejected(setup.Server.QueueFollowUpPromptCommand(SessionId.New(), setup.Run, setup.Lane, "x", null));
        AssertRejected(setup.Server.QueueFollowUpPromptCommand(setup.Session, RunId.New(), setup.Lane, "x", null));
        AssertRejected(setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, LaneId.New(), "x", null));

        using (ExecutionScope.Begin(new ExecutionScopeState(setup.Run, setup.Task, setup.Lane)))
            new EventStream(setup.Store, setup.Codecs, setup.Session).Append(new RunCancelled(setup.Run),
                DurabilityClass.Standard);
        var beforeTerminalAttempt = setup.Store.CurrentSequence(setup.Session);
        AssertRejected(setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, setup.Lane, "x", null));
        Assert.Equal(beforeTerminalAttempt, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Append_failure_restores_parent_scopes_and_retry_uses_only_requested_identity()
    {
        using var setup = new Setup();
        setup.OpenTurn();
        var before = setup.Store.CurrentSequence(setup.Session);
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
        setup.Store.FailNextAppend = true;

        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            Assert.Throws<IOException>(() => setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run,
                setup.Lane, "retry me", null));
            Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var retry = setup.Server.QueueFollowUpPromptCommand(setup.Session, setup.Run, setup.Lane,
                "retry me", null);
            Assert.True(retry.Queued);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, retry.Ack.Outcome?.Kind);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
            var queued = Assert.Single(setup.Store.ReadFrom(setup.Session, before + 1));
            Assert.Equal(Guid.Parse(retry.Ack.CommandId),
                Assert.IsType<CommandCausation>(queued.Causation).CommandId.Value);
            Assert.Equal(setup.Run, queued.RunId);
            Assert.Equal(setup.Task, queued.TaskId);
            Assert.Equal(setup.Lane, queued.LaneId);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    private static void AssertRejected((bool Queued, CommandAck Ack) result)
    {
        Assert.False(result.Queued);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Ack.Outcome?.Kind);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
    }

    private sealed class Setup : IDisposable
    {
        private readonly string _artifactPath = Path.Combine(Path.GetTempPath(), "omnicore-followup-command-"
            + Guid.NewGuid().ToString("N"));
        public FaultStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public TaskId Task { get; }
        public LaneId Lane { get; }

        public Setup()
        {
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), new FileArtifactStore(_artifactPath));
            var ack = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "explore.start")
                + "," + JsonObj.Field("objective", "follow-up fixture") + "}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Lane = Assert.IsType<LaneId>(Server.LastLaneId());
            Task = new TaskId(RunProjection.Replay(Session, Run, Codecs,
                Store.ReadFrom(Session, 1)).RootTask!.Value);
        }

        public TurnId OpenTurn()
        {
            var turn = TurnId.New();
            using (ExecutionScope.Begin(new ExecutionScopeState(Run, Task, Lane, turn)))
                new EventStream(Store, Codecs, Session).Append(new TurnStarted(turn, Lane), DurabilityClass.Standard);
            return turn;
        }

        public void Dispose()
        {
            if (Directory.Exists(_artifactPath)) Directory.Delete(_artifactPath, recursive: true);
        }
    }

    private sealed class FaultStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextAppend { get; set; }
        public void Append(SessionId sessionId, DomainEvent item, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("controlled append failure before persistence");
            }
            _inner.Append(sessionId, item, durability, cancellationToken);
        }
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> items, DurabilityClass durability,
            CancellationToken cancellationToken) => _inner.AppendBatch(sessionId, items, durability, cancellationToken);
        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
