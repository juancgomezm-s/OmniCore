namespace OmniCore.Context;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Context Engine v1 (M2): materializa el contexto de un Turn combinando contribuidores
/// (WorkingState, conversación, task) y produce un ContextSnapshot con fingerprint.
/// La política provisional (ADR-0042 §2) nunca recorta el WorkingState, que va al final
/// del contexto para no invalidar el prefijo cacheado (ADR-0011 §9).
/// </summary>
public sealed class ContextMaterializer
{
    private readonly ITokenCounter _counter;

    private readonly IReadOnlyList<IContextContributor> _contributors;

    public ContextMaterializer(ITokenCounter counter, IReadOnlyList<IContextContributor> contributors)
    {
        _counter = counter;
        _contributors = contributors;
    }

    /// <summary>Contributors configurados (para que un runtime combine el WorkingState en vivo).</summary>
    public IReadOnlyList<IContextContributor> Contributors() => _contributors;

    /// <summary>El contador de tokens configurado.</summary>
    public ITokenCounter Counter() => _counter;

    public ContextSnapshot Materialize(MaterializeRequest request, CancellationToken cancellationToken)
    {
        return MaterializeWithinBudget(request, cancellationToken, 0);
    }

    /// <summary>
    /// Materializa aplicando la política de overflow del contexto (ADR-0042 §3): si el total
    /// excede `maxTokens`, se recortan primero los items volátiles de menor prioridad
    /// (RegenerateEachTurn → ConversationWindow → KeepForever) y al final se trunca el
    /// contenido del item menos crítico. El WorkingState (pinned) nunca se recorta hasta el
    /// último extremo, y en ese caso se trunca su texto  — jamás se descarta entero si es la
    /// única fuente del turno.
    /// </summary>
    public ContextSnapshot MaterializeWithinBudget(MaterializeRequest request,
        CancellationToken cancellationToken, int maxTokens)
    {
        var items = new List<ContextItem>();
        foreach (var contributor in _contributors)
        {
            var contributed = contributor.GetContextAsync(request, cancellationToken).GetAwaiter().GetResult();
            foreach (var item in contributed)
            {
                items.Add(item);
            }
        }

        var ordered = OrderItems(items);
        var budgeted = maxTokens > 0 ? ApplyBudget(ordered, maxTokens, cancellationToken) : ordered;
        var total = 0;
        var counted = new List<ContextItem>();
        foreach (var item in budgeted)
        {
            var count = _counter.CountAsync(item, cancellationToken).GetAwaiter().GetResult();
            counted.Add(WithTokens(item, count));
            total += count;
        }

        return new ContextSnapshot(Guid.NewGuid(), request.SessionId, request.RunId, request.TaskId, request.LaneId,
            request.TurnId, request.BasedOnEventSequence, request.Fingerprint, counted, total);
    }

