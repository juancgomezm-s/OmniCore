namespace OmniCore.Domain;

/// <summary>
/// Complete, durable input binding for a delegated Lane. The selected parent context remains
/// a separately validated SelectedProjection artifact; the packet never embeds a transcript
/// or grants tools/permissions.
/// </summary>
public sealed record TaskPacket(
    int SchemaVersion,
    SessionId SessionId,
    RunId RunId,
    TaskId TaskId,
    LaneId LaneId,
    ExecutionId ExecutionId,
    DelegationId DelegationId,
    ExecutionId ParentExecutionId,
    ProfileId ProfileId,
    string Objective,
    TaskBudget Budget,
    ArtifactRef SelectedProjectionRef,
    string OwnershipId,
    string RepositoryRoot,
    string GitCommonDirectory,
    string WorktreePath,
    string HeadCommit,
    string SnapshotCommit,
    string SnapshotTree,
    WorktreeBase Base,
    string WorkingState);

/// <summary>Canonical journal binding between a provisioning Lane and its private worktree.</summary>
public sealed record LaneWorktreeCreated(
    TaskId TaskId,
    LaneId LaneId,
    ExecutionId ExecutionId,
    string OwnershipId,
    string RepositoryRoot,
    string GitCommonDirectory,
    string WorktreePath,
    string SnapshotCommit,
    string SnapshotTree,
    ArtifactRef TaskPacketRef,
    ArtifactRef SelectedProjectionRef) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.worktree_created");
    public int SchemaVersion() => 1;
}
