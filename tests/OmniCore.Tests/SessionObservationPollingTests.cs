using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class SessionObservationPollingTests
{
    private sealed class CountingStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        internal int FullReads;
        public void Append(SessionId session, DomainEvent value, DurabilityClass durability, CancellationToken token) => _inner.Append(session, value, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> values, DurabilityClass durability, CancellationToken token) => _inner.AppendBatch(session, values, durability, token);
        public long CurrentSequence(SessionId session) => _inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from)
        { if (from == 1) FullReads++; return _inner.ReadFrom(session, from); }
    }

    [Fact]
    public void Idle_observation_polls_reuse_usage_and_negative_context_recovery_until_journal_changes()
    {
        var store = new CountingStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var turn = TurnId.New();
        var root = Path.Combine(Path.GetTempPath(), "omni-observation-poll-" + Guid.NewGuid().ToString("N"));
        var hub = new SessionObservationHub(store, codecs, new FileArtifactStore(root));
        void Add(DomainEventPayload payload) => store.Append(session, DomainEvent.Create(session, payload.Type(),
            payload.SchemaVersion(), null, null, null, null, null, turn, null, null, [],
            codecs.CodecFor(payload.Type()).Encode(payload)), DurabilityClass.Barrier, TestContext.Current.CancellationToken);
        Add(new ModelStepStarted(turn, 0, "fixture", 8192, "Direct", null, null, null));
        var first = hub.Snapshot(session);
        Assert.Null(first.Context);
        var fullReads = store.FullReads;
        for (var i = 0; i < 20; i++) Assert.Equal(first.Consumption, hub.Snapshot(session).Consumption);
        Assert.Equal(fullReads, store.FullReads);
        Add(new ModelStepCompleted(turn, 0, new(100, 20, 0, 0, 0), StopReason.EndTurn, null, "fixture", null));
        var updated = hub.Snapshot(session);
        Assert.True(store.FullReads > fullReads);
        Assert.Equal(100, updated.Consumption.Tokens.Value!.Input);
        Assert.Equal(20, updated.Consumption.Tokens.Value.Output);
        Assert.NotEqual(first.Consumption.BasedOnJournalSequence, updated.Consumption.BasedOnJournalSequence);
        var other = hub.Snapshot(SessionId.New());
        Assert.NotEqual(updated.SessionId, other.SessionId);
        Assert.Equal(0, other.Consumption.Tokens.Value!.Input);
        Assert.Equal(0, other.Consumption.Tokens.Value.Output);
    }

    [Fact]
    public void Idle_subscription_does_not_reread_full_history_and_still_delivers_new_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-idle-subscription-" + Guid.NewGuid().ToString("N"));
        var store = new CountingStore();
        var codecs = EventCodecs.Create();
        var paths = new DefaultPlatformPaths(root);
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), platformPathsFactory: () => paths);
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"hola\"}"), TestContext.Current.CancellationToken).Status);
        var session = server.LastSessionId()!;
        var next = store.CurrentSequence(session) + 1;
        var reads = store.FullReads;
        for (var i = 0; i < 20; i++) Assert.Empty(server.SubscribeSince(next));
        Assert.Equal(reads, store.FullReads);
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"nuevo\"}"), TestContext.Current.CancellationToken).Status);
        Assert.NotEmpty(server.SubscribeSince(next));
        server.ClosePersistentStore();
    }
}
