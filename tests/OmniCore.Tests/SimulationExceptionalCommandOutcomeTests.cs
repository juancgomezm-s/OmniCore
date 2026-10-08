using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class SimulationExceptionalCommandOutcomeTests
{
    [Fact]
    public void Sim_audit_failure_after_durable_execution_returns_accepted_outcome_and_exact_command_range()
    {
        var store = new InMemoryEventStore();
        var server = new OmniServer(store, EventCodecs.Create(), new ThrowingAuditSink());
        var commandId = Ids.NewV7();

        var ack = server.Send(WireEnvelope.Command(commandId, "{\"cmd\":\"sim\"}"),
            CancellationToken.None);

        Assert.Equal(commandId, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.DoesNotContain("private-command-error-marker", ack.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);

        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var persisted = store.ReadFrom(session, 1);
        Assert.NotEmpty(persisted);
        var command = new CommandId(Guid.Parse(commandId));
        var caused = persisted.Where(evt => evt.Causation is CommandCausation cause
            && cause.CommandId == command).ToArray();
        Assert.NotEmpty(caused);
        Assert.Equal(caused.Min(evt => evt.Sequence), ack.FirstSeq);
        Assert.Equal(caused.Max(evt => evt.Sequence), ack.LastSeq);
        Assert.Equal(persisted.Count, caused.Length);
    }

    [Fact]
    public void Sim_resume_store_failure_before_any_new_write_returns_rejected_without_range()
    {
        var store = new FailOnceStore();
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
        var startId = Ids.NewV7();
        var start = server.Send(WireEnvelope.Command(startId,
            "{\"cmd\":\"sim\",\"scenario\":\"with-tool-crash\"}"), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, start.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        Assert.Contains(store.ReadFrom(session, 1), evt => EventCodecs.Create().Decode(evt) is ToolCallStarted);
        var sequenceBefore = store.CurrentSequence(session);

        var resumeId = Ids.NewV7();
        store.FailNextAppend = true;
        var ack = server.Send(WireEnvelope.Command(resumeId, "{\"cmd\":\"sim.resume\"}"),
            CancellationToken.None);

        Assert.Equal(resumeId, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));
        Assert.Empty(store.ReadFrom(session, sequenceBefore + 1));
    }

    [Fact]
    public void Partial_resume_reports_persisted_unknown_and_retry_does_not_duplicate_it()
    {
        var store = new FailOnceStore();
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        var start = server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"sim\",\"scenario\":\"with-tool-crash\"}"), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, start.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var before = store.CurrentSequence(session);
        store.SuccessfulWritesBeforeFailure = 1;
        var failedId = Ids.NewV7();
        var failed = server.Send(WireEnvelope.Command(failedId, "{\"cmd\":\"sim.resume\"}"), CancellationToken.None);
        Assert.Equal("error", failed.Status);
        Assert.Equal(failedId, failed.CommandId);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, failed.Outcome?.Kind);
        var unknown = Assert.Single(store.ReadFrom(session, before + 1));
        var payload = Assert.IsType<ToolCallEffectUnknown>(codecs.Decode(unknown));
        Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(failedId))), unknown.Causation);
        Assert.Equal(unknown.Sequence, failed.FirstSeq);
        Assert.Equal(unknown.Sequence, failed.LastSeq);

        var retryId = Ids.NewV7();
        var retry = server.Send(WireEnvelope.Command(retryId, "{\"cmd\":\"sim.resume\"}"), CancellationToken.None);
        Assert.Equal("ok", retry.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, retry.Outcome?.Kind);
        var events = store.ReadFrom(session, 1);
        Assert.Single(events, evt => codecs.Decode(evt) is ToolCallEffectUnknown item && item.ToolCallId == payload.ToolCallId);
        var reconciled = Assert.Single(events, evt => codecs.Decode(evt) is ToolCallReconciled item && item.ToolCallId == payload.ToolCallId);
        Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(retryId))), reconciled.Causation);
        Assert.True(retry.FirstSeq > failed.LastSeq);
        CanonicalStateTracker.Replay(codecs, events);
    }

    [Fact]
    public void Partial_new_simulation_does_not_retain_the_previous_sessions_run_identity()
    {
        var store = new FailOnceStore();
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
        var initial = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, initial.Outcome?.Kind);
        var previousSession = Assert.IsType<SessionId>(server.LastSessionId());
        Assert.NotNull(server.LastRunId());
        store.SuccessfulWritesBeforeFailure = 1; // SessionCreated persists; RunCreated fails.
        var commandId = Ids.NewV7();
        var failed = server.Send(WireEnvelope.Command(commandId, "{\"cmd\":\"sim\"}"), CancellationToken.None);
        Assert.Equal("error", failed.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, failed.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        Assert.NotEqual(previousSession, session);
        var created = Assert.Single(store.ReadFrom(session, 1));
        Assert.IsType<SessionCreated>(EventCodecs.Create().Decode(created));
        Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(commandId))), created.Causation);
        Assert.Equal(created.Sequence, failed.FirstSeq);
        Assert.Equal(created.Sequence, failed.LastSeq);
        Assert.Null(server.LastRunId());
        var resume = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim.resume\"}"), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, resume.Outcome?.Kind);
        Assert.Null(resume.FirstSeq);
        Assert.Null(resume.LastSeq);
        Assert.Equal(1, store.CurrentSequence(session));
    }

    private sealed class ThrowingAuditSink : IAuditSink
    {
        public void Record(AuditRecord record, CancellationToken cancellationToken) =>
            throw new IOException("controlled audit sink failure after event persistence; Bearer private-command-error-marker");
    }

    private sealed class FailOnceStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextAppend { get; set; }
        public int? SuccessfulWritesBeforeFailure { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (FailNextAppend || SuccessfulWritesBeforeFailure == 0)
            {
                FailNextAppend = false;
                SuccessfulWritesBeforeFailure = null;
                throw new IOException("controlled store failure before append");
            }
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
            if (SuccessfulWritesBeforeFailure is { } remaining) SuccessfulWritesBeforeFailure = remaining - 1;
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequence) =>
            _inner.ReadFrom(sessionId, fromSequence);
    }
}
