using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Host;

/// <summary>Read-only ownership resolution. Neither an event cursor nor ambient scope grants visibility.</summary>
internal sealed class LaneConversationScope
{
    private readonly IEventCodecRegistry _codecs;
    private readonly Dictionary<RunId, TaskId> _roots = new();
    private readonly Dictionary<(RunId Run, LaneId Lane), TaskId> _lanes = new();
    private readonly Dictionary<(RunId Run, TurnId Turn), LaneId> _turns = new();
    private readonly Dictionary<(RunId Run, ToolCallId Call), LaneId> _calls = new();
    private readonly RunId _run;
    public LaneId? TargetLane { get; }
    public bool AllowsSessionHistory { get; }
    public TaskId? TargetTask => TargetLane is { } lane && _lanes.TryGetValue((_run, lane), out var task) ? task : null;
    public bool HasMultipleLanes => _lanes.Keys.Count(key => key.Run == _run) > 1;

    public LaneConversationScope(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId run, LaneId? requestedLane)
    {
        _codecs = codecs; _run = run;
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case RunCreated created:
                    if (!_roots.TryAdd(created.RunId, created.RootTask)) throw InvalidOwner();
                    break;
                case LaneCreated lane when evt.RunId is { } owner:
                    if (!_lanes.TryAdd((owner, lane.LaneId), lane.TaskId)) throw InvalidOwner();
                    break;
                case TurnStarted turn when evt.RunId is { } owner:
                    if (!_turns.TryAdd((owner, turn.TurnId), turn.LaneId)) throw InvalidOwner();
                    break;
            }
        }
        TargetLane = requestedLane ?? RootLane(run);
        // A new-Run history query with no Lane remains a principal conversation query.
        // Children never implicitly inherit the principal Session transcript.
        AllowsSessionHistory = TargetLane is not null && TargetLane == RootLane(run)
            || requestedLane is null && !_roots.ContainsKey(run);
        foreach (var evt in events)
            if (evt.RunId is { } owner && codecs.Decode(evt) is ToolCallRequested call
                && OwnerLane(evt) is { } lane)
                if (!_calls.TryAdd((owner, call.ToolCallId), lane)) throw InvalidOwner();
    }

    public bool CanRead(DomainEvent evt)
    {
        if (evt.RunId is not { } run) return false;
        if (_codecs.Decode(evt) is not (UserInputReceived or AssistantMessageRecorded or ModelCompleted
            or TurnStarted or ModelStepStarted or ModelStepCompleted or ToolCallRequested
            or ToolCallSucceeded or ToolCallFailed or ToolCallRejected or TurnSteeringReceived
            or TurnSteeringApplied or TurnSteeringDropped or InteractionRequested or InteractionResolved
            or ContextCheckpointRecorded)) return false;
        if (run == _run)
            return TargetLane is not null && TargetTask is not null && OwnerLane(evt) == TargetLane;
        return AllowsSessionHistory
            && _codecs.Decode(evt) is UserInputReceived or AssistantMessageRecorded or ModelCompleted
            && RootLane(run) is { } root && OwnerLane(evt) == root;
    }

    public LaneId? OwnerLane(DomainEvent evt)
    {
        if (evt.RunId is not { } run) return null;
        var payload = _codecs.Decode(evt);
        var declared = payload switch {
            TurnStarted e => e.LaneId, AssistantMessageRecorded e => e.LaneId,
            TurnSteeringReceived e => e.LaneId, TurnSteeringApplied e => e.LaneId,
            TurnSteeringDropped e => e.LaneId, _ => null,
        };
        var turn = payload switch {
            TurnStarted e => e.TurnId, ModelCompleted e => e.TurnId,
            ModelStepStarted e => e.TurnId, ModelStepCompleted e => e.TurnId, _ => evt.TurnId,
        };
        var call = payload switch {
            ToolCallRequested e => e.ToolCallId, ToolCallSucceeded e => e.ToolCallId,
            ToolCallFailed e => e.ToolCallId, ToolCallRejected e => e.ToolCallId, _ => evt.ToolCallId,
        };
        var fromTurn = turn is not null && _turns.TryGetValue((run, turn), out var turnLane) ? turnLane : null;
        var fromCall = call is not null && _calls.TryGetValue((run, call), out var callLane) ? callLane : null;
        var candidates = new[] { declared, evt.LaneId, fromTurn, fromCall }.Where(lane => lane is not null).Distinct().ToArray();
        if (candidates.Length > 1)
            throw new InvalidDataException("Persisted conversation has conflicting Lane ownership.");
        if (candidates.Length == 1) return candidates[0];
        if (evt.TaskId is { } task)
        {
            var taskLanes = _lanes.Where(pair => pair.Key.Run == run && pair.Value == task).Select(pair => pair.Key.Lane).ToArray();
            return taskLanes.Length == 1 ? taskLanes[0] : null;
        }
        // SendInput is Run-level and belongs to its principal Lane (ADR-0035).
        if (payload is UserInputReceived) return RootLane(run);
        // Legacy tool/checkpoint ownership is only unambiguous in a one-Lane Run.
        var sole = _lanes.Keys.Where(key => key.Run == run).Select(key => key.Lane).ToArray();
        return sole.Length == 1 ? sole[0] : null;
    }

    private LaneId? RootLane(RunId run)
    {
        if (!_roots.TryGetValue(run, out var task)) return null;
        var roots = _lanes.Where(pair => pair.Key.Run == run && pair.Value == task).Select(pair => pair.Key.Lane).ToArray();
        return roots.Length == 1 ? roots[0] : null;
    }

    private static InvalidDataException InvalidOwner() => new("Persisted conversation has duplicate ownership roots.");
}
