using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Host;

/// <summary>Child ceilings supplement, never replace, Run/session/routing guards.
/// Unknown usage or an unbounded invocation is not permission to spend.</summary>
internal static class DelegationBudgetGuard
{
    internal static void Validate(IReadOnlyList<DomainEvent> journal, IEventCodecRegistry codecs,
        IArtifactStore artifacts, IEventStore store, SqliteSpendReservationStore? reservations,
        SessionId session, RunId run, LaneId lane, TurnId turn,
        bool newTurn, bool invocation, ToolCallId? tool, decimal? maximumCost, long? maximumTokens)
    {
        var task = journal.Where(e => e.RunId == run).Select(codecs.Decode).OfType<LaneCreated>()
            .FirstOrDefault(e => e.LaneId == lane)?.TaskId;
        var definition = journal.Select(codecs.Decode).OfType<TaskCreated>().FirstOrDefault(e => e.TaskId == task);
        if (definition?.ParentTaskId is null) return;
        var executionId = ExecutionScope.Current?.ExecutionId;
        // Frozen/legacy Task+Lane context consumers are not operational dispatch. The dispatcher
        // atomically installs a supervision handshake before any real managed child boundary.
        var bindings = journal.Select(codecs.Decode).OfType<SupervisionBindingCreated>()
            .Where(e => e.ExecutionId == executionId).ToArray();
        if (bindings.Length == 0) return;
        var records = PreM6RecordProjection.Replay(session, codecs, journal);
        if (bindings.Length != 1 || records.Records["binding:" + bindings[0].Binding.BindingId].Phase != PreM6RecordPhase.Accepted)
            throw new InvalidOperationException("Child supervision handshake unavailable.");
        var supervisor = bindings[0].Binding.SupervisorExecutionId;
        if (journal.Select(codecs.Decode).Any(e => e is AgentExecutionCompleted c && c.ExecutionId == supervisor
            || e is AgentExecutionFailed f && f.ExecutionId == supervisor))
            throw new InvalidOperationException("Supervisor terminal; managed child cannot continue unsupervised.");
        var authority = RunProjection.Replay(session, run, codecs, journal).ModeAuthority;
        if ((invocation || newTurn || tool is not null) && (authority?.Mode != RunMode.Orchestrate
            || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)))
            throw new InvalidOperationException("Child coordination authorization revoked or expired.");
        var budget = definition.Budget;
        if (budget.MaxTurns is not > 0 || budget.MaxToolCalls is not > 0 || budget.MaxTokens is not > 0 || budget.MaxCostUsd is not >= 0)
            throw new InvalidOperationException("Child requires finite budgets.");
        var own = journal.Where(e => e.RunId == run && e.LaneId == lane).ToArray();
        var facts = own.Select(codecs.Decode).ToArray();
        var turns = facts.OfType<TurnStarted>().Count();
        if (turns + (newTurn && !facts.OfType<TurnStarted>().Any(t => t.TurnId == turn) ? 1 : 0) > budget.MaxTurns)
            throw new InvalidOperationException("Child Turn budget exhausted.");
        var tools = facts.OfType<ToolCallRequested>().Count();
        if (tools + (tool is not null && !facts.OfType<ToolCallRequested>().Any(t => t.ToolCallId == tool) ? 1 : 0) > budget.MaxToolCalls)
            throw new InvalidOperationException("Child tool budget exhausted.");
        var pool = RunBudgetPool.For(store);
        var tokens = RunTokenBudgetReader.Read(own, codecs, run, budget.MaxTokens,
            (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
        if (tokens.Remaining is not { } remaining || remaining < 0 || invocation && (maximumTokens is null || maximumTokens > remaining))
            throw new InvalidOperationException("Child token budget insufficient or unknown.");
        var reader = new CanonicalSpendReader(codecs, artifacts);
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var primary = reader.ReadPrimary(own, session, run, day,
            (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
        var meta = reader.ReadMeta(own, session, run, day);
        if (primary.Incomplete || meta.Incomplete || primary.RunUsd is null || meta.RunUsd is null)
            throw new InvalidOperationException("Child spend accounting unknown.");
        if (invocation && maximumCost is null || primary.RunUsd + meta.RunUsd + (invocation ? maximumCost : 0m) > budget.MaxCostUsd)
            throw new InvalidOperationException("Child cost budget insufficient.");
    }
}
