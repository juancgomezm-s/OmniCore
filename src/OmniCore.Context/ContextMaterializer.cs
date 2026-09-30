namespace OmniCore.Context;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Materializa una proyección del estado con procedencia y decisiones de presupuesto.</summary>
public sealed class ContextMaterializer
{
    private readonly ITokenCounter _counter;

    private readonly IReadOnlyList<IContextContributor> _contributors;

    public ContextMaterializer(ITokenCounter counter, IReadOnlyList<IContextContributor> contributors)
    {
        _counter = counter;
        _contributors = contributors;
    }

    /// <summary>Contributors configurados para que el runtime combine proyecciones vivas.</summary>
    public IReadOnlyList<IContextContributor> Contributors() => _contributors;

    /// <summary>Contador de tokens configurado.</summary>
    public ITokenCounter Counter() => _counter;

    public ContextSnapshot Materialize(MaterializeRequest request, CancellationToken cancellationToken) =>
        MaterializeWithinBudget(request, cancellationToken, 0);

    /// <summary>Cuenta los items y aplica el recorte provisional de ADR-0042.</summary>
    public ContextSnapshot MaterializeWithinBudget(MaterializeRequest request,
        CancellationToken cancellationToken, int maxTokens)
    {
        var items = new List<ContextItem>();
        foreach (var contributor in _contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contributed = contributor.GetContextAsync(request, cancellationToken).GetAwaiter().GetResult();
            foreach (var item in contributed)
            {
                items.Add(item);
            }
        }

        var ordered = OrderItems(items);
        var counted = new List<ContextItem>();
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = _counter.CountAsync(item, cancellationToken).GetAwaiter().GetResult();
            counted.Add(WithTokens(item, count));
        }

        var budgetResult = maxTokens > 0 ? ApplyBudget(counted, maxTokens, cancellationToken) : null;
        var finalItems = budgetResult is null ? counted : budgetResult.Items;
        var diagnostics = budgetResult is null
            ? IncludedDiagnostics(counted)
            : budgetResult.Diagnostics;
        var tokenCount = 0;
        foreach (var item in finalItems)
        {
            tokenCount += item.EstimatedTokens;
        }

