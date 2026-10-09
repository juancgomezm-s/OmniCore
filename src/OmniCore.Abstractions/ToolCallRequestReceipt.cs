namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>In-process capability minted only after the exact ToolCallRequested event was
/// durably appended. It lets the Host's live tool pipeline avoid writing a second request.</summary>
public sealed class ToolCallRequestReceipt
{
    public ToolCallRequestReceipt(DomainEvent persisted, ToolCallRequested request)
    {
        EventId = persisted.EventId;
        SessionId = persisted.SessionId;
        Sequence = persisted.Sequence;
        RunId = persisted.RunId;
        TaskId = persisted.TaskId;
        LaneId = persisted.LaneId;
        TurnId = persisted.TurnId;
        ExecutionId = persisted.ExecutionId;
        Request = request;
    }

    public EventId EventId { get; }
    public SessionId SessionId { get; }
    public long Sequence { get; }
    public RunId? RunId { get; }
    public TaskId? TaskId { get; }
    public LaneId? LaneId { get; }
    public TurnId? TurnId { get; }
    public ExecutionId? ExecutionId { get; }
    public ToolCallRequested Request { get; }

    public bool MatchesCall(ValidatedToolCall call) => Request.ToolCallId == call.ToolCallId
        && Request.ProviderCallId == call.ProviderCallId
        && Request.ToolName == call.ToolId.ToString()
        && Request.ArgumentsJson == call.NormalizedArgumentsJson;

    public bool MatchesScope(RunId? run, TaskId? task, LaneId? lane, TurnId? turn, ExecutionId? execution) =>
        RunId == run && TaskId == task && LaneId == lane && TurnId == turn && ExecutionId == execution;
}