    /// <summary>Política de overflow: suelta items de menor prioridad/retention y trunca.
    /// El WorkingState y System (pinned) se agregan UNA sola vez; el resto se ordena por
    /// prioridad y se suelta/trunca al superar el presupuesto.</summary>
    private static IReadOnlyList<ContextItem> ApplyBudget(IReadOnlyList<ContextItem> items, int maxTokens,
        CancellationToken cancellationToken)
    {
        var kept = new List<ContextItem>();
        var total = 0;

        // 1. Pinned sin límite primero (WorkingState y System); se marcan para no re-agregarlos.
        var excluded = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == ContextItemKind.WorkingState || item.Kind == ContextItemKind.System)
            {
                kept.Add(item);
                total += item.EstimatedTokens;
                excluded.Add(item.Id);
            }
        }

        // 2. El resto por prioridad desc: High > Normal > Low (volátiles primero a soltar).
        var rest = SortByPriority(items);
        foreach (var item in rest)
        {
            if (IsExcluded(item, excluded))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var next = total + item.EstimatedTokens;
            if (next <= maxTokens)
            {
                kept.Add(item);
                total = next;
                continue;
            }

            if (item.Retention == RetentionPolicy.KeepForever || item.Priority == ContextPriority.High)
            {
                // crítico: se trunca el contenido del item en lugar de soltarlo.
                var truncated = TruncateTo(item, Math.Max(0, maxTokens - total));
                if (truncated is not null)
                {
                    kept.Add(truncated!);
                }

                break;
            }

            // volátil: se suelta (RegenerateEachTurn vuelve a regenerarse el próximo turno).
            if (item.Kind == ContextItemKind.WorkingState)
            {
                kept.Add(TruncateTo(item, Math.Max(0, maxTokens - total))!);
                break;
            }
        }

        return kept.ToArray();
    }

    private static IReadOnlyList<ContextItem> SortByPriority(IReadOnlyList<ContextItem> items)
    {
        var result = new List<ContextItem>();
        result.AddRange(items);
        for (var i = 0; i < result.Count; i++)
        {
            for (var j = i + 1; j < result.Count; j++)
            {
                if (Priority(result[j]) > Priority(result[i]))
                {
                    var tmp = result[i];
                    result[i] = result[j];
                    result[j] = tmp;
                }
            }
        }

        return result.ToArray();
    }

    private static int Priority(ContextItem item) =>
        item.Priority == ContextPriority.High ? 3
            : item.Priority == ContextPriority.Normal ? 2
                : item.Priority == ContextPriority.Pinned ? 4 : 1;

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

        return new ContextItem(item.Id, item.Kind, content, item.EstimatedTokens, item.Priority, item.Retention,
            item.Provenance);
    }

    /// <summary>Copia el item con un valor de tokens (inmutabilidad del record).</summary>
    private static ContextItem WithTokens(ContextItem item, int tokens) =>
        new ContextItem(item.Id, item.Kind, item.Content, tokens, item.Priority, item.Retention, item.Provenance);

    private static IReadOnlyList<ContextItem> OrderItems(IReadOnlyList<ContextItem> items)
    {
        // system → task → skills → conversación → workingState (el más volátil al final).
        var result = new List<ContextItem>();
        result.AddRange(Pick(items, ContextItemKind.System));
        result.AddRange(Pick(items, ContextItemKind.Task));
        result.AddRange(Pick(items, ContextItemKind.Summary));
        result.AddRange(Pick(items, ContextItemKind.UserMessage));
        result.AddRange(Pick(items, ContextItemKind.AssistantMessage));
        result.AddRange(Pick(items, ContextItemKind.ToolResult));
        foreach (var item in items)
        {
            if (IsWorkingState(item) || AlreadyIn(result, item))
            {
                continue;
            }

            result.Add(item);
        }

        // El WorkingState va al final (justo antes del turno actual).
        foreach (var item in items)
        {
            if (IsWorkingState(item))
            {
                result.Add(item);
            }
        }

        return result.ToArray();
    }

    private static bool IsWorkingState(ContextItem item) => item.Kind == ContextItemKind.WorkingState;

    private static bool IsExcluded(ContextItem item, List<string> excluded)
    {
        foreach (var id in excluded)
        {
            if (id.Equals(item.Id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AlreadyIn(List<ContextItem> result, ContextItem item)
    {
        foreach (var existing in result)
        {
            if (ReferenceEquals(existing, item))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ContextItem> Pick(IReadOnlyList<ContextItem> items, ContextItemKind kind)
    {
        var result = new List<ContextItem>();
        foreach (var item in items)
        {
            if (item.Kind == kind)
            {
                result.Add(item);
            }
        }

        return result;
    }
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

/// <summary>Contribuidor de contexto (spec §25, ADR-0028). Cada item lleva procedencia.</summary>
public interface IContextContributor
{
    /// <summary>Aporta ContextItems para la solicitud determinista.</summary>
    Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Contribuye la proyección WorkingState renderizada (ADR-0016 §7).</summary>
public sealed class WorkingStateContributor : IContextContributor
{
    private readonly string _rendered;

    public WorkingStateContributor(string rendered) => _rendered = rendered;

    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        var prov = new ContextProvenance("working-state", ContributionCategory.WorkingState, "engine",
            ScopeLevel.Run, false);
        var item = new ContextItem("working-state", ContextItemKind.WorkingState, _rendered, 0,
            ContextPriority.Pinned, RetentionPolicy.RegenerateEachTurn, prov);
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([item]);
    }
}