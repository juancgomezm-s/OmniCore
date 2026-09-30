namespace OmniCore.Context;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Materializa una proyección del estado con procedencia y decisiones de presupuesto.</summary>
public sealed class ContextMaterializer
{
    private readonly ITokenCounter _counter;

    private readonly IReadOnlyList<IContextContributor> _contributors;
    private readonly IArtifactStore? _artifacts;
    private readonly ContextManagementPolicy _policy;

    public ContextMaterializer(ITokenCounter counter, IReadOnlyList<IContextContributor> contributors,
        IArtifactStore? artifacts = null, ContextManagementPolicy? policy = null)
    {
        _counter = counter;
        _contributors = contributors;
        _artifacts = artifacts;
        _policy = policy ?? ContextManagementPolicy.Default;
        if (_policy.ExternalizeAboveCharacters < 0 || _policy.CompressBodyCharacters < 1
            || _policy.RecentTailItems < 0 || _policy.CompactAfterItems < 1
            || _policy.MaxCheckpointCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(policy), "La política de contexto contiene umbrales inválidos.");
    }

    /// <summary>Contributors configurados para que el runtime combine proyecciones vivas.</summary>
    public IReadOnlyList<IContextContributor> Contributors() => _contributors;

    /// <summary>Contador de tokens configurado.</summary>
    public ITokenCounter Counter() => _counter;

    public ContextSnapshot Materialize(MaterializeRequest request, CancellationToken cancellationToken) =>
        MaterializeWithinBudget(request, cancellationToken, 0);

    /// <summary>Cuenta y aplica poda, externalización, compresión y la última barrera de presupuesto.</summary>
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
        var pipelineDiagnostics = new Dictionary<string, ContextDiagnostic>(StringComparer.Ordinal);
        var transformed = new List<ContextItem>(ordered.Count);
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = item;
            if (candidate.Kind == ContextItemKind.ToolResult && _artifacts is not null
                && candidate.Content.Length > _policy.ExternalizeAboveCharacters)
            {
                var safeOutput = new RedactionPolicy().Redact(candidate.Content);
                var artifact = _artifacts.PutText(safeOutput, "text/plain", ArtifactKind.ToolOutput,
                    candidate.Provenance.Sensitive ? Sensitivity.Sensitive : Sensitivity.Normal);
                var preview = safeOutput[..Math.Min(240, safeOutput.Length)];
                var stub = "[tool output externalized; ref=artifact=" + artifact.Hash
                    + "; re-read by CAS hash with IArtifactStore.GetText or ContextArtifactReferenceResolver.Read(ref)]\\nPreview: " + preview;
                var provenance = candidate.Provenance with
                {
                    Refs = (candidate.Provenance.Refs ?? Array.Empty<string>()).Append("artifact=" + artifact.Hash)
                        .ToArray(),
                };
                candidate = new ContextItem(candidate.Id, candidate.Kind, stub, 0, candidate.Priority,
                    candidate.Retention, provenance, candidate.PreserveWhenTrimming);
                pipelineDiagnostics[candidate.Id] = new ContextDiagnostic(candidate.Id, candidate.Provenance,
                    ContextDecision.Externalized, 0);
            }
            transformed.Add(candidate);
        }

        var pruned = ContextCompaction.PruneSupersededFileReads(transformed, out var pruneDiagnostics);
        foreach (var diagnostic in pruneDiagnostics.Where(d => d.Decision == ContextDecision.Pruned))
            pipelineDiagnostics[diagnostic.ItemId] = diagnostic;
        var prunedIds = new HashSet<string>(pruned.Select(i => i.Id), StringComparer.Ordinal);
        var conversational = transformed.Where(IsConversation).ToArray();
        var compressCount = Math.Max(0, conversational.Length - Math.Max(0, _policy.RecentTailItems));
        var compressibleIds = new HashSet<string>(conversational.Take(compressCount)
            .Where(i => !i.PreserveWhenTrimming).Select(i => i.Id), StringComparer.Ordinal);
        for (var i = 0; i < transformed.Count; i++)
        {
            var old = transformed[i];
            if (prunedIds.Contains(old.Id) && compressibleIds.Contains(old.Id)
                && old.Content.Length > _policy.CompressBodyCharacters)
            {
                var compressed = ContextCompaction.Compress(old, _policy.CompressBodyCharacters);
                if (!ReferenceEquals(compressed, old))
                {
                    old = compressed;
                    pipelineDiagnostics[old.Id] = new ContextDiagnostic(old.Id, old.Provenance,
                        ContextDecision.Compressed, 0);
                    transformed[i] = old;
                }
            }
        }

        var counted = new List<ContextItem>();
        foreach (var item in OrderItems(pruned))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = transformed.FirstOrDefault(i => i.Id == item.Id) ?? item;
            var count = _counter.CountAsync(current, cancellationToken).GetAwaiter().GetResult();
            counted.Add(WithTokens(current, count));
        }

        var budgetResult = maxTokens > 0 ? ApplyBudget(counted, maxTokens, cancellationToken) : null;
        var finalItems = budgetResult is null ? counted : budgetResult.Items;
        var budgetDiagnostics = budgetResult is null ? IncludedDiagnostics(counted) : budgetResult.Diagnostics;
        var diagnosticsById = budgetDiagnostics.ToDictionary(d => d.ItemId, StringComparer.Ordinal);
        foreach (var diagnostic in request.PriorDiagnostics)
            diagnosticsById[diagnostic.ItemId] = diagnostic;
        foreach (var pair in pipelineDiagnostics)
        {
            if (diagnosticsById.TryGetValue(pair.Key, out var existing)
                && existing.Decision == ContextDecision.Included)
                diagnosticsById[pair.Key] = pair.Value with { Tokens = existing.Tokens };
            else if (!diagnosticsById.ContainsKey(pair.Key)) diagnosticsById[pair.Key] = pair.Value;
        }
        var diagnostics = diagnosticsById.Values.ToArray();
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
    public IReadOnlyList<ContextDiagnostic> PriorDiagnostics { get; }

    public MaterializeRequest(SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId, TurnId? turnId,
        long basedOnEventSequence, ExecutionFingerprint fingerprint,
        IReadOnlyList<ContextDiagnostic>? priorDiagnostics = null)
    {
        SessionId = sessionId;
        RunId = runId;
        TaskId = taskId;
        LaneId = laneId;
        TurnId = turnId;
        BasedOnEventSequence = basedOnEventSequence;
        Fingerprint = fingerprint;
        PriorDiagnostics = priorDiagnostics ?? Array.Empty<ContextDiagnostic>();
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

/// <summary>Regenera en cada materialización la proyección de un checkpoint persistido.</summary>
public sealed class ContextCheckpointContributor : IContextContributor
{
    private readonly ContextItem _item;
    public ContextCheckpointContributor(string checkpointId, long throughSequence, string summary, ArtifactRef artifact)
    {
        var provenance = new ContextProvenance("core.context-checkpoint", ContributionCategory.Checkpoint,
            "engine", ScopeLevel.Run, false, new[] { "checkpoint=" + checkpointId, "artifact=" + artifact.Hash });
        _item = new ContextItem("checkpoint-" + checkpointId, ContextItemKind.Summary,
            "Context checkpoint through event " + throughSequence + ":\\n" + summary, 0,
            ContextPriority.High, RetentionPolicy.KeepForever, provenance);
    }

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(new[] { _item });
    }
}

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