        return new ContextSnapshot(Guid.NewGuid(), request.SessionId, request.RunId, request.TaskId, request.LaneId,
            request.TurnId, request.BasedOnEventSequence, request.Fingerprint, finalItems, tokenCount,
            budgetResult?.Overflowed ?? false, diagnostics);
    }

    private sealed class BudgetResult
    {
        public IReadOnlyList<ContextItem> Items { get; }

        public IReadOnlyList<ContextDiagnostic> Diagnostics { get; }

        public bool Overflowed { get; }

        public BudgetResult(IReadOnlyList<ContextItem> items, IReadOnlyList<ContextDiagnostic> diagnostics,
            bool overflowed)
        {
            Items = items;
            Diagnostics = diagnostics;
            Overflowed = overflowed;
        }
    }

    private static BudgetResult ApplyBudget(IReadOnlyList<ContextItem> items, int maxTokens,
        CancellationToken cancellationToken)
    {
        var kept = new bool[items.Count];
        var effective = new ContextItem[items.Count];
        var decisions = new ContextDecision[items.Count];
        var total = 0;
        var protectedTotal = 0;
        for (var i = 0; i < items.Count; i++)
        {
            kept[i] = true;
            effective[i] = items[i];
            decisions[i] = ContextDecision.Included;
            total += items[i].EstimatedTokens;
            if (IsProtected(items[i]))
            {
                protectedTotal += items[i].EstimatedTokens;
            }
        }

        var overflowed = protectedTotal > maxTokens;
        if (overflowed)
        {
            // No se trunca ni se omite System, Task, WorkingState, pinned o el primer input.
            for (var i = 0; i < items.Count; i++)
            {
                if (!IsProtected(items[i]))
                {
                    kept[i] = false;
                    decisions[i] = ContextDecision.OmittedByBudget;
                }
            }
        }
        else
        {
            // ADR-0042: primero se descarta la conversación más antigua, salvo el primer input.
            for (var i = 0; i < items.Count && total > maxTokens; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsConversation(items[i]) && !items[i].PreserveWhenTrimming)
                {
                    kept[i] = false;
                    decisions[i] = ContextDecision.OmittedByBudget;
                    total -= items[i].EstimatedTokens;
                }
            }

            // Después se liberan contribuciones regenerables o de prioridad baja.
            for (var i = 0; i < items.Count && total > maxTokens; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kept[i] && !IsProtected(items[i]) && !IsConversation(items[i])
                    && (items[i].Priority == ContextPriority.Low
                        || items[i].Retention == RetentionPolicy.RegenerateEachTurn))
                {
                    kept[i] = false;
                    decisions[i] = ContextDecision.OmittedByBudget;
                    total -= items[i].EstimatedTokens;
                }
            }

            // Último recurso: truncar otros items no protegidos. El orden visible se conserva.
            for (var i = 0; i < items.Count && total > maxTokens; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!kept[i] || IsProtected(items[i]))
                {
                    continue;
                }

                var available = items[i].EstimatedTokens - (total - maxTokens);
                if (available <= 0)
                {
                    kept[i] = false;
                    decisions[i] = ContextDecision.OmittedByBudget;
                    total -= items[i].EstimatedTokens;
                    continue;
                }

                total -= items[i].EstimatedTokens;
                effective[i] = TruncateTo(items[i], available)!;
                decisions[i] = ContextDecision.TruncatedByBudget;
                total += effective[i].EstimatedTokens;
            }

            if (total > maxTokens)
            {
                overflowed = true;
            }
        }

        var final = new List<ContextItem>();
        var diagnostics = new List<ContextDiagnostic>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = effective[i];
            if (kept[i])
            {
                final.Add(item);
            }

            diagnostics.Add(new ContextDiagnostic(items[i].Id, items[i].Provenance, decisions[i],
                kept[i] ? item.EstimatedTokens : items[i].EstimatedTokens));
        }

        return new BudgetResult(final.ToArray(), diagnostics.ToArray(), overflowed);
    }

    private static bool IsProtected(ContextItem item) => item.Kind == ContextItemKind.WorkingState
        || item.Kind == ContextItemKind.System || item.Kind == ContextItemKind.Task
        || item.Priority == ContextPriority.Pinned || item.PreserveWhenTrimming;

    private static bool IsConversation(ContextItem item) => item.Kind == ContextItemKind.UserMessage
        || item.Kind == ContextItemKind.AssistantMessage || item.Kind == ContextItemKind.ToolResult;

    private static IReadOnlyList<ContextDiagnostic> IncludedDiagnostics(IReadOnlyList<ContextItem> items)
    {
        var result = new List<ContextDiagnostic>();
        foreach (var item in items)
        {
            result.Add(new ContextDiagnostic(item.Id, item.Provenance, ContextDecision.Included,
                item.EstimatedTokens));
        }

        return result.ToArray();
    }

    private static ContextItem? TruncateTo(ContextItem item, int tokens)
    {
        if (tokens <= 0)
        {
            return null;
        }

        var chars = Math.Max(1, tokens * 4);
        var content = item.Content;
        if (content.Length > chars)
        {
            content = content.Substring(0, chars) + "…[truncado]";
        }

        return new ContextItem(item.Id, item.Kind, content, tokens, item.Priority, item.Retention,
            item.Provenance, item.PreserveWhenTrimming);
    }

    private static ContextItem WithTokens(ContextItem item, int tokens) =>
        new ContextItem(item.Id, item.Kind, item.Content, tokens, item.Priority, item.Retention,
            item.Provenance, item.PreserveWhenTrimming);

    private static IReadOnlyList<ContextItem> OrderItems(IReadOnlyList<ContextItem> items)
    {
        var result = new List<ContextItem>();
        AddKind(result, items, ContextItemKind.System);
        AddKind(result, items, ContextItemKind.Task);
        AddKind(result, items, ContextItemKind.Summary);
        foreach (var item in items)
        {
            if (!IsWorkingState(item) && item.Kind != ContextItemKind.System && item.Kind != ContextItemKind.Task
                && item.Kind != ContextItemKind.Summary)
            {
                result.Add(item);
            }
        }

        // WorkingState se coloca después de todo el contexto, inmediatamente antes del turno actual.
        AddKind(result, items, ContextItemKind.WorkingState);
        return result.ToArray();
    }

    private static void AddKind(List<ContextItem> result, IReadOnlyList<ContextItem> items, ContextItemKind kind)
    {
        foreach (var item in items)
        {
            if (item.Kind == kind)
            {
                result.Add(item);
            }
        }
    }

    private static bool IsWorkingState(ContextItem item) => item.Kind == ContextItemKind.WorkingState;
}

