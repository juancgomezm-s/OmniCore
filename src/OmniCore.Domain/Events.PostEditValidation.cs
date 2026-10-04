namespace OmniCore.Domain;

/// <summary>Durable validation debt reserved before an authorized file effect. The ToolCallId
/// identifies the edit; known no-effect outcomes release it during replay.</summary>
public record PostEditValidationPending(RunId RunId, ToolCallId ToolCallId,
    IReadOnlyList<string> Paths) : DomainEventPayload
{
    public EventType Type() => EventType.Of("post_edit_validation.pending");
    public int SchemaVersion() => 1;
}

/// <summary>A host-owned successful build OR test covers exactly these earlier edit IDs.</summary>
public record PostEditValidationConsumed(RunId RunId, IReadOnlyList<ToolCallId> EditIds,
    string Gate) : DomainEventPayload
{
    public EventType Type() => EventType.Of("post_edit_validation.consumed");
    public int SchemaVersion() => 1;
}
