namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Episodio de estancamiento del item actual de un Run (ADR-0048 §1).</summary>
public sealed record StallObservation(RunId RunId, PlanItemId PlanItemId, string ItemDescription,
    int TurnsWithoutProgress, DateTimeOffset LastProgressAt, IReadOnlySet<LaneId> Lanes,
    int Step, StallPolicy? LastPolicy);

/// <summary>Directiva vigente para el contexto de una Lane vigilada (ADR-0048 §3).</summary>
public sealed record StallDirective(PlanItemId PlanItemId, string ItemDescription, StallPolicy Policy,
    int TurnsWithoutProgress)
{
    /// <summary>
    /// Texto para el modelo (en inglés, ADR-0040). Sin <c>plan.propose</c> en la Lane, la directiva
    /// pide informar del bloqueo en lugar de proponer mutaciones que no puede enviar.
    /// </summary>
    public string Render(bool canProposePlan)
    {
        var header = "Runtime notice (stall policy " + Policy + "): no progress on the current plan item \""
            + ItemDescription + "\" for " + TurnsWithoutProgress + " turns. Do not repeat the previous approach.";
        var action = Policy switch
        {
            StallPolicy.Replan => canProposePlan
                ? "Before doing more work, propose a revision of the plan with plan.propose (Revise, Split, Block or Skip) that addresses why the current approach is not advancing."
                : "Before doing more work, state why the current approach is not advancing and which different approach you will take.",
            StallPolicy.Diagnose => canProposePlan
                ? "Diagnose the cause first: re-read the latest errors and tool results, identify the concrete blocker and state it. If the item cannot advance, propose Block for it with the reason via plan.propose."
                : "Diagnose the cause first: re-read the latest errors and tool results, identify the concrete blocker and report it explicitly.",
            StallPolicy.SplitTask => canProposePlan
                ? "Split the current item into smaller, verifiable steps with plan.propose (Split) and work only on the first one."
                : "Break the current work into smaller, verifiable steps, report them, and complete only the first one.",
            _ => throw new InvalidOperationException("Only Replan, Diagnose and SplitTask produce a directive."),
        };
        return header + "\n" + action;
    }
}

/// <summary>
/// Disponibilidad de las respuestas que dependen del Host (ADR-0048 §2). Un motivo no nulo
/// significa "no disponible" y se registra como código estable en el evento.
/// </summary>
public sealed record StallAvailability(string? EscalateModelUnavailable, string? AskUserUnavailable)
{
    public static StallAvailability DirectivesOnly(string reason) => new(reason, reason);
}

/// <summary>Respuesta elegida; <c>Policy</c> nulo cuando ninguna está disponible.</summary>
public sealed record StallResponseDecision(StallPolicy? Policy, IReadOnlyList<string> Skipped);

/// <summary>
/// Decodificación memoizada por <c>EventId</c> para evaluar el watchdog en cada Turn sin volver a
/// deserializar el journal entero: los eventos son inmutables y append-only (ADR-0001), así que el
/// payload decodificado de un id no cambia. Solo para lectores que no mutan los payloads.
/// </summary>
public sealed class MemoizedEventDecoder : IEventCodecRegistry
{
    private readonly IEventCodecRegistry _inner;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EventId, DomainEventPayload> _decoded = new();

    public MemoizedEventDecoder(IEventCodecRegistry inner) => _inner = inner;

    public IDomainEventCodec CodecFor(EventType type) => _inner.CodecFor(type);

    public int CurrentVersion(EventType type) => _inner.CurrentVersion(type);

    public DomainEventPayload Decode(DomainEvent evt) =>
        _decoded.TryGetValue(evt.EventId, out var payload) ? payload : _decoded.GetOrAdd(evt.EventId, _inner.Decode(evt));
}

/// <summary>
/// Watchdog sobre el journal de un Run (ADR-0016 §9, ADR-0036 §7, ADR-0048 §1). Funciones puras:
/// solo leen eventos; quien llama decide si escribe <c>ProgressStalled</c>.
/// </summary>
public static class StallWatch
{
    /// <summary>
    /// Eventos del Run: desde su <c>RunCreated</c> hasta el <c>RunCreated</c> siguiente. Los Runs de
    /// una sesión son secuenciales (INV-026), como en la proyección de WorkingState del servidor.
    /// </summary>
    public static IReadOnlyList<DomainEvent> RunEvents(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events,
        RunId run)
    {
        var own = new List<DomainEvent>();
        var inRun = false;
        foreach (var evt in events)
        {
            if (evt.Type.ToString() == "run.created" && codecs.Decode(evt) is RunCreated created)
                inRun = created.RunId == run;
            if (inRun) own.Add(evt);
        }

        return own;
    }

