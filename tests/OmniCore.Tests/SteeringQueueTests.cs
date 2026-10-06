namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

public sealed class SteeringQueueTests
{
    [Fact]
    public void Receive_is_redacted_idempotent_and_pending_is_fifo()
    {
        var fixture = new Fixture();
        var first = SteeringId.New();
        var second = SteeringId.New();

        Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, first,
            fixture.Run, fixture.Lane, fixture.Turn, "first key=sk-secret-value", "composer"));
        Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, second,
            fixture.Run, fixture.Lane, fixture.Turn, "second steering", "tui"));
        var receivedEvents = fixture.Store.ReadFrom(fixture.Session, 1)
            .Where(evt => fixture.Codecs.Decode(evt) is TurnSteeringReceived).ToArray();
        Assert.Equal(new[] { first, second }, receivedEvents
            .Select(evt => Assert.IsType<TurnSteeringReceived>(fixture.Codecs.Decode(evt)).SteeringId));
        Assert.Equal(fixture.Run, receivedEvents[0].RunId);
        Assert.Equal(fixture.Lane, receivedEvents[0].LaneId);
        Assert.Equal(fixture.Turn, receivedEvents[0].TurnId);
        Assert.DoesNotContain("sk-secret-value", receivedEvents[0].PayloadJson, StringComparison.Ordinal);

        var beforeReplay = fixture.Store.CurrentSequence(fixture.Session);
        Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, first,
            fixture.Run, fixture.Lane, fixture.Turn, "first key=sk-secret-value", "composer"));
        Assert.False(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, first,
            fixture.Run, fixture.Lane, fixture.Turn, "different text", "composer"));
        Assert.Equal(beforeReplay, fixture.Store.CurrentSequence(fixture.Session));

        var pending = SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn);
        Assert.Equal(new[] { first, second }, pending.Select(item => item.Id));
        Assert.Equal(receivedEvents.Select(evt => evt.EventId), pending.Select(item => item.ReceivedEventId));
        Assert.Equal(receivedEvents.Select(evt => evt.Sequence), pending.Select(item => item.Sequence));
    }

    [Fact]
    public void Application_events_are_pure_and_replay_moves_items_to_applied()
    {
        var fixture = new Fixture();
        var id = SteeringId.New();
        Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, id,
            fixture.Run, fixture.Lane, fixture.Turn, "retain this", null));
        var item = Assert.Single(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));

        var payloads = SteeringQueue.ApplicationEvents(new[] { item }, fixture.Run, fixture.Lane,
            fixture.Turn, stepIndex: 3);
        Assert.Single(payloads);
        Assert.IsType<TurnSteeringApplied>(payloads[0]);
        Assert.Single(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));

        fixture.AppendBatch(payloads);
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));
        Assert.Equal(item, Assert.Single(SteeringQueue.Applied(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn)));
    }

    [Fact]
    public void Drop_events_are_pure_and_reason_must_be_nonempty()
    {
        var fixture = new Fixture();
        var id = SteeringId.New();
        Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, id,
            fixture.Run, fixture.Lane, fixture.Turn, "drop me", null));
        var item = Assert.Single(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));

        Assert.Throws<ArgumentException>(() => SteeringQueue.DropEvents(new[] { item }, fixture.Run,
            fixture.Lane, fixture.Turn, "  "));
        var payloads = SteeringQueue.DropEvents(new[] { item }, fixture.Run, fixture.Lane,
            fixture.Turn, "turn ended without another model step");
        Assert.IsType<TurnSteeringDropped>(Assert.Single(payloads));
        Assert.Single(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));
        fixture.AppendBatch(payloads);
        Assert.Empty(SteeringQueue.Pending(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));
        Assert.Empty(SteeringQueue.Applied(fixture.Store, fixture.Codecs, fixture.Session,
            fixture.Run, fixture.Lane, fixture.Turn));
    }

    [Fact]
    public void Receive_uses_target_scope_and_restores_unrelated_ambient_scope()
    {
        var fixture = new Fixture();
        var foreign = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New(),
            ExecutionId: ExecutionId.New());
        var id = SteeringId.New();
        using (ExecutionScope.Begin(foreign))
        {
            Assert.True(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session, id,
                fixture.Run, fixture.Lane, fixture.Turn, "scoped input", "test"));
            Assert.Equal(foreign, ExecutionScope.Current);
        }
        Assert.Null(ExecutionScope.Current);

        var evt = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1),
            value => fixture.Codecs.Decode(value) is TurnSteeringReceived);
        Assert.Equal(fixture.Run, evt.RunId);
        Assert.Equal(fixture.Task, evt.TaskId);
        Assert.Equal(fixture.Lane, evt.LaneId);
        Assert.Equal(fixture.Turn, evt.TurnId);
        Assert.Null(evt.ExecutionId);
    }

    [Fact]
    public void Admission_rejects_unknown_run_closed_turn_and_lane_owned_by_another_run()
    {
        var fixture = new Fixture();
        var unknownRun = RunId.New();
        Assert.False(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session,
            SteeringId.New(), unknownRun, fixture.Lane, fixture.Turn, "no fallback", null));

        var other = new Fixture(fixture.Store, fixture.Codecs, fixture.Session);
        Assert.False(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session,
            SteeringId.New(), fixture.Run, other.Lane, other.Turn, "foreign lane", null));

        fixture.Append(new TurnCompleted(fixture.Turn));
        Assert.False(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session,
            SteeringId.New(), fixture.Run, fixture.Lane, fixture.Turn, "closed", null));

        var terminal = new Fixture(fixture.Store, fixture.Codecs, fixture.Session);
        terminal.Append(new RunFailed(terminal.Run, "terminal test"));
        Assert.False(SteeringQueue.TryReceive(fixture.Store, fixture.Codecs, fixture.Session,
            SteeringId.New(), terminal.Run, terminal.Lane, terminal.Turn, "terminal Run", null));
    }

    [Theory]
    [InlineData("duplicate-receive")]
    [InlineData("apply-before-receive")]
    [InlineData("double-apply")]
    [InlineData("double-drop")]
    [InlineData("negative-step")]
    [InlineData("empty-reason")]
    [InlineData("cross-scope")]
    public void Replay_rejects_invalid_journal(string invalid)
    {
        var fixture = new Fixture();
        var id = SteeringId.New();
        var received = new TurnSteeringReceived(id, fixture.Run, fixture.Lane, fixture.Turn, "[\"safe\"]");
        switch (invalid)
        {
            case "duplicate-receive":
                fixture.Append(received);
                fixture.Append(received);
                break;
            case "apply-before-receive":
                fixture.Append(new TurnSteeringApplied(id, fixture.Run, fixture.Lane, fixture.Turn, 0));
                break;
            case "double-apply":
                fixture.Append(received);
                fixture.Append(new TurnSteeringApplied(id, fixture.Run, fixture.Lane, fixture.Turn, 0));
                fixture.Append(new TurnSteeringApplied(id, fixture.Run, fixture.Lane, fixture.Turn, 1));
                break;
            case "double-drop":
                fixture.Append(received);
                fixture.Append(new TurnSteeringDropped(id, fixture.Run, fixture.Lane, fixture.Turn, "discard"));
                fixture.Append(new TurnSteeringDropped(id, fixture.Run, fixture.Lane, fixture.Turn, "discard again"));
                break;
            case "negative-step":
                fixture.Append(received);
                fixture.Append(new TurnSteeringApplied(id, fixture.Run, fixture.Lane, fixture.Turn, -1));
                break;
            case "empty-reason":
                fixture.Append(received);
                fixture.Append(new TurnSteeringDropped(id, fixture.Run, fixture.Lane, fixture.Turn, ""));
                break;
            case "cross-scope":
                fixture.Append(received);
                fixture.Append(new TurnSteeringApplied(id, fixture.Run, LaneId.New(), fixture.Turn, 0));
                break;
        }

        Assert.Throws<InvalidStateTransitionException>(() => SteeringQueue.Pending(fixture.Store,
            fixture.Codecs, fixture.Session, fixture.Run, fixture.Lane, fixture.Turn));
    }

    private sealed class Fixture
    {
        public readonly InMemoryEventStore Store;
        public readonly EventCodecs Codecs;
        public readonly SessionId Session;
        public readonly RunId Run;
        public readonly TaskId Task;
        public readonly LaneId Lane;
        public readonly TurnId Turn;

        public Fixture() : this(new InMemoryEventStore(), EventCodecs.Create(), SessionId.New()) { }

        public Fixture(InMemoryEventStore store, EventCodecs codecs, SessionId session)
        {
            Store = store;
            Codecs = codecs;
            Session = session;
            Run = RunId.New();
            Task = TaskId.New();
            Lane = LaneId.New();
            Turn = TurnId.New();
            var budget = new TaskBudget(null, null, null, null);
            var stream = new EventStream(Store, Codecs, Session);
            stream.AppendBatch(new DomainEventPayload[]
            {
                new RunCreated(Run, Session, "steering test", RunMode.Act, ExecutionStrategy.Direct,
                    FailurePolicy.BlockDependents, budget, Task, DateTimeOffset.UnixEpoch),
                new RunStarted(Run),
                new TaskCreated(Task, Run, "steering test", Array.Empty<TaskDependency>(), budget),
                new TaskReady(Task),
                new LaneCreated(Lane, Task, ProfileId.New()),
                new LaneStarted(Lane),
                new TaskStarted(Task, Lane),
                new TurnStarted(Turn, Lane),
            }, DurabilityClass.Standard);
        }

        public void Append(DomainEventPayload payload)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run, Task, Lane, Turn)))
                new EventStream(Store, Codecs, Session).Append(payload, DurabilityClass.Standard);
        }

        public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run, Task, Lane, Turn)))
                new EventStream(Store, Codecs, Session).AppendBatch(payloads, DurabilityClass.Barrier);
        }
    }
}
