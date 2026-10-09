namespace OmniCore.Host;

using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

/// <summary>Host-side atomic admission for bounded model work. Canonical usage remains in the
/// event journal; SQLite rows only hold ceilings until a matching receipt is durable. In-flight
/// ownership is deliberately process-local, so a dispatched reservation recovered after restart
/// stays uncertain and blocks retry rather than being adopted.</summary>
internal sealed class RunBudgetPool
{
    internal sealed record Allocation(decimal CostUsd, long Tokens, long Turns, long ToolCalls);
    private static readonly ConditionalWeakTable<IEventStore, RunBudgetPool> Stores = new();
    private readonly object _gate = new();
    private readonly Dictionary<(SessionId Session, RunId Run), object> _admissionGates = new();
    private readonly HashSet<string> _ownedPrimaryInvocations = new(StringComparer.Ordinal);

    internal static RunBudgetPool For(IEventStore store) => Stores.GetValue(store, _ => new());

    internal IDisposable EnterAdmission(SessionId session, RunId run)
    {
        object admission;
        lock (_gate)
        {
            if (!_admissionGates.TryGetValue((session, run), out admission!))
                _admissionGates.Add((session, run), admission = new object());
        }
        Monitor.Enter(admission);
        return new MonitorLease(admission);
    }

    internal IDisposable TrackOwnedPrimary(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        lock (_gate)
        {
            if (!_ownedPrimaryInvocations.Add(identity))
                throw new InvalidOperationException("Primary invocation ownership identity is already active.");
        }
        return new OwnershipLease(this, identity);
    }

    internal bool OwnsPendingPrimary(DomainEvent evt, ModelStepStarted step,
        SqliteSpendReservationStore? reservations)
    {
        var identity = PrimaryIdentity(evt, step);
        if (identity is null) return false;
        lock (_gate)
            if (!_ownedPrimaryInvocations.Contains(identity)) return false;
        if (reservations is null) return false;
        return reservations.HasFullDispatchedBound(identity)
            || reservations.HasFullDispatchedBound(TokenReservationIdentity(identity));
    }

    internal static string? PrimaryIdentity(DomainEvent evt, ModelStepStarted step)
    {
        if (evt.RunId is not { } run || evt.LaneId is not { } lane || evt.SessionId is not { } session
            || step.StepIndex < 0 || string.IsNullOrWhiteSpace(step.TurnId.ToString())) return null;
        return PrimaryIdentity(session, run, lane, step.TurnId, step.StepIndex);
    }

    internal static string PrimaryIdentity(SessionId session, RunId run, LaneId lane, TurnId turn, int index) =>
        $"primary/{session}/{run}/{lane}/{turn}/{index}";

    internal static string TokenReservationIdentity(string primaryIdentity) => "tokens/" + primaryIdentity;

