using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>First-input admission faults use an in-memory store fixture, no model invocation.</summary>
public sealed class SessionInputInitialFailureTests
{
    [Theory]
    [InlineData("session.routing_policy_set", false)]
    [InlineData("run.created", true)]
    public void First_input_tracks_attempt_identity_and_distinguishes_atomic_rejection_from_durable_admission(
        string failureType, bool sessionWasCommitted)
    {
        var store = new RejectEventStore(failureType);
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        var id = Ids.NewV7();
        var ack = server.Send(WireEnvelope.Command(id, "{\"cmd\":\"session.input\",\"text\":\"hello\"}"),
            CancellationToken.None);

        Assert.Equal(id, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.Equal(sessionWasCommitted ? RuntimeCommandOutcomeKind.Accepted : RuntimeCommandOutcomeKind.Rejected,
            ack.Outcome?.Kind);
        Assert.Null(server.LastRunId());
        var session = Assert.IsType<SessionId>(store.AttemptedSession);
        var events = store.ReadFrom(session, 1);
        if (sessionWasCommitted)
        {
            Assert.Equal(session, server.LastSessionId());
            Assert.Equal(2, events.Count);
            Assert.IsType<SessionCreated>(codecs.Decode(events[0]));
            Assert.IsType<SessionRoutingPolicySet>(codecs.Decode(events[1]));
            Assert.Equal(1L, ack.FirstSeq);
            Assert.Equal(2L, ack.LastSeq);
            Assert.All(events, e => Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(id))), e.Causation));
        }
        else
        {
            Assert.Empty(events);
            Assert.Null(server.LastSessionId());
            Assert.Null(ack.FirstSeq);
            Assert.Null(ack.LastSeq);
        }
    }

    private sealed class RejectEventStore(string failureType) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public SessionId? AttemptedSession { get; private set; }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken ct) =>
            AppendBatch(session, new[] { evt }, durability, ct);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken ct)
        {
            AttemptedSession = session;
            if (events.Any(e => e.Type.ToString() == failureType))
                throw new IOException("controlled first-input commit rejection");
            _inner.AppendBatch(session, events, durability, ct);
        }
        public long CurrentSequence(SessionId session) => _inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => _inner.ReadFrom(session, from);
    }
}
