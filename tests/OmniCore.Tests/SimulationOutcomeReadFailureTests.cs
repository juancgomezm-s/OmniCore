using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Store/audit fault fixtures, not provider calls or real consumption.</summary>
public sealed class SimulationOutcomeReadFailureTests
{
    [Fact]
    public void Unreadable_journal_after_partial_execution_is_not_reported_as_rejected_or_zero_effects()
    {
        var store = new ReadFailureStore();
        var server = new OmniServer(store, EventCodecs.Create(), new FailingAudit(store));
        var id = Ids.NewV7();
        var ack = server.Send(WireEnvelope.Command(id, "{\"cmd\":\"sim\"}"), CancellationToken.None);

        Assert.Equal(id, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, ack.Outcome?.Kind);
        Assert.Equal("JournalOutcomeUnavailable", ack.Outcome?.Reason);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.DoesNotContain("read-failure-private-marker", ack.Error ?? "", StringComparison.Ordinal);

        // A failed query says nothing about effects. Inspect the retained fixture only after
        // recovery; the test must prove writes really happened, not assume an empty journal.
        store.FailReads = false;
        var retained = store.RetainedEvents;
        Assert.NotEmpty(retained);
        Assert.Contains(retained, e => e.Causation is CommandCausation cause
            && cause.CommandId.Value == Guid.Parse(id));
        Assert.Contains(retained, e => e.Type.ToString() == "run.created");
    }

    private sealed class FailingAudit(ReadFailureStore store) : IAuditSink
    {
        public void Record(AuditRecord record, CancellationToken ct)
        {
            store.FailReads = true;
            throw new IOException("audit fixture failure after durable execution");
        }
    }

    private sealed class ReadFailureStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly HashSet<SessionId> _sessions = new();
        public bool FailReads { get; set; }
        public IReadOnlyList<DomainEvent> RetainedEvents =>
            _sessions.SelectMany(session => _inner.ReadFrom(session, 1)).ToArray();

        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken ct)
        {
            _inner.Append(session, evt, durability, ct);
            _sessions.Add(session);
        }

        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken ct)
        {
            _inner.AppendBatch(session, events, durability, ct);
            _sessions.Add(session);
        }

        public long CurrentSequence(SessionId session) => _inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => FailReads
            ? throw new IOException("read-failure-private-marker") : _inner.ReadFrom(session, from);
    }
}
