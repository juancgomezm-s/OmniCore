namespace OmniCore.Engine;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Durable, turn-scoped FIFO projection for explicit steering input.</summary>
public static class SteeringQueue
{
    public sealed record Item(SteeringId Id, RunId RunId, LaneId LaneId, TurnId TurnId,
        string InputPartsJson, string? Origin, EventId ReceivedEventId, long Sequence);

    /// <summary>
    /// Persists a redacted steering input only for an explicitly identified, nonterminal Run and
    /// its exact open Turn/Lane. Repeating an identical ID and request is idempotent.
    /// </summary>
    public static bool TryReceive(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        SteeringId id, RunId run, LaneId lane, TurnId turn, string text, string? origin)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(turn);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var events = store.ReadFrom(session, 1);
        if (!HasRun(events, codecs, run) || RunProjection.Replay(session, run, codecs, events).IsTerminal()
            || !LaneBelongsToRun(events, codecs, run, lane)
            || !IsOpenTurn(events, codecs, run, lane, turn))
            return false;
        var task = TaskForLane(events, codecs, lane);
        if (task is null) return false;

        var safeText = new RedactionPolicy().Redact(text);
        var parts = "[\"" + JsonEncodedText.Encode(safeText) + "\"]";
        var projection = Project(events, codecs);
        if (projection.TryGetValue(id, out var existing))
        {
            return existing.Item.RunId == run && existing.Item.LaneId == lane
                && existing.Item.TurnId == turn
                && string.Equals(existing.Item.InputPartsJson, parts, StringComparison.Ordinal)
                && string.Equals(existing.Item.Origin, origin, StringComparison.Ordinal);
        }

