using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Injected command/gate faults with in-memory persistence; no provider queries.</summary>
public sealed class CompletionCommandFailureOutcomeTests
{
    [Fact]
    public void Gate_exception_after_validation_started_returns_partial_accepted_range_without_completion()
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        var parent = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        var original = new InvalidOperationException("controlled gate failure");
        var calls = 0;

        (bool? Completed, CommandAck Ack, Exception? Failure) result;
        using (CausationScope.Begin(parent))
        using (ExecutionScope.Begin(parentExecution))
        {
            result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
            {
                calls++;
                throw original;
            }, null);

            Assert.Same(original, result.Failure);
            Assert.Null(result.Completed);
            AssertErrorAck(result.Ack);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
            Assert.Equal(parent, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
        }

        Assert.Equal(1, calls);
        var events = setup.Store.ReadFrom(setup.Session, before + 1);
        var causal = CausalEvents(events, result.Ack.CommandId);
        Assert.Contains(causal, evt => evt.Type.ToString() == "run.validation_started");
        Assert.DoesNotContain(events, evt => evt.Type.ToString() == "run.completed");
        Assert.Equal(causal.Min(evt => evt.Sequence), result.Ack.FirstSeq);
        Assert.Equal(causal.Max(evt => evt.Sequence), result.Ack.LastSeq);
        Assert.Equal(RunState.Validating, RunProjection.Replay(setup.Session, setup.Run, setup.Codecs,
            setup.Store.ReadFrom(setup.Session, 1)).State);
    }

    [Fact]
    public void Gate_cancellation_after_validation_started_keeps_durable_outcome_and_does_not_retry_gate()
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        var calls = 0;

        var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
        {
            calls++;
            throw original;
        }, null);

        Assert.Same(original, result.Failure);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        Assert.Equal(1, calls);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(result.Failure).CancellationToken);

        var events = setup.Store.ReadFrom(setup.Session, before + 1);
        var causal = CausalEvents(events, result.Ack.CommandId);
        Assert.Contains(causal, evt => evt.Type.ToString() == "run.validation_started");
        Assert.DoesNotContain(events, evt => evt.Type.ToString() == "run.completed");
        Assert.Equal(causal.Min(evt => evt.Sequence), result.Ack.FirstSeq);
        Assert.Equal(causal.Max(evt => evt.Sequence), result.Ack.LastSeq);
    }

    [Fact]
    public void Failure_before_any_append_returns_rejected_without_range_and_never_calls_gate()
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        setup.Store.FailBeforeNextWrite = true;
        var calls = 0;

        var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);

        Assert.IsType<IOException>(result.Failure);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Ack.Outcome?.Kind);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Equal(0, calls);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
        Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));
    }

    [Fact]
    public void Unreadable_confirmation_after_prefix_returns_deferred_and_preserves_original_failure()
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        var original = new InvalidOperationException("controlled failure after prefix");
        var calls = 0;

        var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
        {
            calls++;
            setup.Store.FailNextRead = true;
            throw original;
        }, null);

        Assert.Same(original, result.Failure);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        Assert.Equal("JournalOutcomeUnavailable", result.Ack.Outcome?.Reason);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Equal(1, calls);

        // The injected confirmation-read fault has been consumed. Inspect storage directly now;
        // the command remains uncertain to its caller, but no command/gate is replayed here.
        var persisted = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Contains(CausalEvents(persisted, result.Ack.CommandId),
            evt => evt.Type.ToString() == "run.validation_started");
        Assert.DoesNotContain(persisted, evt => evt.Type.ToString() == "run.completed");
    }

    [Fact]
    public void Append_that_persists_then_throws_returns_accepted_error_with_exact_prefix_range()
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        setup.Store.ThrowAfterPersistingType = "run.validation_started";
        var gateCalls = 0;

        var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
        {
            gateCalls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);

        Assert.IsType<IOException>(result.Failure);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        Assert.Equal(0, gateCalls); // the append failed from the caller's perspective; do not continue.

        var persisted = setup.Store.ReadFrom(setup.Session, before + 1);
        var causal = CausalEvents(persisted, result.Ack.CommandId);
        Assert.Contains(causal, evt => evt.Type.ToString() == "run.validation_started");
        Assert.DoesNotContain(persisted, evt => evt.Type.ToString() == "run.completed");
        Assert.Equal(causal.Min(evt => evt.Sequence), result.Ack.FirstSeq);
        Assert.Equal(causal.Max(evt => evt.Sequence), result.Ack.LastSeq);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Admission_read_or_checkpoint_failure_is_deferred_without_calling_gate(bool checkpointFailure)
    {
        using var setup = new Fixture();
        var before = setup.Store.CurrentSequence(setup.Session);
        setup.Store.FailNextRead = !checkpointFailure;
        setup.Store.FailNextSequence = checkpointFailure;
        var calls = 0;
        var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run, _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);
        Assert.IsType<IOException>(result.Failure);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        Assert.Equal("JournalOutcomeUnavailable", result.Ack.Outcome?.Reason);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Equal(0, calls);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
        Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Failed_evaluation_of_another_session_does_not_change_selected_host_identity()
    {
        using var setup = new Fixture();
        var otherServer = new OmniServer(setup.Store, setup.Codecs, new InMemoryAuditSink(), setup.Artifacts);
        var start = otherServer.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
            + JsonObj.Field("objective", "other session") + "," + JsonObj.Field("workspace", setup.Root) + "}"),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", start.Status);
        var otherSession = Assert.IsType<SessionId>(otherServer.LastSessionId());
        var otherRun = Assert.IsType<RunId>(otherServer.LastRunId());
        Assert.NotEqual(setup.Session, otherSession);
        var originalBefore = setup.Store.CurrentSequence(setup.Session);
        var otherBefore = setup.Store.CurrentSequence(otherSession);
        var result = setup.Server.CheckRunCompletionAndGate(otherSession, otherRun,
            _ => throw new IOException("other session gate failure"), null);
        Assert.Null(result.Completed);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(setup.Session, setup.Server.LastSessionId());
        Assert.Equal(setup.Run, setup.Server.LastRunId());
        Assert.Equal(originalBefore, setup.Store.CurrentSequence(setup.Session));
        var causal = CausalEvents(setup.Store.ReadFrom(otherSession, otherBefore + 1), result.Ack.CommandId);
        Assert.NotEmpty(causal);
        Assert.All(causal, evt => Assert.Equal(otherRun, evt.RunId));
        Assert.Equal(causal.Min(evt => evt.Sequence), result.Ack.FirstSeq);
        Assert.Equal(causal.Max(evt => evt.Sequence), result.Ack.LastSeq);
        Assert.DoesNotContain(causal, evt => evt.Type.ToString() == "run.completed");
    }

    private static void AssertErrorAck(CommandAck ack)
    {
        Assert.False(string.IsNullOrWhiteSpace(ack.CommandId));
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.NotNull(ack.Outcome);
    }

    private static DomainEvent[] CausalEvents(IEnumerable<DomainEvent> events, string commandId)
    {
        var parsed = Guid.Parse(commandId);
        return events.Where(evt => evt.Causation is CommandCausation cause
            && cause.CommandId.Value == parsed).ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-completion-outcome-"
            + Guid.NewGuid().ToString("N"));
        public TestEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public FileArtifactStore Artifacts { get; }
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), Artifacts);
            var start = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                + JsonObj.Field("objective", "completion outcome fixture") + ","
                + JsonObj.Field("workspace", Root) + "}"), CancellationToken.None);
            Assert.Equal("ok", start.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, start.Outcome?.Kind);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailBeforeNextWrite { get; set; }
        public bool FailNextRead { get; set; }
        public bool FailNextSequence { get; set; }
        public string? ThrowAfterPersistingType { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            ThrowBeforeWriteIfArmed();
            _inner.Append(sessionId, evt, durability, cancellationToken);
            ThrowAfterPersistIfMatched(evt.Type.ToString());
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            ThrowBeforeWriteIfArmed();
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
            foreach (var evt in events) ThrowAfterPersistIfMatched(evt.Type.ToString());
        }

        public long CurrentSequence(SessionId sessionId)
        {
            if (FailNextSequence)
            {
                FailNextSequence = false;
                throw new IOException("controlled checkpoint failure");
            }
            return _inner.CurrentSequence(sessionId);
        }

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        {
            if (FailNextRead)
            {
                FailNextRead = false;
                throw new IOException("controlled confirmation read failure");
            }
            return _inner.ReadFrom(sessionId, fromSequenceInclusive);
        }

        private void ThrowBeforeWriteIfArmed()
        {
            if (!FailBeforeNextWrite) return;
            FailBeforeNextWrite = false;
            throw new IOException("controlled failure before first completion write");
        }

        private void ThrowAfterPersistIfMatched(string eventType)
        {
            if (!StringComparer.Ordinal.Equals(ThrowAfterPersistingType, eventType)) return;
            ThrowAfterPersistingType = null;
            throw new IOException("controlled exception after durable append");
        }
    }
}
