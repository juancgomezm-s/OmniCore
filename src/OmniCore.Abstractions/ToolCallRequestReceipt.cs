namespace OmniCore.Abstractions;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;

/// <summary>In-process capability minted only after the exact ToolCallRequested event was
/// durably appended. It lets the Host's live tool pipeline avoid writing a second request.</summary>
public sealed class ToolCallRequestReceipt
{
    private readonly string _argumentsDigest;

    public ToolCallRequestReceipt(DomainEvent persisted, ToolCallRequested request,
        string? matchingArgumentsJson = null)
    {
        if (matchingArgumentsJson is not null
            && (request.ToolName != "memory.propose"
                || request.ArgumentsJson != "{\"screenedByMemoryPolicy\":true}"))
            throw new ArgumentException("Only the screened memory.propose request may carry an in-memory argument match.",
                nameof(matchingArgumentsJson));
        EventId = persisted.EventId;
        SessionId = persisted.SessionId;
        Sequence = persisted.Sequence;
        RunId = persisted.RunId;
        TaskId = persisted.TaskId;
        LaneId = persisted.LaneId;
        TurnId = persisted.TurnId;
        ExecutionId = persisted.ExecutionId;
        Request = request;
        _argumentsDigest = Digest(matchingArgumentsJson ?? request.ArgumentsJson);
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
        && _argumentsDigest == Digest(call.NormalizedArgumentsJson);

    public bool MatchesScope(RunId? run, TaskId? task, LaneId? lane, TurnId? turn, ExecutionId? execution) =>
        RunId == run && TaskId == task && LaneId == lane && TurnId == turn && ExecutionId == execution;

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
