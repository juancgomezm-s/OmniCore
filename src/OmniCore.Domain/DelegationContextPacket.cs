namespace OmniCore.Domain;

/// <summary>Explicit target of a selected-context packet. Data only, never worker admission.</summary>
public sealed record DelegationContextTarget(DelegationId DelegationId, ExecutionId ParentExecutionId,
    LaneId ChildLaneId, ExecutionRelation Relation, ExecutionSupervision Supervision);

/// <summary>Historical factual text. No source CAS references, pinning, permissions or routing.</summary>
public sealed record InheritedContextFact(string SourceItemId, ContextItemKind SourceKind, string Content);

/// <summary>Versioned SelectedProjection payload for Delegation.PacketRef, not the M7 TaskPacket.
/// Publication and the canonical delegation record remain the command publisher's responsibility.</summary>
public sealed record DelegationContextPacket(int SchemaVersion, SessionId SessionId, RunId RunId,
    DelegationContextTarget Target, TaskId ParentTaskId, LaneId ParentLaneId, TaskId ChildTaskId,
    ProfileId ChildProfileId, EvidenceEventRef SourceEvent, long SourceSequence, Guid SourceSnapshotId,
    int MaximumUtf8Bytes, IReadOnlyList<InheritedContextFact> Facts)
{
    private IReadOnlyList<InheritedContextFact> _facts = ContractCopies.Required(Facts);
    public IReadOnlyList<InheritedContextFact> Facts
    { get => _facts; init => _facts = ContractCopies.Required(value); }
}
