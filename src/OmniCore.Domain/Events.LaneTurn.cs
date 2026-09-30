namespace OmniCore.Domain;


/// <summary>LaneCreated: la Lane entra en Queued.</summary>
public record LaneCreated(LaneId LaneId, TaskId TaskId, ProfileId AgentProfile) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.created");

    public int SchemaVersion() => 1;
}

/// <summary>LaneProvisioning: Queued → Provisioning (worktree/proceso externo).</summary>
public record LaneProvisioning(LaneId LaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.provisioning");

    public int SchemaVersion() => 1;
}

/// <summary>LaneStarted: Queued/Provisioning → Running.</summary>
public record LaneStarted(LaneId LaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.started");

    public int SchemaVersion() => 1;
}

/// <summary>LaneBlocked: Running → Blocked.</summary>
public record LaneBlocked(LaneId LaneId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.blocked");

    public int SchemaVersion() => 1;
}

/// <summary>LaneUnblocked: Blocked → Running.</summary>
public record LaneUnblocked(LaneId LaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.unblocked");

    public int SchemaVersion() => 1;
}

/// <summary>LaneCompleted: Running → Completed.</summary>
public record LaneCompleted(LaneId LaneId, AgentResult? Result) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.completed");

    public int SchemaVersion() => 1;
}

/// <summary>LaneFailed: Running/Blocked/Provisioning → Failed.</summary>
public record LaneFailed(LaneId LaneId, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.failed");

    public int SchemaVersion() => 1;
}

/// <summary>LaneCancelled: cualquier estado no terminal → Cancelled.</summary>
public record LaneCancelled(LaneId LaneId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.cancelled");

    public int SchemaVersion() => 1;
}

/// <summary>LaneHeartbeatRecorded: último heartbeat persistido de una Lane al cerrarla (ADR-0036 §3).</summary>
public record LaneHeartbeatRecorded(LaneId LaneId, LaneHeartbeat Heartbeat) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.heartbeat");

    public int SchemaVersion() => 1;
}

/// <summary>TurnStarted: un Turn de la Lane arranca y registra su fingerprint de ejecución (ADR-0017).</summary>
public record TurnStarted(TurnId TurnId, LaneId LaneId, ExecutionFingerprint? Fingerprint = null,
    ArtifactRef? ContextSnapshotRef = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.started");

    public int SchemaVersion() => 2;
}

/// <summary>ModelCompleted: el modelo terminó su respuesta completa (ADR-0036 §6).</summary>
public record ModelCompleted(TurnId TurnId, ArtifactRef? ResponseArtifact) : DomainEventPayload
{
    public EventType Type() => EventType.Of("model.completed");

    public int SchemaVersion() => 1;
}

/// <summary>TurnCompleted: el Turn terminó normalmente.</summary>
public record TurnCompleted(TurnId TurnId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.completed");

    public int SchemaVersion() => 1;
}

/// <summary>TurnInterrupted: generación cortada por Interrupt (ADR-0035 §6).</summary>
public record TurnInterrupted(TurnId TurnId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.interrupted");

    public int SchemaVersion() => 1;
}

/// <summary>TurnAbandoned: respuesta incompleta tras resume; se re-infiere (ADR-0004 §5).</summary>
public record TurnAbandoned(TurnId TurnId, string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.abandoned");

    public int SchemaVersion() => 1;
}