    /// <summary>Remaining child ceilings held against the Run until that child's owned execution
    /// terminates. Canonical completed usage is already counted at Run scope, so only unspent
    /// child headroom is returned here. Dispatched child calls remain in the SQLite pending ledger
    /// and are subtracted from that headroom to avoid counting the same bound twice.</summary>
    internal Allocation ReadChildAllocations(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        IArtifactStore artifacts, IEventStore store, SqliteSpendReservationStore? reservations,
        SessionId session, RunId run, LaneId? exceptLane = null)
    {
        var payloads = events.Where(evt => evt.RunId == run || evt.CorrelationId == run)
            .Select(codecs.Decode).ToArray();
        var tasks = payloads.OfType<TaskCreated>().Where(task => task.ParentTaskId is not null).ToArray();
        var lanesByTask = payloads.OfType<LaneCreated>().GroupBy(lane => lane.TaskId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var delegations = payloads.OfType<DelegationCreated>().Select(item => item.Delegation)
            .ToDictionary(item => item.ChildTaskId);
        var executionStarts = payloads.OfType<AgentExecutionStarted>().GroupBy(item => item.LaneId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var childExecutionTerminals = payloads.OfType<AgentExecutionCompleted>().Select(item => item.ExecutionId)
            .Concat(payloads.OfType<AgentExecutionFailed>().Select(item => item.ExecutionId)).ToHashSet();
        decimal cost = 0m;
        long tokens = 0, turns = 0, tools = 0;
        var taskProjection = TaskGraphProjection.Replay(codecs, events.Where(evt => evt.RunId == run).ToArray());
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var spendReader = new CanonicalSpendReader(codecs, artifacts);
        var pool = For(store);
        foreach (var task in tasks)
        {
            if (!lanesByTask.TryGetValue(task.TaskId, out var lanes) || lanes.Length != 1)
                throw new InvalidDataException("Child budget ownership is ambiguous.");
            var lane = lanes[0].LaneId;
            if (lane == exceptLane) continue;
            if (delegations.TryGetValue(task.TaskId, out var delegation)
                && executionStarts.TryGetValue(lane, out var starts) && starts.Length == 1
                && childExecutionTerminals.Contains(starts[0].ExecutionId)) continue;
            if (StateMachines.IsTaskTerminal(taskProjection.Get(task.TaskId)?.State ?? TaskState.Failed)) continue;

            var laneEvents = events.Where(evt => evt.RunId == run && evt.LaneId == lane).ToArray();
            var laneFacts = laneEvents.Select(codecs.Decode).ToArray();
            var usedTurns = laneFacts.OfType<TurnStarted>().Select(item => item.TurnId).Distinct().LongCount();
            var usedTools = laneFacts.OfType<ToolCallRequested>().Select(item => item.ToolCallId).Distinct().LongCount();
            turns = checked(turns + Math.Max(0, (long)(task.Budget.MaxTurns ?? 0) - usedTurns));
            tools = checked(tools + Math.Max(0, (long)(task.Budget.MaxToolCalls ?? 0) - usedTools));

            if (task.Budget.MaxTokens is { } tokenLimit)
            {
                var laneTokens = RunTokenBudgetReader.Read(laneEvents, codecs, run, tokenLimit,
                    (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
                var spent = laneTokens.ObservedSettledTokens ?? 0;
                var pending = PendingAmountForLane(laneEvents, codecs, lane, run, reservations, token: true);
                if (pending != decimal.Truncate(pending) || pending > long.MaxValue)
                    throw new InvalidDataException("Token reservation is not an exact integral amount.");
                tokens = checked(tokens + Math.Max(0L, tokenLimit - spent - decimal.ToInt64(pending)));
            }

            if (task.Budget.MaxCostUsd is { } costLimit)
            {
                var primary = spendReader.ReadPrimary(laneEvents, session, run, day,
                    (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
                var meta = spendReader.ReadMeta(laneEvents, session, run, day);
                var spent = primary.RunUsd is { } p && meta.RunUsd is { } m ? checked(p + m) : 0m;
                var pending = PendingAmountForLane(laneEvents, codecs, lane, run, reservations, token: false);
                cost = checked(cost + Math.Max(0m, costLimit - spent - pending));
            }
        }
        return new(cost, tokens, turns, tools);
    }

    private decimal PendingAmountForLane(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        LaneId lane, RunId run, SqliteSpendReservationStore? reservations, bool token)
    {
        // A bound remains charged in the pool while a receipt is waiting for its SQLite settlement,
        // whether or not the start still has an open completion edge.
        var starts = events.Where(evt => evt.RunId == run && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(pair => pair.Payload is ModelStepStarted)
            .Select(pair => (pair.Event, Start: (ModelStepStarted)pair.Payload)).ToArray();
        var amount = 0m;
        foreach (var pair in starts)
        {
            var identity = PrimaryIdentity(pair.Event, pair.Start);
            if (identity is null) continue;
            var reservationId = token ? TokenReservationIdentity(identity) : identity;
            var pending = reservations?.ReadPendingAmount(reservationId) ?? 0m;
            amount = checked(amount + pending);
        }
        return amount;
    }

    private void ReleaseOwnedPrimary(string identity)
    {
        lock (_gate) _ownedPrimaryInvocations.Remove(identity);
    }

    private sealed class MonitorLease(object gate) : IDisposable
    {
        private object? _gate = gate;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _gate, null);
            if (value is not null) Monitor.Exit(value);
        }
    }

    private sealed class OwnershipLease(RunBudgetPool owner, string identity) : IDisposable
    {
        private RunBudgetPool? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOwnedPrimary(identity);
    }
}
