namespace OmniCore.Domain;

/// <summary>Durable lifecycle of a particular integration proposal. This is the M7 worktree
/// entity state; ToolCall events remain the generic permission/execution state machine.</summary>
public enum WorktreeIntegrationState
{
    Started,
    Completed,
    NotApplied,
    Conflict,
}

/// <summary>Per-target recovery state contains only safe relative paths and classifications.</summary>
public sealed record WorktreeIntegrationFileStatus(string RelativePath, ReconciliationOutcome Outcome);

public sealed record WorktreeIntegrationStateRecorded(ToolCallId ToolCallId, string OwnershipId,
    string ProposalId, WorktreeIntegrationState State,
    IReadOnlyList<WorktreeIntegrationFileStatus>? Files = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("worktree.integration_state.recorded");
    public int SchemaVersion() => 1;
}
