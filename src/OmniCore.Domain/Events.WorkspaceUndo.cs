namespace OmniCore.Domain;

/// <summary>Durable lifecycle for a compensation attempt. It records newly appended facts and
/// never rewrites the effect events it compensates.</summary>
public enum UndoOutcome { Applied, NoOp, Conflict, Unsupported, Unknown }

public sealed record UndoFileState(string RelativePath, ReconciliationOutcome Outcome);
public sealed record UndoFileIntent(string RelativePath, string ExpectedBeforeUndoHash,
    string ExpectedRestoredHash, string? QuarantinePath = null);

public sealed record UndoAttribution(EventId SourceEventId, RunId? SourceRunId, TaskId? SourceTaskId,
    LaneId? SourceLaneId, TurnId? SourceTurnId, ExecutionId? SourceExecutionId,
    ExecutionId UndoExecutionId);

public sealed record UndoStarted(CommandId CommandId, string UndoId, ToolCallId OriginalEffect, UndoAttribution Attribution,
    IReadOnlyList<UndoFileIntent> Files, string WorkspaceIdentity) : DomainEventPayload
{
    public EventType Type() => EventType.Of("workspace.undo.started");
    public int SchemaVersion() => 1;
}

public sealed record UndoCompleted(CommandId CommandId, string UndoId, ToolCallId OriginalEffect, UndoAttribution Attribution,
    UndoOutcome Outcome, IReadOnlyList<UndoFileState> Files, string WorkspaceIdentity) : DomainEventPayload
{
    public EventType Type() => EventType.Of("workspace.undo.completed");
    public int SchemaVersion() => 1;
}
