namespace OmniCore.Domain;

/// <summary>
/// Evento de dominio durable: envelope con causación y tipos como ArtifactRefs indexables
/// (ADR-0001 §3, ADR-0013). El payload es JSON; los subtipos concretos se serializan con un
/// codec registrado por <see cref="EventType"/>.
/// </summary>
public sealed class DomainEvent
{
    /// <summary>Ids de evento y de sesión + secuencia por sesión (empieza en 1).</summary>
    public EventId EventId { get; }

    public SessionId SessionId { get; }

    public long Sequence { get; }

    /// <summary>Tipo de evento durable (string estable, p. ej. run.started; ADR-0013).</summary>
    public EventType Type { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>Evento o comando que originó esta cadena causal (índice causal).</summary>
    public CausationId? Causation { get; }

    /// <summary>Run al que pertenece la cadena causal. Siempre presente salvo Session y audit.</summary>
    public RunId? CorrelationId { get; }

    public RunId? RunId { get; }

    public TaskId? TaskId { get; }

    public LaneId? LaneId { get; }

    public TurnId? TurnId { get; }

    public PlanItemId? PlanItemId { get; }

    public ToolCallId? ToolCallId { get; }

    /// <summary>Referencias explícitas para indexar sin parsear el payload (ADR-0001 §3).</summary>
    public IReadOnlyList<ArtifactRef> ArtifactRefs { get; }

    /// <summary>Payload JSON del evento.</summary>
    public string PayloadJson { get; }

    private DomainEvent(
        EventId eventId,
        SessionId sessionId,
        long sequence,
        EventType type,
        int schemaVersion,
        DateTimeOffset timestamp,
        CausationId? causation,
        RunId? correlationId,
        RunId? runId,
        TaskId? taskId,
        LaneId? laneId,
        TurnId? turnId,
        PlanItemId? planItemId,
        ToolCallId? toolCallId,
        IReadOnlyList<ArtifactRef> artifactRefs,
        string payloadJson)
    {
        EventId = eventId;
        SessionId = sessionId;
        Sequence = sequence;
        Type = type;
        SchemaVersion = schemaVersion;
        Timestamp = timestamp;
        Causation = causation;
        CorrelationId = correlationId;
        RunId = runId;
        TaskId = taskId;
        LaneId = laneId;
        TurnId = turnId;
        PlanItemId = planItemId;
        ToolCallId = toolCallId;
        ArtifactRefs = artifactRefs;
        PayloadJson = payloadJson;
    }

    /// <summary>Crea un evento ya persistible. La secuencia la asigna el escritor (ADR-0002 §1).</summary>
    public static DomainEvent Create(
        SessionId sessionId,
        EventType type,
        int schemaVersion,
        CausationId? causation,
        RunId? correlationId,
        RunId? runId,
        TaskId? taskId,
        LaneId? laneId,
        TurnId? turnId,
        PlanItemId? planItemId,
        ToolCallId? toolCallId,
        IReadOnlyList<ArtifactRef> artifactRefs,
        string payloadJson) =>
        new(
            EventId.New(),
            sessionId,
            0,
            type,
            schemaVersion,
            DateTimeOffset.Now,
            causation,
            correlationId,
            runId,
            taskId,
            laneId,
            turnId,
            planItemId,
            toolCallId,
            artifactRefs,
            payloadJson);

    /// <summary>Reconstruye un evento persistido con su secuencia asignada.</summary>
    public static DomainEvent Stored(
        EventId eventId,
        SessionId sessionId,
        long sequence,
        EventType type,
        int schemaVersion,
        DateTimeOffset timestamp,
        CausationId? causation,
        RunId? correlationId,
        RunId? runId,
        TaskId? taskId,
        LaneId? laneId,
        TurnId? turnId,
        PlanItemId? planItemId,
        ToolCallId? toolCallId,
        IReadOnlyList<ArtifactRef> artifactRefs,
        string payloadJson) =>
        new(
            eventId,
            sessionId,
            sequence,
            type,
            schemaVersion,
            timestamp,
            causation,
            correlationId,
            runId,
            taskId,
            laneId,
            turnId,
            planItemId,
            toolCallId,
            artifactRefs,
            payloadJson);
}