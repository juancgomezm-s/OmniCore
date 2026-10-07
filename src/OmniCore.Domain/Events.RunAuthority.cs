namespace OmniCore.Domain;

/// <summary>Snapshots an explicit selection for the active Run or at Run creation.</summary>
public sealed record RunModeAuthoritySelected(RunModeAuthority Authority, string CommandId, string Origin)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.mode_authority_selected");
    public int SchemaVersion() => 1;
}

/// <summary>Records the authority/revision that allowed a mode transition, separate from v1 history.</summary>
public sealed record RunModeTransitionAuthorized(
    RunId RunId,
    RunMode From,
    RunMode To,
    string Reason,
    string Origin,
    string CommandId,
    long AuthorityRevision,
    long ObjectiveRevision,
    string ObjectiveDigest,
    long PolicyRevision,
    Guid? AuthorizationId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.mode_transition_authorized");
    public int SchemaVersion() => 1;
}

/// <summary>Revokes the adaptive authorization without rewriting the history that granted it.</summary>
public sealed record RunModeAuthorityRevoked(
    RunId RunId,
    long AuthorityRevision,
    Guid AuthorizationId,
    string Reason,
    string CommandId,
    string Origin) : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.mode_authority_revoked");
    public int SchemaVersion() => 1;
}
