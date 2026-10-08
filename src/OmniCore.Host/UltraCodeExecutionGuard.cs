namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;

/// <summary>Run-scoped execution ceilings, not a scheduler or a permission grant.
/// Replays fresh canonical evidence at each dispatch boundary; suspension/retry cannot
/// reset counters and a budget continuation cannot widen the authorization.</summary>
internal static class UltraCodeExecutionGuard
{
    public static void Validate(IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs,
        IArtifactStore artifacts, SessionId session, RunId run, TurnId turn,
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
        if (turns.Length > grant.Limits.MaxTurns || newTurn && !turns.Contains(turn) && turns.Length >= grant.Limits.MaxTurns)
            throw Denied("límite de turnos alcanzado");
        var tools = own.Select(codecs.Decode).OfType<ToolCallRequested>().Select(call => call.ToolCallId).Distinct().ToArray();
        if (tools.Length > grant.Limits.MaxToolCalls || toolCall is not null && !tools.Contains(toolCall)
            && tools.Length >= grant.Limits.MaxToolCalls) throw Denied("límite de herramientas alcanzado");
        var reader = new CanonicalSpendReader(codecs, artifacts);
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var primary = reader.ReadPrimary(own, session, run, day);
        var meta = reader.ReadMeta(own, session, run, day);
        if (primary.Incomplete || meta.Incomplete || primary.RunUsd is null || meta.RunUsd is null)
            throw Denied("consumo histórico desconocido");
        if (invocation && quote is null) throw Denied("cota monetaria de invocación desconocida");
        try
        {
            if (checked(primary.RunUsd.Value + meta.RunUsd.Value + (invocation ? quote!.Value : 0m)) > grant.Limits.MaxSpendUsd)
                throw Denied("límite de gasto insuficiente");
        }
        catch (OverflowException) { throw Denied("consumo no representable"); }
    }

    private static BudgetExceededException Denied(string reason) => new("UltraCode: " + reason);
}
