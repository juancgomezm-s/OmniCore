using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class InternalModelEscalationCommandTests
{
    [Fact]
    public void Each_escalation_record_has_its_own_exact_ack_range_and_terminal_completion_is_allowed()
    {
        using var setup = new Setup();
        var requested = setup.Server.RecordModelEscalationRequested(setup.Session,
            new ModelEscalationRequested(setup.Run, "m1", "m2", EscalationCause.ContextLimit));
        var requestEvent = AssertSingleAckEvent(setup, requested.Ack, "model.escalation_requested");

        var approved = setup.Server.RecordModelEscalationApproved(setup.Session,
            new ModelEscalationApproved(setup.Run, "m2", "policy:auto"));
        var approvedEvent = AssertSingleAckEvent(setup, approved.Ack, "model.escalation_approved");
        Assert.NotEqual(requestEvent.EventId.Value, approvedEvent.EventId.Value);

        var cancel = setup.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "run.cancel")
            + "," + JsonObj.Field("runId", setup.Run.Value.ToString()) + "}"), CancellationToken.None);
        Assert.Equal("ok", cancel.Status);
        Assert.Equal(RunState.Cancelled, RunProjection.Replay(setup.Session, setup.Run, setup.Codecs,
            setup.Store.ReadFrom(setup.Session, 1)).State);

        var completed = setup.Server.RecordModelEscalationCompleted(setup.Session,
            new ModelEscalationCompleted(setup.Run, "m2"));
        var completedEvent = AssertSingleAckEvent(setup, completed.Ack, "model.escalation_completed");
        Assert.NotEqual(approvedEvent.EventId.Value, completedEvent.EventId.Value);
        Assert.Equal(setup.Run, completedEvent.RunId);
        Assert.Null(completedEvent.TaskId);
        Assert.Null(completedEvent.LaneId);
        Assert.Null(completedEvent.TurnId);
    }

    [Fact]
    public void Existing_command_cause_is_preserved_and_foreign_execution_scope_does_not_contaminate_payload()
    {
        using var setup = new Setup();
        var cause = new CommandCausation(new CommandId(Guid.NewGuid()));
        var foreignScope = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
        using (CausationScope.Begin(cause))
        using (ExecutionScope.Begin(foreignScope))
        {
            var ack = setup.Server.RecordModelEscalationRequested(setup.Session,
                new ModelEscalationRequested(setup.Run, "m1", "m2", EscalationCause.ManualRequest));
            Assert.Equal(cause.CommandId.Value.ToString(), ack.CommandId);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            Assert.Equal(foreignScope, ExecutionScope.Current);
            Assert.Equal(cause, CausationScope.Current);
            var evt = Assert.Single(setup.Store.ReadFrom(setup.Session, ack.FirstSeq!.Value),
                item => item.Sequence == ack.FirstSeq.Value);
            Assert.Equal(cause, evt.Causation);
            Assert.Equal(setup.Run, evt.RunId);
            Assert.Null(evt.TaskId);
            Assert.Null(evt.LaneId);
            Assert.Null(evt.TurnId);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Wrong_session_or_nonexistent_run_is_rejected_without_writing()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var wrongSession = setup.Server.RecordModelEscalationRequested(SessionId.New(),
            new ModelEscalationRequested(setup.Run, "m1", "m2", EscalationCause.ContextLimit));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongSession.Outcome?.Kind);
        Assert.Null(wrongSession.FirstSeq);
        Assert.Null(wrongSession.LastSeq);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));

        var nonexistentRun = setup.Server.RecordModelEscalationApproved(setup.Session,
            new ModelEscalationApproved(RunId.New(), "m2", "policy:auto"));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, nonexistentRun.Outcome?.Kind);
        Assert.Null(nonexistentRun.FirstSeq);
        Assert.Null(nonexistentRun.LastSeq);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Failed_append_propagates_without_row_or_success_ack_and_retry_is_recorded()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        setup.Store.FailNextAppend = true;
        var parent = new EventCausation(new EventId(Guid.NewGuid()));
        var foreign = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        using (CausationScope.Begin(parent))
        using (ExecutionScope.Begin(foreign))
        {
            var failed = setup.Server.RecordModelEscalationApproved(setup.Session,
                new ModelEscalationApproved(setup.Run, "m2", "policy:auto"));
            var exception = Assert.IsType<IOException>(failed.Failure);
            Assert.Equal("error", failed.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, failed.Outcome?.Kind);
            Assert.Null(failed.FirstSeq);
            Assert.Null(failed.LastSeq);
            Assert.Same(exception, Assert.Throws<IOException>(() => failed.ThrowIfFailure()));
            Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
            Assert.Equal(parent, CausationScope.Current);
            Assert.Equal(foreign, ExecutionScope.Current);
            Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));

            var retry = setup.Server.RecordModelEscalationApproved(setup.Session,
                new ModelEscalationApproved(setup.Run, "m2", "policy:auto"));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, retry.Outcome?.Kind);
            Assert.NotNull(retry.FirstSeq);
            Assert.NotNull(retry.LastSeq);
            Assert.Equal(parent, CausationScope.Current);
            Assert.Equal(foreign, ExecutionScope.Current);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    private static DomainEvent AssertSingleAckEvent(Setup setup, CommandAck ack, string type)
    {
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.NotNull(ack.FirstSeq);
        Assert.Equal(ack.FirstSeq, ack.LastSeq);
        var evt = Assert.Single(setup.Store.ReadFrom(setup.Session, ack.FirstSeq!.Value),
            item => item.Sequence == ack.FirstSeq.Value);
        Assert.Equal(type, evt.Type.ToString());
        Assert.Equal(Guid.Parse(ack.CommandId), Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value);
        Assert.Equal(setup.Run, evt.RunId);
        return evt;
    }

    private sealed class Setup : IDisposable
    {
        public TestEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }

        public Setup()
        {
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink());
            var start = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "explore.start")
                + "," + JsonObj.Field("objective", "control escalation records") + "}"), CancellationToken.None);
            Assert.Equal("ok", start.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
        }

        public void Dispose() { }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextAppend { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("controlled failure before event persistence");
            }
            _inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken) =>
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
