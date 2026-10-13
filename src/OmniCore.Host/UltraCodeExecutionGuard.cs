namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

/// <summary>Run-scoped execution ceilings, not a scheduler or a permission grant.
/// Replays fresh canonical evidence at each dispatch boundary; suspension/retry cannot
/// reset counters and a budget continuation cannot widen the authorization.</summary>
internal static class UltraCodeExecutionGuard
{
    public static void Validate(IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs,
        IArtifactStore artifacts, IEventStore store, SqliteSpendReservationStore? reservations,
        SessionId session, RunId run, TurnId turn,
        bool newTurn, bool invocation, decimal? quote, ToolCallId? toolCall = null)
    {
        var authority = RunProjection.Replay(session, run, codecs, journal).ModeAuthority;
        if (authority?.Authorization is not { } grant) return;
        var now = DateTimeOffset.UtcNow;
        if (grant.IsExpiredAt(now)) throw Denied("autorización vencida");
        if (authority.ObjectiveDigest != grant.ObjectiveDigest || authority.ObjectiveRevision != grant.ObjectiveRevision
            || authority.PolicyRevision != grant.PolicyRevision) throw Denied("alcance de autorización distinto");
        var own = journal.Where(evt => evt.RunId == run || evt.CorrelationId == run).ToArray();
        var turns = own.Select(codecs.Decode).OfType<TurnStarted>().Select(start => start.TurnId).Distinct().ToArray();
        var allocations = RunBudgetPool.For(store).ReadChildAllocations(journal, codecs, artifacts, store,
            reservations, session, run, ExecutionScope.Current?.LaneId);
        if (turns.Length + allocations.Turns > grant.Limits.MaxTurns
            || newTurn && !turns.Contains(turn) && turns.Length + allocations.Turns >= grant.Limits.MaxTurns)
            throw Denied("límite de turnos alcanzado");
        var payloads = own.Select(codecs.Decode).ToArray();
        var tools = payloads.OfType<ToolCallRequested>().Select(call => call.ToolCallId).Distinct().ToArray();
        var externalTools = CountExternalToolUses(payloads);
        var usedTools = checked((long)tools.Length + externalTools);
        if (usedTools + allocations.ToolCalls > grant.Limits.MaxToolCalls
            || toolCall is not null && !tools.Contains(toolCall)
            && usedTools + allocations.ToolCalls >= grant.Limits.MaxToolCalls)
            throw Denied("límite de herramientas alcanzado");
        // No monetary admission quote is required for a quota the user did not set.
        // This does not authorize a provider route or change tool permissions.
        if (grant.Limits.MaxSpendUsd is not { } spendLimit) return;
        var reader = new CanonicalSpendReader(codecs, artifacts);
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var pool = RunBudgetPool.For(store);
        var primary = reader.ReadPrimary(own, session, run, day,
            (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
        var meta = reader.ReadMeta(own, session, run, day);
        if (primary.Incomplete || meta.Incomplete || primary.RunUsd is null || meta.RunUsd is null)
            throw Denied("consumo histórico desconocido");
        if (invocation && quote is null) throw Denied("cota monetaria de invocación desconocida");
        try
        {
            if (checked(primary.RunUsd.Value + meta.RunUsd.Value + (invocation ? quote!.Value : 0m)) > spendLimit)
                throw Denied("límite de gasto insuficiente");
        }
        catch (OverflowException) { throw Denied("consumo no representable"); }
    }

    private static long CountExternalToolUses(IReadOnlyList<DomainEventPayload> payloads)
    {
        var receipts = payloads.OfType<ExternalLaneUsageRecorded>().ToArray();
        var activities = payloads.OfType<ExternalLaneActivityObserved>().ToArray();
        if (receipts.Any(item => item.ToolUseCount < 0) || activities.Any(item => item.ToolUseCount < 0)
            || receipts.GroupBy(item => (item.ExecutionId, item.LaneId)).Any(group => group.Count() != 1)
            || activities.GroupBy(item => (item.ExecutionId, item.LaneId)).Any(group => group.Count() != 1))
            throw Denied("actividad externa inválida");
        var activityByExecution = activities.ToDictionary(item => (item.ExecutionId, item.LaneId));
        var receiptKeys = receipts.Select(item => (item.ExecutionId, item.LaneId)).ToHashSet();
        long count = 0;
        foreach (var receipt in receipts)
        {
            var key = (receipt.ExecutionId, receipt.LaneId);
            if (activityByExecution.TryGetValue(key, out var activity)
                && activity.ToolUseCount != receipt.ToolUseCount)
                throw Denied("actividad externa y recibo no coinciden");
            count = checked(count + (activityByExecution.TryGetValue(key, out activity)
                ? activity.ToolUseCount : receipt.ToolUseCount));
        }
        foreach (var activity in activities)
            if (!receiptKeys.Contains((activity.ExecutionId, activity.LaneId)))
                count = checked(count + activity.ToolUseCount);
        return count;
    }

    private static BudgetExceededException Denied(string reason) => new("UltraCode: " + reason);
}
