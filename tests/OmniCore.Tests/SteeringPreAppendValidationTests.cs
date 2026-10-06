using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// ADR-0001 §3: invalid steering transitions must be rejected by EventStream before persistence.
/// These tests deliberately use direct appends rather than SteeringQueue projection validation.
/// </summary>
public sealed class SteeringPreAppendValidationTests
{
    [Fact]
    public void Direct_append_rejects_duplicate_received_without_changing_journal_or_live_state()
    {
        var fx = new Fixture();
        var received = fx.Received();
        fx.Append(received);

        fx.AssertRejectedWithoutMutation(() => fx.Append(received));
    }

    [Theory]
    [InlineData("closed-turn")]
    [InlineData("terminal-run")]
    [InlineData("unknown-run")]
    [InlineData("unknown-lane")]
    [InlineData("unknown-turn")]
    public void Direct_append_rejects_received_outside_live_scope(string invalidScope)
    {
        var fx = new Fixture();
        var received = fx.Received();
        switch (invalidScope)
        {
            case "closed-turn":
                fx.Append(new TurnCompleted(fx.Turn));
                break;
            case "terminal-run":
                fx.Append(new RunFailed(fx.Run, "terminal for validation test"));
                break;
            case "unknown-run":
                received = received with { RunId = RunId.New() };
                break;
            case "unknown-lane":
                received = received with { LaneId = LaneId.New() };
                break;
            case "unknown-turn":
                received = received with { TurnId = TurnId.New() };
                break;
        }

        fx.AssertRejectedWithoutMutation(() => fx.Append(received));
    }

    [Theory]
    [InlineData("apply-without-receive")]
    [InlineData("negative-step")]
    [InlineData("double-apply")]
    [InlineData("cross-scope-apply")]
    public void Direct_append_rejects_invalid_applied_transition(string invalid)
    {
        var fx = new Fixture();
        var received = fx.Received();
        var applied = new TurnSteeringApplied(received.SteeringId, fx.Run, fx.Lane, fx.Turn, 0);
        if (invalid is "negative-step" or "double-apply" or "cross-scope-apply")
            fx.Append(received);
        if (invalid == "double-apply")
            fx.Append(applied);
        if (invalid == "negative-step")
            applied = applied with { StepIndex = -1 };
        if (invalid == "cross-scope-apply")
            applied = applied with { LaneId = LaneId.New() };

        fx.AssertRejectedWithoutMutation(() => fx.Append(applied));
    }

    [Theory]
    [InlineData("drop-without-receive")]
    [InlineData("empty-reason")]
    [InlineData("double-drop")]
    public void Direct_append_rejects_invalid_dropped_transition(string invalid)
    {
        var fx = new Fixture();
        var received = fx.Received();
        var dropped = new TurnSteeringDropped(received.SteeringId, fx.Run, fx.Lane, fx.Turn, "turn ended");
        if (invalid is "empty-reason" or "double-drop")
            fx.Append(received);
        if (invalid == "double-drop")
            fx.Append(dropped);
        if (invalid == "empty-reason")
            dropped = dropped with { Reason = "" };

        fx.AssertRejectedWithoutMutation(() => fx.Append(dropped));
    }

    [Fact]
    public void Invalid_batch_with_duplicate_received_ids_is_atomic()
    {
        var fx = new Fixture();
        var received = fx.Received();
        fx.AssertRejectedWithoutMutation(() => fx.AppendBatch([received, received]));
    }

    [Fact]
    public void Invalid_batch_does_not_consume_prior_received_item()
    {
        var fx = new Fixture();
        var received = fx.Received();
        fx.Append(received);
        var applied = new TurnSteeringApplied(received.SteeringId, fx.Run, fx.Lane, fx.Turn, 3);
        var duplicate = new TurnSteeringApplied(received.SteeringId, fx.Run, fx.Lane, fx.Turn, 3);

        fx.AssertRejectedWithoutMutation(() => fx.AppendBatch([applied, duplicate]));
        fx.Append(applied); // A failed validation clone must not have consumed the original pending entry.
        fx.AssertRejectedWithoutMutation(() => fx.Append(duplicate));
    }

    private sealed class Fixture
    {
        private readonly InMemoryEventStore _store = new();
        private readonly EventCodecs _codecs = EventCodecs.Create();
        private readonly SessionId _session = SessionId.New();
        private readonly EventStream _stream;

        public RunId Run { get; }
        public TaskId Task { get; }
        public LaneId Lane { get; }
        public TurnId Turn { get; }

        public Fixture()
        {
            _stream = new EventStream(_store, _codecs, _session);
            var opened = TestRun.Open(_stream, _session, "steering pre-append validation");
            Run = opened.RunId;
            Task = opened.RootTask;
            Lane = opened.RootLane;
            Turn = TurnId.New();
            Append(new TurnStarted(Turn, Lane));
        }

        public TurnSteeringReceived Received() => new(SteeringId.New(), Run, Lane, Turn, "[\"steer\"]");

        public void Append(DomainEventPayload payload)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run, Task, Lane, Turn)))
                _stream.Append(payload, DurabilityClass.Standard);
        }

        public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run, Task, Lane, Turn)))
                _stream.AppendBatch(payloads, DurabilityClass.Standard);
        }

        public void AssertRejectedWithoutMutation(Action append)
        {
            var sequenceBefore = _store.CurrentSequence(_session);
            var stateBefore = _stream.LiveState().Snapshot();
            Assert.Throws<InvalidStateTransitionException>(append);
            Assert.Equal(sequenceBefore, _store.CurrentSequence(_session));
            Assert.Equal(stateBefore, _stream.LiveState().Snapshot());
        }
    }
}
