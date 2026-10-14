namespace OmniCore.Domain;

/// <summary>Durable, content-free evidence that a lifecycle hook affected a turn (ADR-0029).</summary>
public sealed record HookApplied(RunId? RunId, TurnId? TurnId, string HookId, string Point, string Result,
    IReadOnlyList<string>? DetailIds = null)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("hook.applied");
    public int SchemaVersion() => 3;
}

/// <summary>Resultado fresco de un validador solicitado por un hook antes de un efecto.</summary>
public sealed record HookValidationResolved(RunId RunId, TurnId TurnId, string ValidatorId,
    string EvidenceHash, bool Passed, string Summary) : DomainEventPayload
{
    public EventType Type() => EventType.Of("hook.validation_resolved");
    public int SchemaVersion() => 1;
}
