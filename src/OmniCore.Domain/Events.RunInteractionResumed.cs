namespace OmniCore.Domain;

/// <summary>Reactivates an AwaitingInput Run after its user-owned route consent is resolved.</summary>
public sealed record RunInteractionResumed(RunId RunId, InteractionId InteractionId, string CommandId)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.interaction_resumed");
    public int SchemaVersion() => 1;
}
