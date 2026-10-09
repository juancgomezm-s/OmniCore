namespace OmniCore.Domain;

/// <summary>Whether fan-in leaves member results separate or writes a deterministic aggregate.</summary>
public enum FanInPolicy { Direct, Aggregate }

/// <summary>A durable ordered set of direct child delegations owned by one supervisor.</summary>
public sealed record FanOutGroup(FanOutGroupId GroupId, ExecutionId OwnerExecutionId,
    IReadOnlyList<DelegationId> MemberDelegationIds, FanInPolicy FanInPolicy)
{
    private IReadOnlyList<DelegationId> _members = Array.AsReadOnly(ContractCopies.Required(MemberDelegationIds).ToArray());
    public IReadOnlyList<DelegationId> MemberDelegationIds
    { get => _members; init => _members = Array.AsReadOnly(ContractCopies.Required(value).ToArray()); }

    public void Validate()
    {
        ContractCopies.Id(GroupId?.Value); ContractCopies.Id(OwnerExecutionId?.Value);
        ContractCopies.Known(FanInPolicy);
        if (MemberDelegationIds.Count < 2) throw new ArgumentException("Fan-out requires at least two delegations.");
        foreach (var member in MemberDelegationIds) ContractCopies.Id(member?.Value);
        if (MemberDelegationIds.Distinct().Count() != MemberDelegationIds.Count)
            throw new ArgumentException("Fan-out members must be unique.");
    }
}

/// <summary>Durable fan-out admission. Membership order is the caller's stable output order.</summary>
public sealed record FanOutGroupCreated(ExecutionId ExecutionId, FanOutGroup Group) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("fanout_group.created");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => [];
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value); ArgumentNullException.ThrowIfNull(Group); Group.Validate();
        if (ExecutionId != Group.OwnerExecutionId) throw new ArgumentException("Fan-out owner mismatch.");
    }
}

/// <summary>Replaces one member asking for rework while preserving its stable group position.</summary>
public sealed record FanOutGroupMemberReplaced(ExecutionId ExecutionId, FanOutGroupId GroupId,
    DelegationId PreviousDelegationId, DelegationId ReplacementDelegationId) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("fanout_group.member_replaced");
    public int SchemaVersion() => 1;
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value); ContractCopies.Id(GroupId?.Value);
        ContractCopies.Id(PreviousDelegationId?.Value); ContractCopies.Id(ReplacementDelegationId?.Value);
        if (PreviousDelegationId == ReplacementDelegationId)
            throw new ArgumentException("Fan-out rework replacement must name a new delegation.");
    }
}

/// <summary>All member results were explicitly accepted. AggregateRef is set only for Aggregate policy.</summary>
public sealed record FanOutGroupResolved(ExecutionId ExecutionId, FanOutGroupId GroupId,
    IReadOnlyList<ArtifactRef> MemberResultRefs, ArtifactRef? AggregateRef) : IValidatedDomainEventPayload
{
    private IReadOnlyList<ArtifactRef> _results = Array.AsReadOnly(ContractCopies.Required(MemberResultRefs).ToArray());
    public IReadOnlyList<ArtifactRef> MemberResultRefs
    { get => _results; init => _results = Array.AsReadOnly(ContractCopies.Required(value).ToArray()); }
    public EventType Type() => EventType.Of("fanout_group.resolved");
    public int SchemaVersion() => 1;
    public IReadOnlyList<ArtifactRef> RecordArtifacts() => AggregateRef is null
        ? MemberResultRefs : MemberResultRefs.Append(AggregateRef).ToArray();
    public void Validate()
    {
        ContractCopies.Id(ExecutionId?.Value); ContractCopies.Id(GroupId?.Value);
        if (MemberResultRefs.Count < 2) throw new ArgumentException("Resolved fan-out requires member results.");
        ContractCopies.Artifacts(RecordArtifacts());
    }
}
