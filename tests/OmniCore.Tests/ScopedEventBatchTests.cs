using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ScopedEventBatchTests
{
    [Fact]
    public void Per_item_owners_share_one_commit_and_command_without_leaking_ambient_scope()
    {
        var store = new RecordingStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var first = Scope();
        var second = Scope();
        var ambient = Scope();
        var command = new CommandCausation(CommandId.New());
        using (ExecutionScope.Begin(ambient))
        using (CausationScope.Begin(command))
        {
            stream.AppendBatch(new[] { Neutral(), Neutral(), Neutral() }, DurabilityClass.Barrier,
                new ExecutionScopeState?[] { first, second, null });
            Assert.Same(ambient, ExecutionScope.Current);
            Assert.Same(command, CausationScope.Current);
        }
        Assert.Null(ExecutionScope.Current);
        Assert.Null(CausationScope.Current);
        Assert.Equal(1, store.Writes);
        Assert.Equal(DurabilityClass.Barrier, store.LastDurability);
        var events = store.ReadFrom(session, 1);
        Assert.Equal(3, events.Count);
        AssertOwner(first, events[0]);
        AssertOwner(second, events[1]);
        AssertOwner(ambient, events[2]);
        Assert.All(events, evt => Assert.Equal(command, evt.Causation));
    }

    [Fact]
    public void Payload_identity_remains_authoritative_over_item_scope()
    {
        var store = new RecordingStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var scope = Scope();
        var explicitRun = RunId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new ModelEscalationRequested(explicitRun, "a", "b", EscalationCause.ManualRequest) },
            DurabilityClass.Standard, new[] { scope });
        var written = Assert.Single(store.ReadFrom(session, 1));
        Assert.Equal(explicitRun, written.RunId);
        Assert.Equal(explicitRun, written.CorrelationId);
        Assert.Equal(scope.TaskId, written.TaskId);
    }

    [Fact]
    public void Failed_scoped_batch_publishes_no_partial_state_and_can_retry_as_one_commit()
    {
        var store = new RecordingStore { FailNextWrite = true };
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var payloads = new[] { Neutral(), Neutral() };
        var owners = new[] { Scope(), Scope() };
        var command = new CommandCausation(CommandId.New());
        using var cause = CausationScope.Begin(command);
        Assert.Throws<IOException>(() => stream.AppendBatch(payloads, DurabilityClass.Standard, owners));
        Assert.Empty(store.ReadFrom(session, 1));
        Assert.Empty(stream.WrittenPayloads);
        Assert.Equal(0, store.CurrentSequence(session));
        stream.AppendBatch(payloads, DurabilityClass.Standard, owners);
        Assert.Equal(2, store.Writes);
        Assert.Equal(2, stream.WrittenPayloads.Count);
        var events = store.ReadFrom(session, 1);
        Assert.Equal(2, events.Count);
        AssertOwner(owners[0], events[0]);
        AssertOwner(owners[1], events[1]);
        Assert.All(events, evt => Assert.Equal(command, evt.Causation));
    }

    [Fact]
    public void Scope_count_mismatch_is_rejected_before_read_or_write_even_for_empty_batch()
    {
        var store = new RecordingStore();
        var stream = new EventStream(store, EventCodecs.Create(), SessionId.New());
        Assert.Throws<ArgumentException>(() => stream.AppendBatch(new[] { Neutral() },
            DurabilityClass.Standard, Array.Empty<ExecutionScopeState?>()));
        Assert.Throws<ArgumentException>(() => stream.AppendBatch(Array.Empty<DomainEventPayload>(),
            DurabilityClass.Standard, new[] { Scope() }));
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Empty(stream.WrittenPayloads);
    }

    private static DomainEventPayload Neutral() => new InteractionExpired(InteractionId.New());
    private static ExecutionScopeState Scope() => new(RunId.New(), TaskId.New(), LaneId.New(),
        TurnId.New(), ToolCallId.New(), ExecutionId.New());
    private static void AssertOwner(ExecutionScopeState expected, DomainEvent actual)
    {
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.RunId, actual.CorrelationId);
        Assert.Equal(expected.TaskId, actual.TaskId);
        Assert.Equal(expected.LaneId, actual.LaneId);
        Assert.Equal(expected.TurnId, actual.TurnId);
        Assert.Equal(expected.ToolCallId, actual.ToolCallId);
        Assert.Equal(expected.ExecutionId, actual.ExecutionId);
    }

    private sealed class RecordingStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool FailNextWrite { get; set; }
        public DurabilityClass LastDurability { get; private set; }
        public void Append(SessionId session, DomainEvent item, DurabilityClass durability,
            CancellationToken token) => AppendBatch(session, new[] { item }, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> items,
            DurabilityClass durability, CancellationToken token)
        {
            Writes++;
            LastDurability = durability;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("fixture failure before the atomic commit");
            }
            _inner.AppendBatch(session, items, durability, token);
        }
        public long CurrentSequence(SessionId session) => _inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long sequence)
        {
            Reads++;
            return _inner.ReadFrom(session, sequence);
        }
    }
}
