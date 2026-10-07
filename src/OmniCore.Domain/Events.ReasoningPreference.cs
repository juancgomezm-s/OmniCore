namespace OmniCore.Domain;

/// <summary>Replayable run-local reasoning choice; request null is explicit off, HasSelection false is inherited.</summary>
public sealed record RunReasoningSelectionState(long Revision, bool HasSelection,
    ReasoningRequest? Request, string Source, long? UserPreferenceRevision,
    bool HasCapturedUserDefault = false, ReasoningRequest? CapturedUserDefault = null,
    long? CapturedUserPreferenceRevision = null);

/// <summary>Durable reasoning choice for this Run, copied from User default or explicitly selected by the user.</summary>
public sealed record RunReasoningPreferenceSelected(RunId RunId, long Revision, ReasoningRequest? Request,
    string Source, string CommandId, long? UserPreferenceRevision = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.reasoning_preference_selected");
    public int SchemaVersion() => 1;
}

/// <summary>Clears only this Run's override so it inherits the captured User default.</summary>
public sealed record RunReasoningPreferenceRevoked(RunId RunId, long Revision,
    string CommandId, string Origin = "User") : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.reasoning_preference_revoked");
    public int SchemaVersion() => 1;
}
