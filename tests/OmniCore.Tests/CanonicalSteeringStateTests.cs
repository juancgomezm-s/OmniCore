using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class CanonicalSteeringStateTests
{
    [Fact]
    public void Pending_applied_and_dropped_states_match_replay_golden_rule()
    {
        var fx = new Fixture();
        var pending = fx.Received();
        fx.Append(pending);
        AssertSteeringState(fx.Stream.LiveState(), pending.SteeringId, "Pending");

        var applied = fx.Received();
        fx.Append(applied);
        fx.Append(new TurnSteeringApplied(applied.SteeringId, fx.Run.RunId, fx.Run.RootLane,
            fx.Turn, 2));
        AssertSteeringState(fx.Stream.LiveState(), applied.SteeringId, "Applied");

        var dropped = fx.Received();
        fx.Append(dropped);
        fx.Append(new TurnSteeringDropped(dropped.SteeringId, fx.Run.RunId, fx.Run.RootLane,
            fx.Turn, "turn ended without another model step"));
        AssertSteeringState(fx.Stream.LiveState(), dropped.SteeringId, "Dropped");
        Assert.Equal(fx.Stream.LiveState().Snapshot(),
            CanonicalStateTracker.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Snapshot());
    }

    [Fact]
    public void Clone_transitions_do_not_mutate_original_tracker()
    {
        var fx = new Fixture();
        var received = fx.Received();
        fx.Append(received);
        var original = fx.Stream.LiveState();
        var appliedClone = original.Clone();
        appliedClone.Apply(new TurnSteeringApplied(received.SteeringId, fx.Run.RunId,
            fx.Run.RootLane, fx.Turn, 0));
        AssertSteeringState(original, received.SteeringId, "Pending");
        AssertSteeringState(appliedClone, received.SteeringId, "Applied");

        var droppedClone = original.Clone();
        droppedClone.Apply(new TurnSteeringDropped(received.SteeringId, fx.Run.RunId,
            fx.Run.RootLane, fx.Turn, "cancelled"));
        AssertSteeringState(original, received.SteeringId, "Pending");
        AssertSteeringState(droppedClone, received.SteeringId, "Dropped");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_receipt_write_leaves_state_and_sequence_unchanged_then_retry_records_once(bool batch)
    {
        var store = new FailOnceStore();
        var fx = new Fixture(store);
        var received = fx.Received();
        var beforeSequence = store.CurrentSequence(fx.Session);
        var beforeSnapshot = fx.Stream.LiveState().Snapshot();
        store.FailNextWrite = true;

        Assert.Throws<IOException>(() =>
        {
            if (batch) fx.AppendBatch([received]);
            else fx.Append(received);
        });

        Assert.Equal(beforeSequence, store.CurrentSequence(fx.Session));
        Assert.Equal(beforeSnapshot, fx.Stream.LiveState().Snapshot());
        Assert.Empty(store.ReadFrom(fx.Session, beforeSequence + 1));

        if (batch) fx.AppendBatch([received]);
        else fx.Append(received);
        AssertSteeringState(fx.Stream.LiveState(), received.SteeringId, "Pending");
        Assert.Single(store.ReadFrom(fx.Session, beforeSequence + 1),
            evt => fx.Codecs.Decode(evt) is TurnSteeringReceived item && item.SteeringId == received.SteeringId);
        Assert.Equal(fx.Stream.LiveState().Snapshot(),
            CanonicalStateTracker.Replay(fx.Codecs, store.ReadFrom(fx.Session, 1)).Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_application_write_does_not_consume_pending_input(bool batch)
    {
        var store = new FailOnceStore();
        var fx = new Fixture(store);
        var received = fx.Received();
        fx.Append(received);
        var applied = new TurnSteeringApplied(received.SteeringId, fx.Run.RunId,
            fx.Run.RootLane, fx.Turn, 2);
        var beforeSequence = store.CurrentSequence(fx.Session);
        var beforeSnapshot = fx.Stream.LiveState().Snapshot();
        store.FailNextWrite = true;

        Assert.Throws<IOException>(() =>
        {
            if (batch) fx.AppendBatch([applied]);
            else fx.Append(applied);
        });
        Assert.Equal(beforeSequence, store.CurrentSequence(fx.Session));
        Assert.Equal(beforeSnapshot, fx.Stream.LiveState().Snapshot());
        AssertSteeringState(fx.Stream.LiveState(), received.SteeringId, "Pending");
        Assert.Empty(store.ReadFrom(fx.Session, beforeSequence + 1));

        if (batch) fx.AppendBatch([applied]);
        else fx.Append(applied);
        AssertSteeringState(fx.Stream.LiveState(), received.SteeringId, "Applied");
        Assert.Single(store.ReadFrom(fx.Session, beforeSequence + 1),
            evt => fx.Codecs.Decode(evt) is TurnSteeringApplied item && item.SteeringId == received.SteeringId);
        Assert.Equal(fx.Stream.LiveState().Snapshot(),
            CanonicalStateTracker.Replay(fx.Codecs, store.ReadFrom(fx.Session, 1)).Snapshot());
    }

    [Fact]
    public void Sqlite_reopen_preserves_pending_scope_and_accepts_matching_transition()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-steering-state-" + Guid.NewGuid().ToString("N") + ".db");
        var session = SessionId.New();
        var codecs = EventCodecs.Create();
        var received = default(TurnSteeringReceived)!;
        IReadOnlyList<string> expectedSnapshot;
        try
        {
            var store = new SqliteEventStore(path);
            var initialConnection = (Microsoft.Data.Sqlite.SqliteConnection)store.Connection;
            try
            {
                var fx = new Fixture(store, session, codecs);
                received = fx.Received();
                fx.Append(received);
                expectedSnapshot = fx.Stream.LiveState().Snapshot();
            }
            finally
            {
                store.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(initialConnection);
                initialConnection.Dispose();
            }

            var reopened = new SqliteEventStore(path);
            var reopenedConnection = (Microsoft.Data.Sqlite.SqliteConnection)reopened.Connection;
            try
            {
                var tracker = CanonicalStateTracker.Replay(codecs, reopened.ReadFrom(session, 1));
                Assert.Equal(expectedSnapshot, tracker.Snapshot());
                AssertSteeringState(tracker, received.SteeringId, "Pending");
                var resumed = new EventStream(reopened, codecs, session);
                using (ExecutionScope.Begin(new ExecutionScopeState(received.RunId,
                    TestRunTaskFromJournal(codecs, reopened.ReadFrom(session, 1), received.RunId),
                    received.LaneId, received.TurnId)))
                    resumed.Append(new TurnSteeringApplied(received.SteeringId, received.RunId,
                        received.LaneId, received.TurnId, 4));
                AssertSteeringState(resumed.LiveState(), received.SteeringId, "Applied");
                Assert.Equal(resumed.LiveState().Snapshot(),
                    CanonicalStateTracker.Replay(codecs, reopened.ReadFrom(session, 1)).Snapshot());
            }
            finally
            {
                reopened.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(reopenedConnection);
                reopenedConnection.Dispose();
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Theory]
    [InlineData("different-run")]
    [InlineData("different-lane")]
    [InlineData("different-turn")]
    public void Existing_but_foreign_scope_identity_is_rejected(string mismatch)
    {
        var fx = new Fixture();
        var other = fx.OpenOtherRun();
        var received = fx.Received();
        received = mismatch switch
        {
            "different-run" => received with { RunId = other.Run.RunId },
            "different-lane" => received with { LaneId = other.Run.RootLane },
            _ => received with { TurnId = other.Turn },
        };
        fx.AssertRejectedWithoutMutation(() => fx.Append(received));
    }

    [Fact]
    public void Apply_is_rejected_after_turn_or_run_terminal_but_drop_is_allowed_after_run_failure()
    {
        var turnFx = new Fixture();
        var turnItem = turnFx.Received();
        turnFx.Append(turnItem);
        turnFx.Append(new TurnCompleted(turnFx.Turn));
        turnFx.AssertRejectedWithoutMutation(() => turnFx.Append(new TurnSteeringApplied(
            turnItem.SteeringId, turnFx.Run.RunId, turnFx.Run.RootLane, turnFx.Turn, 0)));

        var runFx = new Fixture();
        var runItem = runFx.Received();
        runFx.Append(runItem);
        runFx.Append(new RunFailed(runFx.Run.RunId, "controlled terminal state"));
        runFx.AssertRejectedWithoutMutation(() => runFx.Append(new TurnSteeringApplied(
            runItem.SteeringId, runFx.Run.RunId, runFx.Run.RootLane, runFx.Turn, 0)));
        runFx.Append(new TurnSteeringDropped(runItem.SteeringId, runFx.Run.RunId,
            runFx.Run.RootLane, runFx.Turn, "run cancelled"));
        AssertSteeringState(runFx.Stream.LiveState(), runItem.SteeringId, "Dropped");
    }

    private static void AssertSteeringState(CanonicalStateTracker tracker, SteeringId id, string state) =>
        Assert.Contains("steering:" + id + "=" + state, tracker.Snapshot());

    private static TaskId TestRunTaskFromJournal(IEventCodecRegistry codecs,
        IReadOnlyList<DomainEvent> events, RunId runId) => events.Select(codecs.Decode)
        .OfType<RunCreated>().Single(run => run.RunId == runId).RootTask;

    private sealed class Fixture
    {
        public IEventStore Store { get; }
        public EventCodecs Codecs { get; }
        public SessionId Session { get; }
        public TestRun.Opened Run { get; }
        public EventStream Stream { get; }
        public TurnId Turn { get; }

        public Fixture() : this(new InMemoryEventStore(), SessionId.New(), EventCodecs.Create()) { }

        public Fixture(IEventStore store) : this(store, SessionId.New(), EventCodecs.Create()) { }

        public Fixture(IEventStore store, SessionId session, EventCodecs codecs)
        {
            Store = store;
            Session = session;
            Codecs = codecs;
            Stream = new EventStream(store, codecs, session);
            Run = TestRun.Open(Stream, session);
            Turn = TurnId.New();
            Append(new TurnStarted(Turn, Run.RootLane));
        }

        public TurnSteeringReceived Received() => new(SteeringId.New(), Run.RunId,
            Run.RootLane, Turn, "[\"steering input\"]");

        public void Append(DomainEventPayload payload)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, Run.RootTask, Run.RootLane, Turn)))
                Stream.Append(payload, DurabilityClass.Standard);
        }

        public void AppendBatch(IReadOnlyList<DomainEventPayload> payloads)
        {
            using (ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, Run.RootTask, Run.RootLane, Turn)))
                Stream.AppendBatch(payloads, DurabilityClass.Standard);
        }

        public (TestRun.Opened Run, TurnId Turn) OpenOtherRun()
        {
            var other = TestRun.Open(Stream, Session, "foreign run");
            var otherTurn = TurnId.New();
            using (ExecutionScope.Begin(new ExecutionScopeState(other.RunId, other.RootTask, other.RootLane, otherTurn)))
                Stream.Append(new TurnStarted(otherTurn, other.RootLane));
            return (other, otherTurn);
        }

        public void AssertRejectedWithoutMutation(Action append)
        {
            var beforeSequence = Store.CurrentSequence(Session);
            var beforeSnapshot = Stream.LiveState().Snapshot();
            Assert.Throws<InvalidStateTransitionException>(append);
            Assert.Equal(beforeSequence, Store.CurrentSequence(Session));
            Assert.Equal(beforeSnapshot, Stream.LiveState().Snapshot());
        }
    }

    private sealed class FailOnceStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextWrite { get; set; }
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("controlled pre-persistence failure");
            }
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
        }
        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequence) =>
            _inner.ReadFrom(sessionId, fromSequence);
    }
}
