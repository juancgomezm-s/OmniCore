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
