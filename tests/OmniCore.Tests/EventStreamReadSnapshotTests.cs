using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EventStreamReadSnapshotTests
{
    [Fact]
    public void Repeated_reads_materialize_only_new_events_and_keep_previous_snapshots_stable()
    {
        var inner = new InMemoryEventStore();
        var store = new CountingStore(inner);
        var session = SessionId.New();
        for (var index = 0; index < 200; index++) Append(inner, session, index);
        var reader = new EventStream(store, EventCodecs.Create(), session);
        var first = reader.EventsSince(1);
        Assert.Equal(200, first.Count);
        for (var index = 200; index < 220; index++)
        {
            Append(inner, session, index);
            Assert.Equal(index + 1, reader.EventsSince(1).Count);
        }
        Assert.Equal(220, store.MaterializedEvents);
        Assert.Equal(200, first.Count);
        Assert.Equal(first.Select(evt => evt.EventId), reader.EventsSince(1).Take(200).Select(evt => evt.EventId));
        Assert.Equal(220, store.MaterializedEvents);
    }

    [Fact]
    public void Reading_an_earlier_range_reconstructs_the_missing_prefix_and_future_reads_stay_empty()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        for (var index = 0; index < 5; index++) Append(store, session, index);
        var stream = new EventStream(store, EventCodecs.Create(), session);
        Assert.Equal(new long[] { 3, 4, 5 }, stream.EventsSince(3).Select(evt => evt.Sequence));
        Assert.Equal(store.ReadFrom(session, 1), stream.EventsSince(1));
        Assert.Empty(stream.EventsSince(100));
        Append(store, session, 5);
        Assert.Empty(stream.EventsSince(100));
        Assert.Equal(new long[] { 5, 6 }, stream.EventsSince(5).Select(evt => evt.Sequence));
    }

    [Fact]
    public void Independent_sqlite_connection_appends_are_visible_and_purged_events_are_not_retained()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-read-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var readerStore = new SqliteEventStore(journal);
        var writerStore = new SqliteEventStore(journal);
        try
        {
            var session = SessionId.New();
            var other = SessionId.New();
            Append(writerStore, session, 0);
            var stream = new EventStream(readerStore, EventCodecs.Create(), session);
            var first = stream.EventsSince(1);
            Append(writerStore, session, 1);
            Append(writerStore, other, 99);
            var second = stream.EventsSince(1);
            Assert.Equal(writerStore.ReadFrom(session, 1).Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)),
                second.Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)));
            Assert.Single(first);
            Assert.Equal(2, second.Count);
            Assert.All(second, evt => Assert.Equal(session, evt.SessionId));
            Assert.Equal(2, writerStore.PurgeSession(session, CancellationToken.None));
            Assert.Empty(stream.EventsSince(1));
            Assert.Equal(2, second.Count);
            Assert.Single(writerStore.ReadFrom(other, 1));
        }
        finally
        {
            readerStore.Close();
            writerStore.Close();
        }
    }

    private static void Append(IEventStore store, SessionId session, int index) =>
        store.Append(session, DomainEvent.Create(session, EventType.Of("test.read"), 1,
            null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(),
            "{\"index\":" + index + "}"), DurabilityClass.Standard, CancellationToken.None);

    private sealed class CountingStore(IEventStore inner) : IEventStore
    {
        public long MaterializedEvents { get; private set; }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token) =>
            inner.Append(session, evt, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken token) => inner.AppendBatch(session, events, durability, token);
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from)
        {
            var events = inner.ReadFrom(session, from);
            MaterializedEvents += events.Count;
            return events;
        }
    }
}