/// <summary>Solicitud de materialización del contexto de un Turn.</summary>
public sealed class MaterializeRequest
{
    public SessionId SessionId { get; }
    public RunId RunId { get; }
    public TaskId? TaskId { get; }
    public LaneId? LaneId { get; }
    public TurnId? TurnId { get; }
    public long BasedOnEventSequence { get; }
    public ExecutionFingerprint Fingerprint { get; }

    public MaterializeRequest(SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId, TurnId? turnId,
        long basedOnEventSequence, ExecutionFingerprint fingerprint)
    {
        SessionId = sessionId;
        RunId = runId;
        TaskId = taskId;
        LaneId = laneId;
        TurnId = turnId;
        BasedOnEventSequence = basedOnEventSequence;
        Fingerprint = fingerprint;
    }
}

/// <summary>Contributor de contexto; cada item debe incluir procedencia (ADR-0029).</summary>
public interface IContextContributor
{
    Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Contribuye la conversación materializada del Run (ADR-0042).</summary>
public sealed class SessionConversationContributor : IContextContributor
{
    private readonly IReadOnlyList<ConversationContextEntry> _entries;

    public SessionConversationContributor(IReadOnlyList<ConversationContextEntry> entries) => _entries = entries;

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        var result = new List<ContextItem>();
        var provenance = new ContextProvenance("session-conversation", ContributionCategory.Conversation,
            "engine", ScopeLevel.Session, false);
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new ContextItem(entry.Id, entry.Kind, entry.Content, 0, ContextPriority.Normal,
                RetentionPolicy.ConversationWindow, provenance, entry.PreserveWhenTrimming));
        }

        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(result.ToArray());
    }
}

/// <summary>Entrada estable para contribuir y seleccionar un mensaje conversacional.</summary>
public sealed record ConversationContextEntry(string Id, ContextItemKind Kind, string Content,
    bool PreserveWhenTrimming = false);

/// <summary>Contribuye las instrucciones del system prompt para incluirlas en el presupuesto.</summary>
public sealed class SystemPromptContributor : IContextContributor
{
    private readonly string _prompt;

    public SystemPromptContributor(string prompt) => _prompt = prompt;

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = new ContextProvenance("core.system", ContributionCategory.System,
            "engine", ScopeLevel.Run, false);
        var item = new ContextItem("system-prompt", ContextItemKind.System, _prompt, 0,
            ContextPriority.Pinned, RetentionPolicy.KeepForever, provenance);
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([item]);
    }
}

/// <summary>Contribuye la proyección WorkingState renderizada (ADR-0016 §7).</summary>
public sealed class WorkingStateContributor : IContextContributor
{
    private readonly string _rendered;

    public WorkingStateContributor(string rendered) => _rendered = rendered;

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provenance = new ContextProvenance("core.working-state", ContributionCategory.WorkingState,
            "engine", ScopeLevel.Run, false);
        var item = new ContextItem("working-state", ContextItemKind.WorkingState, _rendered, 0,
            ContextPriority.Pinned, RetentionPolicy.RegenerateEachTurn, provenance);
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([item]);
    }
}
