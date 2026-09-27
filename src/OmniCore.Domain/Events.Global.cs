namespace OmniCore.Domain;

using System.Text.Json.Serialization;

/// <summary>
/// SessionCreated: se crea una sesión durable vinculada a un workspace. El workspace viaja como
/// campos planos (WorkspaceId se serializa por defecto como objeto; aquí va su string canónico).
/// </summary>
public record SessionCreated(SessionId SessionId, string WorkspaceId, string WorkspaceDisplayPath,
    ProfileId Profile, DateTimeOffset CreatedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.created");

    public int SchemaVersion() => 1;
}

/// <summary>UserInputReceived: el usuario envía input al Run (ADR-0035 §1).</summary>
[JsonSerializable(typeof(UserInputReceived))]
public record UserInputReceived(RunId RunId, string InputPartsJson, ArtifactRef? ContentRef) : DomainEventPayload
{
    public EventType Type() => EventType.Of("user_input.received");

    public int SchemaVersion() => 1;
}

/// <summary>AssistantMessageRecorded: bloques de texto finales de un Turn (ADR-0035 §1).</summary>
public record AssistantMessageRecorded(RunId RunId, LaneId LaneId, TurnId TurnId, ArtifactRef? ContentRef)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("assistant_message.recorded");

    public int SchemaVersion() => 1;
}

/// <summary>InteractionRequested: petición humana (ADR-0034 §3).</summary>
public record InteractionRequested(
    InteractionId InteractionId,
    InteractionKind Kind,
    string SubjectJson,
    string OptionsJson,
    string DefaultOptionId,
    DateTimeOffset? ExpiresAt,
    LaneId? Lane,
    TaskId? Task,
    PlanItemId? PlanItem,
    int QueuePosition,
    int QueueLength) : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.requested");

    public int SchemaVersion() => 1;
}

/// <summary>InteractionResolved: resolución de una interacción.</summary>
public record InteractionResolved(InteractionId InteractionId, string OptionId, InteractionCause Cause)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.resolved");

    public int SchemaVersion() => 1;
}

/// <summary>InteractionExpired: la interacción venció sin respuesta.</summary>
[JsonSerializable(typeof(InteractionExpired))]
public record InteractionExpired(InteractionId InteractionId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("interaction.expired");

    public int SchemaVersion() => 1;
}

/// <summary>ProgressStalled: un item sin señal de progreso (ADR-0016 §9, ADR-0036 §7).</summary>
public record ProgressStalled(PlanItemId PlanItemId, int TurnsWithoutProgress, DateTimeOffset LastProgressAt)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("progress.stalled");

    public int SchemaVersion() => 1;
}

/// <summary>RunModeChanged: el Run cambia de modo sin cambiar de estado (ADR-0036 §1).</summary>
[JsonSerializable(typeof(RunModeChanged))]
public record RunModeChanged(RunId RunId, RunMode From, RunMode To, string Cause) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.mode_changed");

    public int SchemaVersion() => 1;
}