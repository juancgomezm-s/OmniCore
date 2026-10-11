namespace OmniCore.Domain;

/// <summary>How this agent execution relates to its parent (independent of supervision).</summary>
public enum ExecutionRelation
{
    Awaited,
    Detached,
}

/// <summary>Whether a supervisor manages the execution (independent of relation).</summary>
public enum ExecutionSupervision
{
    Managed,
    Unmanaged,
}

/// <summary>Starts an AgentExecution; lifecycle events do not imply an AgentResult or Task outcome.</summary>
public record AgentExecutionStarted(ExecutionId ExecutionId, LaneId LaneId, ProfileId ProfileId,
    ExecutionId? ParentExecutionId, ExecutionRelation Relation, ExecutionSupervision Supervision)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("agent_execution.started");
    public int SchemaVersion() => 1;
}

/// <summary>Completes the executor lifecycle only; result production/acceptance are separate contracts.</summary>
public record AgentExecutionCompleted(ExecutionId ExecutionId, LaneId LaneId, ProfileId ProfileId,
    ExecutionId? ParentExecutionId, ExecutionRelation Relation, ExecutionSupervision Supervision)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("agent_execution.completed");
    public int SchemaVersion() => 1;
}

/// <summary>Marks executor failure without implying Lane, Task, or Run failure.</summary>
public record AgentExecutionFailed(ExecutionId ExecutionId, LaneId LaneId, ProfileId ProfileId,
    ExecutionId? ParentExecutionId, ExecutionRelation Relation, ExecutionSupervision Supervision)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("agent_execution.failed");
    public int SchemaVersion() => 1;
}

/// <summary>Durable, read-only association of a workflow lane with an already-owned worktree.
/// Ownership remains with OwnerLaneId/OwnerExecutionId; this event never creates or transfers it.</summary>
public sealed record LaneWorktreeBound(TaskId TaskId, LaneId LaneId, ExecutionId ExecutionId,
    TaskId OwnerTaskId, LaneId OwnerLaneId, ExecutionId OwnerExecutionId, string OwnershipId,
    string RepositoryRoot, string GitCommonDirectory, string WorktreePath, string SnapshotCommit,
    string SnapshotTree, ArtifactRef TaskPacketRef, ArtifactRef SelectedProjectionRef,
    string WorkflowId, string WorkflowVersion, string WorkflowInstance,
    string SourceTree, string SourceHead, string? SourceBranch) : DomainEventPayload
{
    public EventType Type() => EventType.Of("lane.worktree_bound");
    public int SchemaVersion() => 1;
}

/// <summary>Durable binding to an external CLI session emitted at its first validated init event.</summary>
public sealed record ExternalLaneSessionBound(ExecutionId ExecutionId, LaneId LaneId, string SessionId,
    string Model, IReadOnlyList<string> Tools, IReadOnlyDictionary<string, bool> Capabilities) : DomainEventPayload
{
    public EventType Type() => EventType.Of("external_lane.session_bound");
    public int SchemaVersion() => 1;
}

/// <summary>Bounded usage reported by the external CLI after its final result event.</summary>
public sealed record ExternalLaneUsageRecorded(ExecutionId ExecutionId, LaneId LaneId, string SessionId,
    long? InputTokens, long? OutputTokens, long? CacheReadInputTokens, long? CacheCreationInputTokens,
    decimal? TotalCostUsd, int ToolUseCount, int EventCount, long StreamBytes, ArtifactRef ResultRef) : DomainEventPayload
{
    public EventType Type() => EventType.Of("external_lane.usage_recorded");
    public int SchemaVersion() => 1;
}
