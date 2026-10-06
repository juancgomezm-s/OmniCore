namespace OmniCore.Domain;

/// <summary>Identifica de forma durable una sugerencia de steering (UUIDv7).</summary>
public record SteeringId(Guid Value)
{
    public static SteeringId New() => new(Guid.CreateVersion7());
    public static SteeringId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

/// <summary>Input explícito de steering recibido mientras un ModelStep sigue abierto.</summary>
public record TurnSteeringReceived(SteeringId SteeringId, RunId RunId, LaneId LaneId, TurnId TurnId,
    string InputPartsJson, string? Origin = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.steering_received");
    public int SchemaVersion() => 1;
}

/// <summary>Steering consumido en el índice de ModelStep indicado.</summary>
public record TurnSteeringApplied(SteeringId SteeringId, RunId RunId, LaneId LaneId, TurnId TurnId,
    int StepIndex) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.steering_applied");
    public int SchemaVersion() => 1;
}

/// <summary>Steering descartado explícitamente; Reason debe ser no vacío.</summary>
public record TurnSteeringDropped(SteeringId SteeringId, RunId RunId, LaneId LaneId, TurnId TurnId,
    string Reason) : DomainEventPayload
{
    public EventType Type() => EventType.Of("turn.steering_dropped");
    public int SchemaVersion() => 1;
}
