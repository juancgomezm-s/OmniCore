using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EventStreamFailedWriteCausationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Store_failure_does_not_make_the_next_event_point_to_an_unpersisted_cause(bool batch)
    {
        var store = new FailingStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = TestRun.Open(stream, session);
        var previous = store.ReadFrom(session, 1).Last();
        using var cause = CausationScope.Begin(new EventCausation(previous.EventId));
        var count = stream.WrittenPayloads.Count;
        var payload = new ModelEscalationRequested(run.RunId, "a", "b", EscalationCause.ManualRequest);
        store.FailNextWrite = true;

        Assert.Throws<IOException>(() =>
        {
            if (batch) stream.AppendBatch(new DomainEventPayload[] { payload,
                new ModelEscalationApproved(run.RunId, "b", "test") }, DurabilityClass.Standard);
            else stream.Append(payload);
        });
        Assert.Equal(previous.Sequence, store.CurrentSequence(session));
        Assert.Equal(count, stream.WrittenPayloads.Count);
        stream.Append(payload);

        var next = store.ReadFrom(session, 1).Last();
        Assert.Equal(previous.EventId, Assert.IsType<EventCausation>(next.Causation).EventId);
        Assert.Contains(store.ReadFrom(session, 1), item => item.EventId ==
            Assert.IsType<EventCausation>(next.Causation).EventId);
    }

    [Fact]
    public void Invalid_later_batch_payload_does_not_leave_a_cause_from_the_unwritten_first_payload()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = TestRun.Open(stream, session);
        var previous = store.ReadFrom(session, 1).Last();
        using var cause = CausationScope.Begin(new EventCausation(previous.EventId));
        var count = stream.WrittenPayloads.Count;
        var payload = new ModelEscalationRequested(run.RunId, "a", "b", EscalationCause.ManualRequest);
        Assert.Throws<InvalidOperationException>(() => stream.AppendBatch(
            new DomainEventPayload[] { payload, new InvalidVersionPayload() }, DurabilityClass.Standard));
        Assert.Equal(previous.Sequence, store.CurrentSequence(session));
        Assert.Equal(count, stream.WrittenPayloads.Count);
        stream.Append(payload);
        Assert.Equal(previous.EventId,
            Assert.IsType<EventCausation>(store.ReadFrom(session, 1).Last().Causation).EventId);
    }

    [Fact]
    public void Failed_payload_run_does_not_leak_into_a_later_payload_without_run_identity()
    {
        var store = new FailingStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = TestRun.Open(stream, session);
        store.FailNextWrite = true;
        Assert.Throws<IOException>(() => stream.Append(new ModelEscalationRequested(
            RunId.New(), "a", "b", EscalationCause.ManualRequest)));
        stream.Append(new ToolCallRequested(ToolCallId.New(), "test", "filesystem.read", "{}"));
        var written = store.ReadFrom(session, 1).Last();
        Assert.Equal(run.RunId, written.RunId);
        Assert.Equal(run.RunId, written.CorrelationId);
    }

    [Fact]
    public void Successful_atomic_batch_preserves_explicit_cause_and_does_not_publish_an_implicit_tail()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = TestRun.Open(stream, session);
        var previous = store.ReadFrom(session, 1).Last();
        using (CausationScope.Begin(new EventCausation(previous.EventId)))
            stream.AppendBatch(new DomainEventPayload[]
            {
                new ModelEscalationRequested(run.RunId, "a", "b", EscalationCause.ManualRequest),
                new ModelEscalationApproved(run.RunId, "b", "test"),
            }, DurabilityClass.Standard);
        stream.Append(new ModelEscalationCompleted(run.RunId, "b"));
        var written = store.ReadFrom(session, previous.Sequence + 1);
        Assert.Equal(3, written.Count);
        Assert.Equal(previous.EventId, Assert.IsType<EventCausation>(written[0].Causation).EventId);
        Assert.Equal(previous.EventId, Assert.IsType<EventCausation>(written[1].Causation).EventId);
        Assert.Null(written[2].Causation);
    }

    private sealed record InvalidVersionPayload : DomainEventPayload
    {
        public EventType Type() => EventType.Of("model.escalation_approved");
        public int SchemaVersion() => 99;
    }

    private sealed class FailingStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextWrite { get; set; }
        public void Append(SessionId session, DomainEvent item, DurabilityClass durability,
            CancellationToken token) => AppendBatch(session, new[] { item }, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> items,
            DurabilityClass durability, CancellationToken token)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("controlled failure before persistence");
            }
            _inner.AppendBatch(session, items, durability, token);
        }
        public long CurrentSequence(SessionId session) => _inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long sequence) =>
            _inner.ReadFrom(session, sequence);
    }
}
