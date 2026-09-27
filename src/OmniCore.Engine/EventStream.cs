namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Escritor de eventos del Engine: serializa un payload tipado con el codec de su EventType,
/// construye el envelope y lo persiste asignando la secuencia (ADR-0001 §3, §5). Un solo
/// escritor por sesión (ADR-0002 §1).
/// </summary>
public sealed class EventStream
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly SessionId _sessionId;

    public EventStream(IEventStore store, IEventCodecRegistry codecs, SessionId sessionId)
    {
        _store = store;
        _codecs = codecs;
        _sessionId = sessionId;
    }

    public void Append(DomainEventPayload payload)
    {
        var type = payload.Type();
        var codec = _codecs.CodecFor(type);
        var json = codec.Encode(payload);
        var envelope = DomainEvent.Create(_sessionId, type, payload.SchemaVersion(), null, null,
            ExtractRunId(payload), ExtractTaskId(payload), ExtractLaneId(payload), ExtractTurnId(payload),
            ExtractPlanItemId(payload), ExtractToolCallId(payload), new ArtifactRef[0], json);
        _store.Append(_sessionId, envelope, DurabilityClass.Standard, CancellationToken.None);
    }

    /// <summary>Replay de todos los eventos de la sesión desde la secuencia dada (1-based inclusive).</summary>
    public IReadOnlyList<DomainEvent> EventsSince(long fromSequenceInclusive) =>
        _store.ReadFrom(_sessionId, fromSequenceInclusive);

    private static RunId? ExtractRunId(DomainEventPayload payload)
    {
        if (payload is RunCreated r)
        {
            return r.RunId;
        }

        return null;
    }

    private static TaskId? ExtractTaskId(DomainEventPayload payload)
    {
        if (payload is TaskCreated t)
        {
            return t.TaskId;
        }

        if (payload is TaskReady r)
        {
            return null;
        }

        return null;
    }

    private static LaneId? ExtractLaneId(DomainEventPayload payload)
    {
        if (payload is LaneCreated l)
        {
            return l.LaneId;
        }

        return null;
    }

    private static TurnId? ExtractTurnId(DomainEventPayload payload)
    {
        if (payload is TurnStarted t)
        {
            return t.TurnId;
        }

        if (payload is TurnCompleted c)
        {
            return null;
        }

        return null;
    }

    private static PlanItemId? ExtractPlanItemId(DomainEventPayload payload)
    {
        if (payload is PlanItemAdded a)
        {
            return a.PlanItemId;
        }

        if (payload is PlanItemStarted s)
        {
            return s.PlanItemId;
        }

        if (payload is PlanItemCompleted c)
        {
            return c.PlanItemId;
        }

        if (payload is PlanItemBlocked b)
        {
            return b.PlanItemId;
        }

        if (payload is PlanItemFailed f)
        {
            return f.PlanItemId;
        }

        return null;
    }

    private static ToolCallId? ExtractToolCallId(DomainEventPayload payload)
    {
        if (payload is ToolCallRequested r)
        {
            return r.ToolCallId;
        }

        return null;
    }
}