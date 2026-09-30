namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resultado de un Completion Gate: acepta o rechaza con los faltantes.</summary>
public sealed class GateResult
{
    public bool Passed { get; }

    public IReadOnlyList<string> Missing { get; }

    public GateResult(bool passed, IReadOnlyList<string> missing)
    {
        Passed = passed;
        Missing = missing;
    }

    public static GateResult Ok() => new(true, new string[0]);

    public static GateResult Fail(string what) => new(false, [what]);
}

/// <summary>
/// PlanCompletionGate (ADR-0016 §10): rechaza la finalización si un item Required está en
/// Pending/Ready/InProgress/Blocked. Un Failed requerido → nunca Completed limpio (el Run
/// decide Failed vs CompletedWithIssues según la FailurePolicy). Solo cuentan las hojas: un item
/// con hijos es un contenedor cuyo estado se deriva de ellos (ADR-0036 §4).
/// </summary>
public sealed class PlanCompletionGate
{
    /// <summary>Items requeridos (hojas) que terminaron en Failed.</summary>
    public IReadOnlyList<string> FailedRequired(PlanProjection plan)
    {
        var failed = new List<string>();
        foreach (var item in Leaves(plan))
        {
            if (item.Required && item.State == PlanItemState.Failed)
            {
                failed.Add(item.Description);
            }
        }

        return failed;
    }

    /// <summary>Items sin hijos: los únicos que cuentan para el gate y las reglas R1–R7.</summary>
    public static IReadOnlyList<PlanItem> Leaves(PlanProjection plan)
    {
        var items = plan.Items();
        var parents = new HashSet<PlanItemId>();
        foreach (var item in items)
        {
            if (item.ParentId is not null)
            {
                parents.Add(item.ParentId);
            }
        }

        return items.Where(item => !parents.Contains(item.Id)).ToArray();
    }

    public GateResult Check(PlanProjection plan)
    {
        var missing = new List<string>();
        foreach (var item in Leaves(plan))
        {
            if (!item.Required)
            {
                continue;
            }

            if (item.State == PlanItemState.Pending || item.State == PlanItemState.Ready
                || item.State == PlanItemState.InProgress || item.State == PlanItemState.Blocked)
            {
                missing.Add("PlanItem " + item.Id + " (" + item.Description + ") en " + item.State);
            }
        }

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", missing));
    }

    private static string _Flatten(IReadOnlyList<string> missing) => string.Join(", ", missing);
}

/// <summary>
/// PendingTaskGate (ADR-0035 §5): rechaza si alguna Task está Running o no terminal.
/// </summary>
public sealed class PendingTaskGate
{
    public GateResult Check(TaskGraphProjection tasks, PlanProjection plan) => Check(tasks, plan, null);

    /// <summary>
    /// Igual, sin contar la Task raíz del Run: su Lane es la conversación y se cierra al terminar
    /// el Run, después de pasar los gates (ADR-0035 §2).
    /// </summary>
    public GateResult Check(TaskGraphProjection tasks, PlanProjection plan, TaskId? rootTask)
    {
        var missing = new List<string>();
        foreach (var task in tasks.Tasks())
        {
            if (rootTask is not null && task.Id.Equals(rootTask))
            {
                continue;
            }

            if (task.State == TaskState.Running)
            {
                missing.Add("Task " + task.Id + " en ejecución");
            }
            else if (task.State == TaskState.Pending || task.State == TaskState.Ready
                || task.State == TaskState.Blocked)
            {
                missing.Add("Task " + task.Id + " no terminal (" + task.State + ")");
            }
        }

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", ObjectConversions.Strings(missing)));
    }
}

/// <summary>Conversiones utilitarias de colecciones.</summary>
public sealed class ObjectConversions
{
    public static IReadOnlyList<string> Strings(IReadOnlyList<string> source) => source;
}

/// <summary>
/// Watchdog de progreso (ADR-0036 §7, ADR-0016 §9): si el item actual lleva N Turns en InProgress
/// sin ninguna señal de progreso, se emite <c>ProgressStalled</c>. N es el umbral configurado (en M1
/// una constante, 6; desde M2 sale de <c>HarnessPolicy.StallThresholdTurns</c>).
/// <para>Señales de progreso: transición de una Task, Lane o PlanItem; una ToolCall con efecto
/// aplicado; una ToolCall que termina bien sobre un recurso no visto antes (explorar cuenta); un
/// resultado de validación. Solo cuentan los Turns de las Lanes vinculadas al item o, si no tiene
/// vínculos, los de la Lane raíz.</para>
/// </summary>
public sealed class ProgressWatchdog
{
    private readonly int _thresholdTurns;

    public ProgressWatchdog() => _thresholdTurns = ProgressReconciler.DefaultStallThresholdTurns;

    public ProgressWatchdog(int thresholdTurns)
    {
        if (thresholdTurns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdTurns), "el umbral debe ser al menos 1 Turn");
        }

        _thresholdTurns = thresholdTurns;
    }

    public int ThresholdTurns() => _thresholdTurns;

    public bool IsStalled(int turnsInProgressSinceSignal) => turnsInProgressSinceSignal >= _thresholdTurns;

    /// <summary>Turns de las Lanes dadas desde la última señal de progreso (ADR-0036 §7).</summary>
    public static int TurnsWithoutProgress(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events,
        IReadOnlyCollection<LaneId> lanes)
    {
        var turns = 0;
        var effects = new Dictionary<ToolCallId, EffectClass>();
        var resourceOf = new Dictionary<ToolCallId, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            var payload = codecs.Decode(evt);
            switch (payload)
            {
                case TurnStarted started when lanes.Contains(started.LaneId):
                    turns += 1;
                    break;
                case ToolCallRequested requested:
                    resourceOf[requested.ToolCallId] = requested.ToolName + "|" + requested.ArgumentsJson;
                    break;
                case ToolCallStarted started:
                    effects[started.ToolCallId] = started.EffectClass;
                    break;
                case ToolCallSucceeded succeeded:
                    var applied = effects.TryGetValue(succeeded.ToolCallId, out var effect) && effect != EffectClass.None;
                    var fresh = resourceOf.TryGetValue(succeeded.ToolCallId, out var resource) && seen.Add(resource);
                    if (applied || fresh)
                    {
                        turns = 0;
                    }

                    break;
                case ToolCallReconciled { Outcome: ReconciliationOutcome.Applied }:
                case RunValidationRejected or RunValidationStarted:
                    turns = 0;
                    break;
                default:
                    if (IsTransition(payload))
                    {
                        turns = 0;
                    }

                    break;
            }
        }

        return turns;
    }

    private static bool IsTransition(DomainEventPayload payload) => payload is
        TaskReady or TaskStarted or TaskBlocked or TaskUnblocked or TaskCompleted or TaskFailed or TaskSkipped
        or TaskCancelled or LaneStarted or LaneBlocked or LaneUnblocked or LaneCompleted or LaneFailed
        or LaneCancelled or PlanItemReady or PlanItemStarted or PlanItemBlocked or PlanItemUnblocked
        or PlanItemCompleted or PlanItemFailed or PlanItemSkipped or PlanItemCancelled or PlanItemReopened;
}