        using (ExecutionScope.Begin(new ExecutionScopeState(run, task, lane, turn)))
            new EventStream(store, codecs, session).Append(
                new TurnSteeringReceived(id, run, lane, turn, parts, origin), DurabilityClass.Barrier);
        return true;
    }

    /// <summary>Pending inputs in journal order for exactly the requested Run/Lane/Turn.</summary>
    public static IReadOnlyList<Item> Pending(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, LaneId lane, TurnId turn)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codecs);
        var events = store.ReadFrom(session, 1);
        return Project(events, codecs).Values
            .Where(value => value.Outcome == Outcome.Pending && value.Item.RunId == run && value.Item.LaneId == lane
                && value.Item.TurnId == turn)
            .Select(value => value.Item)
            .OrderBy(item => item.Sequence)
            .ToArray();
    }

    /// <summary>Applied inputs in journal order for exactly the requested Run/Lane/Turn.</summary>
    public static IReadOnlyList<Item> Applied(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, LaneId lane, TurnId turn)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codecs);
        return Project(store.ReadFrom(session, 1), codecs).Values
            .Where(value => value.Outcome == Outcome.Applied && value.Item.RunId == run
                && value.Item.LaneId == lane && value.Item.TurnId == turn)
            .Select(value => value.Item)
            .OrderBy(item => item.Sequence)
            .ToArray();
    }

    public static IReadOnlyList<DomainEventPayload> ApplicationEvents(IReadOnlyList<Item> items,
        RunId run, LaneId lane, TurnId turn, int stepIndex)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (stepIndex < 0) throw new ArgumentOutOfRangeException(nameof(stepIndex));
        var result = new List<DomainEventPayload>(items.Count);
        foreach (var item in items)
        {
            EnsureScope(item, run, lane, turn);
            result.Add(new TurnSteeringApplied(item.Id, run, lane, turn, stepIndex));
        }
        return result;
    }

    public static IReadOnlyList<DomainEventPayload> DropEvents(IReadOnlyList<Item> items,
        RunId run, LaneId lane, TurnId turn, string reason)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason must be non-empty.", nameof(reason));
        var result = new List<DomainEventPayload>(items.Count);
        foreach (var item in items)
        {
            EnsureScope(item, run, lane, turn);
            result.Add(new TurnSteeringDropped(item.Id, run, lane, turn, reason));
        }
        return result;
    }

    private static void EnsureScope(Item item, RunId run, LaneId lane, TurnId turn)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RunId != run || item.LaneId != lane || item.TurnId != turn)
            throw new InvalidOperationException("Steering item is outside the target Run/Lane/Turn scope.");
    }

    private static bool HasRun(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs, RunId run)
        => events.Any(evt => codecs.Decode(evt) is RunCreated created && created.RunId == run);

    private static bool LaneBelongsToRun(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId run, LaneId lane)
    {
        var taskRuns = events.Select(evt => codecs.Decode(evt)).OfType<TaskCreated>()
            .ToDictionary(item => item.TaskId, item => item.RunId);
        var task = TaskForLane(events, codecs, lane);
        return task is { } taskId && taskRuns.TryGetValue(taskId, out var owner) && owner == run;
    }

    private static TaskId? TaskForLane(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs, LaneId lane)
        => events.Select(evt => codecs.Decode(evt)).OfType<LaneCreated>()
            .FirstOrDefault(item => item.LaneId == lane)?.TaskId;

    private static bool IsOpenTurn(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId run, LaneId lane, TurnId turn)
    {
        var started = false;
        var closed = false;
        foreach (var evt in events)
        {
            if (evt.RunId != run) continue;
            switch (codecs.Decode(evt))
            {
                case TurnStarted value when value.TurnId == turn:
                    started = value.LaneId == lane;
                    closed = false;
                    break;
                case TurnCompleted completed when completed.TurnId == turn:
                case TurnInterrupted interrupted when interrupted.TurnId == turn:
                case TurnAbandoned abandoned when abandoned.TurnId == turn:
                    closed = true;
                    break;
            }
        }
        return started && !closed;
    }

    private enum Outcome { Pending, Applied, Dropped }

    private sealed record ProjectionItem(Item Item, Outcome Outcome);

    private static Dictionary<SteeringId, ProjectionItem> Project(IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs)
    {
        var byId = new Dictionary<SteeringId, ProjectionItem>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case TurnSteeringReceived received:
                    var priorEvents = events.TakeWhile(value => value.EventId != evt.EventId).ToArray();
                    var receivedTask = TaskForLane(priorEvents, codecs, received.LaneId);
                    ValidateEnvelope(evt, received.RunId, received.LaneId, received.TurnId, receivedTask,
                        "turn.steering_received");
                    var item = new Item(received.SteeringId, received.RunId, received.LaneId, received.TurnId,
                        received.InputPartsJson, received.Origin, evt.EventId, evt.Sequence);
                    if (!HasRun(priorEvents, codecs, received.RunId)
                        || RunProjection.Replay(evt.SessionId, received.RunId, codecs, priorEvents).IsTerminal()
                        || !LaneBelongsToRun(priorEvents, codecs, received.RunId, received.LaneId)
                        || !IsOpenTurn(priorEvents, codecs, received.RunId, received.LaneId, received.TurnId))
                        throw Invalid("receive outside open Run/Lane/Turn", "turn.steering_received");
                    if (!byId.TryAdd(received.SteeringId, new ProjectionItem(item, Outcome.Pending)))
                        throw Invalid("duplicate or cross-scope receive", "turn.steering_received");
                    break;
                case TurnSteeringApplied applied:
                    ValidateEnvelope(evt, applied.RunId, applied.LaneId, applied.TurnId,
                        TaskForLane(events, codecs, applied.LaneId), "turn.steering_applied");
                    if (applied.StepIndex < 0 || !byId.TryGetValue(applied.SteeringId, out var pending)
                        || pending.Outcome != Outcome.Pending || !Matches(pending.Item, applied.RunId, applied.LaneId, applied.TurnId))
                        throw Invalid("invalid application", "turn.steering_applied");
                    byId[applied.SteeringId] = pending with { Outcome = Outcome.Applied };
                    break;
                case TurnSteeringDropped dropped:
                    ValidateEnvelope(evt, dropped.RunId, dropped.LaneId, dropped.TurnId,
                        TaskForLane(events, codecs, dropped.LaneId), "turn.steering_dropped");
                    if (string.IsNullOrWhiteSpace(dropped.Reason)
                        || !byId.TryGetValue(dropped.SteeringId, out var pendingDrop)
                        || pendingDrop.Outcome != Outcome.Pending || !Matches(pendingDrop.Item, dropped.RunId, dropped.LaneId, dropped.TurnId))
                        throw Invalid("invalid drop", "turn.steering_dropped");
                    byId[dropped.SteeringId] = pendingDrop with { Outcome = Outcome.Dropped };
                    break;
            }
        }
        return byId;
    }

    private static bool Matches(Item item, RunId run, LaneId lane, TurnId turn)
        => item.RunId == run && item.LaneId == lane && item.TurnId == turn;

    private static void ValidateEnvelope(DomainEvent evt, RunId run, LaneId lane, TurnId turn,
        TaskId? task, string type)
    {
        if (task is null || evt.RunId != run || evt.TaskId != task || evt.LaneId != lane || evt.TurnId != turn)
            throw Invalid("payload/envelope scope mismatch", type);
    }

    private static InvalidStateTransitionException Invalid(string from, string to)
        => new("steering", from, to);
}
