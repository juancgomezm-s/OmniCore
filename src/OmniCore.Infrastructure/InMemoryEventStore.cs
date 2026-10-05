namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Event Store en memoria para tests deterministas (ADR-0002 §1).
/// Un solo escritor por sesión asigna secuencias contiguas sin huecos.
/// </summary>
public sealed class InMemoryEventStore : IEventStore, IWorkspaceJournalReader
{
    private readonly Dictionary<SessionId, DomainEvent[]> _sessions = new();

    public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
        CancellationToken cancellationToken)
    {
        AppendBatch(sessionId, [evt], durability, cancellationToken);
    }

    public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
        CancellationToken cancellationToken)
    {
        var existing = _sessions.TryGetValue(sessionId, out var stored) ? stored : new DomainEvent[0];
        var copy = new DomainEvent[existing.Length + evts.Count];
        for (var i = 0; i < existing.Length; i++)
        {
            copy[i] = existing[i];
        }

        var idx = 0;
        foreach (var pending in evts)
        {
            copy[existing.Length + idx] = DomainEvent.Stored(
                pending.EventId,
                sessionId,
                existing.Length + (long) idx + 1,
                pending.Type,
                pending.SchemaVersion,
                pending.Timestamp,
                pending.Causation,
                pending.CorrelationId,
                pending.RunId,
                pending.TaskId,
                pending.LaneId,
                pending.TurnId,
                pending.PlanItemId,
                pending.ToolCallId,
                pending.ArtifactRefs,
                pending.PayloadJson,
                pending.ExecutionId);
            idx++;
        }

        _sessions[sessionId] = copy;
    }

    public long CurrentSequence(SessionId sessionId)
    {
        var found = _sessions.TryGetValue(sessionId, out var stored);
        return found ? (long) stored!.Length : 0;
    }

    public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
    {
        if (!_sessions.TryGetValue(sessionId, out var stored))
        {
            return new DomainEvent[0];
        }

        var start = (int) Math.Max(0, fromSequenceInclusive - 1);
        if (start >= stored!.Length)
        {
            return new DomainEvent[0];
        }

        var tail = new DomainEvent[stored!.Length - start];
        for (var i = 0; i < tail.Length; i++)
        {
            tail[i] = stored![start + i];
        }

        return tail;
    }

    public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => _sessions.Values
        .SelectMany(events => events)
        .Where(evt => evt.Type.Equals(type))
        .ToArray();
}