    /// <summary>
    /// Devuelve el episodio si el item actual lleva <paramref name="thresholdTurns"/> Turns en
    /// InProgress sin señal de progreso ni un <c>ProgressStalled</c> previo del mismo episodio.
    /// </summary>
    public static StallObservation? Observe(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events,
        RunId run, int thresholdTurns)
    {
        var watchdog = new ProgressWatchdog(thresholdTurns);
        var own = RunEvents(codecs, events, run);
        // Atajo por envelope: sin el umbral de Turns en el Run no puede haber episodio.
        if (!watchdog.IsStalled(own.Count(evt => evt.Type.ToString() == "turn.started"))) return null;
        if (Watched(codecs, own, run) is not { } watched) return null;
        var scan = ProgressWatchdog.Scan(codecs, own, watched.Lanes, watched.Item.Id);
        if (!watchdog.IsStalled(scan.EpisodeTurns)) return null;
        return new StallObservation(run, watched.Item.Id, watched.Item.Description, scan.TurnsSinceSignal,
            scan.LastSignalAt ?? own[0].Timestamp, watched.Lanes, scan.Step, scan.LastResponse?.Policy);
    }

    /// <summary>
    /// Directiva de Replan/Diagnose/SplitTask vigente para <paramref name="lane"/>: la última
    /// respuesta del item actual, mientras no haya otra señal de progreso (ADR-0048 §3).
    /// </summary>
    public static StallDirective? ActiveDirective(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events,
        RunId run, LaneId lane)
    {
        var own = RunEvents(codecs, events, run);
        // Atajo por envelope: sin respuestas seleccionadas en el Run no hay directiva.
        if (!own.Any(evt => evt.Type.ToString() == "stall.response_selected")) return null;
        if (Watched(codecs, own, run) is not { } watched || !watched.Lanes.Contains(lane)) return null;
        var scan = ProgressWatchdog.Scan(codecs, own, watched.Lanes, watched.Item.Id);
        return scan.LastResponse is { Policy: StallPolicy.Replan or StallPolicy.Diagnose or StallPolicy.SplitTask } response
            ? new StallDirective(watched.Item.Id, watched.Item.Description, response.Policy, scan.TurnsSinceSignal)
            : null;
    }

    private static (PlanItem Item, IReadOnlySet<LaneId> Lanes)? Watched(IEventCodecRegistry codecs,
        IReadOnlyList<DomainEvent> own, RunId run)
    {
        if (own.Count == 0) return null;
        var plan = PlanProjection.Replay(codecs, own);
        var current = new ProgressReconciler().CurrentItem(plan);
        var item = current is null ? null : plan.Item(current);
        if (item?.State != PlanItemState.InProgress)
        {
            // ADR-0048 §1: un Plan sin descomponer (solo el item raíz, sin vínculos) representa el
            // objetivo del Run en curso; se vigila aunque nadie lo haya marcado InProgress.
            var items = plan.Items();
            item = items.Count == 1 && items[0] is { ParentId: null, LinkedTasks.Count: 0 } objective
                && objective.State is PlanItemState.Pending or PlanItemState.Ready ? objective : null;
        }
        if (item is null) return null;

        // ADR-0036 §7: Lanes vinculadas al item o, sin vínculos, la Lane raíz.
        var lanes = LaneProjection.Replay(codecs, own);
        var watched = item.LinkedTasks.SelectMany(link => lanes.ForTask(link.TaskId)).Select(lane => lane.Id).ToHashSet();
        if (watched.Count == 0 && RunProjection.Replay(own[0].SessionId, run, codecs, own).RootTask is { } root)
            watched = lanes.ForTask(root).Select(lane => lane.Id).ToHashSet();
        return (item, watched);
    }
}

/// <summary>
/// Política determinista de respuesta al estancamiento (ADR-0048 §2). El modelo nunca la elige.
/// </summary>
public static class StallPolicyEvaluator
{
    /// <summary>Cadena por defecto de ADR-0016 §9.</summary>
    public static readonly IReadOnlyList<StallPolicy> DefaultChain =
    [
        StallPolicy.Replan, StallPolicy.Diagnose, StallPolicy.EscalateModel, StallPolicy.SplitTask, StallPolicy.AskUser,
    ];

    /// <summary>
    /// Elige la respuesta siguiente a <paramref name="lastPolicy"/> en la cadena, saltando las no
    /// disponibles. Agotada la cadena se repite <c>AskUser</c>: el usuario acota el bucle.
    /// </summary>
    public static StallResponseDecision Select(StallPolicy? lastPolicy, StallAvailability availability)
    {
        var skipped = new List<string>();
        var start = lastPolicy is { } last ? IndexOf(last) + 1 : 0;
        for (var index = start; index < DefaultChain.Count; index++)
        {
            var policy = DefaultChain[index];
            if (Unavailable(policy, availability) is { } reason)
            {
                skipped.Add(policy + ":" + reason);
                continue;
            }

            return new StallResponseDecision(policy, skipped);
        }

        if (availability.AskUserUnavailable is null) return new StallResponseDecision(StallPolicy.AskUser, skipped);
        if (start >= DefaultChain.Count) skipped.Add(StallPolicy.AskUser + ":" + availability.AskUserUnavailable);
        return new StallResponseDecision(null, skipped);
    }

    private static int IndexOf(StallPolicy policy)
    {
        for (var index = 0; index < DefaultChain.Count; index++)
            if (DefaultChain[index] == policy) return index;
        throw new ArgumentOutOfRangeException(nameof(policy), policy, "policy outside the stall chain");
    }

    private static string? Unavailable(StallPolicy policy, StallAvailability availability) => policy switch
    {
        StallPolicy.EscalateModel => availability.EscalateModelUnavailable,
        StallPolicy.AskUser => availability.AskUserUnavailable,
        _ => null,
    };
}
