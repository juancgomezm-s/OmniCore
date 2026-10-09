namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resolves legacy event ownership only when a Run has a single possible Lane.</summary>
internal sealed class CanonicalEventOwnership
{
    private readonly Dictionary<(RunId Run, TurnId Turn), LaneId> _turns = new();
    private readonly Dictionary<(RunId Run, ToolCallId Call), LaneId> _calls = new();
    private readonly Dictionary<(RunId Run, TaskId Task), List<LaneId>> _taskLanes = new();
    private readonly Dictionary<RunId, HashSet<LaneId>> _runLanes = new();

    public CanonicalEventOwnership(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(events);
        var activeTurns = new Dictionary<RunId, Dictionary<TurnId, LaneId>>();
        foreach (var evt in events)
        {
            var run = evt.RunId ?? evt.CorrelationId;
            if (run is null) continue;
            var payload = codecs.Decode(evt);
            switch (payload)
            {
                case LaneCreated laneCreated:
                    if (!_runLanes.TryGetValue(run, out var lanes)) _runLanes.Add(run, lanes = []);
                    lanes.Add(laneCreated.LaneId);
                    if (!_taskLanes.TryGetValue((run, laneCreated.TaskId), out var taskLanes)) _taskLanes.Add((run, laneCreated.TaskId), taskLanes = []);
                    taskLanes.Add(laneCreated.LaneId);
                    break;
                case TurnStarted turn:
                    _turns[(run, turn.TurnId)] = turn.LaneId;
                    if (!activeTurns.TryGetValue(run, out var runTurns)) activeTurns.Add(run, runTurns = []);
                    runTurns[turn.TurnId] = turn.LaneId;
                    break;
                case ToolCallRequested request:
                    LaneId? lane = ExplicitLane(evt, run);
                    if (lane is null && activeTurns.TryGetValue(run, out var open)
                        && open.Count == 1) lane = open.Values.Single();
                    lane ??= activeTurns.TryGetValue(run, out var anyOpen) && anyOpen.Count > 0
                        ? null : SoleLane(run);
                    if (lane is not { } owner) break;
                    var key = (run, request.ToolCallId);
                    if (_calls.TryGetValue(key, out var prior) && prior != owner)
                        throw new InvalidDataException("Persisted ToolCall has conflicting Lane ownership.");
                    _calls[key] = owner;
                    break;
                case TurnCompleted completed:
                    if (activeTurns.TryGetValue(run, out var completedTurns)) completedTurns.Remove(completed.TurnId);
                    break;
                case TurnInterrupted interrupted:
                    if (activeTurns.TryGetValue(run, out var interruptedTurns)) interruptedTurns.Remove(interrupted.TurnId);
                    break;
                case TurnAbandoned abandoned:
                    if (activeTurns.TryGetValue(run, out var abandonedTurns)) abandonedTurns.Remove(abandoned.TurnId);
                    break;
            }
        }
    }

    public LaneId? LaneOf(DomainEvent evt)
    {
        var run = evt.RunId ?? evt.CorrelationId;
        if (run is null) return evt.LaneId;
        var direct = ExplicitLane(evt, run);
        if (direct is not null) return direct;
        if (evt.ToolCallId is { } call && _calls.TryGetValue((run, call), out var callLane)) return callLane;
        if (evt.TaskId is { } task && _taskLanes.TryGetValue((run, task), out var taskLanes)
            && taskLanes.Count == 1) return taskLanes[0];
        return SoleLane(run);
    }

    public bool IsAmbiguousToolCall(DomainEvent evt)
    {
        var run = evt.RunId ?? evt.CorrelationId;
        return run is not null && evt.ToolCallId is { } call && ExplicitLane(evt, run) is null
            && !_calls.ContainsKey((run, call)) && SoleLane(run) is null;
    }

    private LaneId? ExplicitLane(DomainEvent evt, RunId run)
    {
        LaneId? fromTurn = evt.TurnId is { } turn && _turns.TryGetValue((run, turn), out var turnLane)
            ? turnLane : null;
        var direct = evt.LaneId;
        if (direct is not null && fromTurn is not null && direct != fromTurn)
            throw new InvalidDataException("Persisted event has conflicting Turn and Lane ownership.");
        return direct ?? fromTurn;
    }

    private LaneId? SoleLane(RunId run) => _runLanes.TryGetValue(run, out var lanes)
        && lanes.Distinct().Take(2).Count() == 1 ? lanes.First() : null;
}
