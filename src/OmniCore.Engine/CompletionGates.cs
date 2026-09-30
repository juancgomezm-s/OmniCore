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

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", missing));
    }
}

/// <summary>
/// Lane Completion Pipeline (ADR-0016 §10, ADR-0035 §5, EPIC-007): antes de marcar una Lane como
/// <c>Completed</c> pasan los gates de Lane. Los mínimos, todos estructurales:
/// <list type="bullet">
/// <item><b>ToolCalls</b>: ninguna ToolCall de la Lane sin resolver. Terminal es
/// <c>Succeeded</c>, <c>Failed</c>, <c>Rejected</c>, <c>Cancelled</c> o <c>Reconciled</c>;
/// <c>EffectUnknown</c> sigue siendo intermedio (ADR-0004 §2).</item>
/// <item><b>Turns</b>: ningún Turn de la Lane abierto (<c>Started</c> o <c>ModelCompleted</c>).</item>
/// <item><b>Task</b>: la Task de la Lane está en vuelo (<c>Running</c> o <c>Blocked</c>), nunca
/// terminal ni sin empezar: el cierre de la Lane tiene que ser coherente con su Task.</item>
/// </list>
/// <para>Los eventos de ToolCall no llevan Lane ni Turn, así que la pertenencia se atribuye por el
/// journal: una ToolCall pertenece a la Lane si se pidió con un Turn de esa Lane abierto (pipeline
/// real) o si se pidió sin ningún Turn abierto y el Turn que la envuelve llega después (el journal
/// de la simulación escribe la cadena antes del <c>TurnStarted</c>).</para>
/// <para>Si un gate falla, la Lane NO se completa y se queda <c>Running</c>. ADR-0016 §10 y
/// ADR-0036 §3 no definen ningún evento de rechazo a nivel de Lane (la actividad
/// <c>Validating</c> de una Lane es derivada y no se persiste, INV-027): el rechazo se expresa con
/// el evento que sí existe, <c>RunValidationRejected</c>, en la validación del Run.</para>
/// </summary>
public sealed class LaneCompletionPipeline
{
    /// <summary>
    /// Ejecuta los gates de Lane sobre el journal tal cual está: devuelve qué falta para que la
    /// Lane pueda completarse (los estados canónicos salen de <see cref="CanonicalStateTracker"/>).
    /// </summary>
    public GateResult Check(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events, LaneId lane)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(lane);

        var tracker = CanonicalStateTracker.Replay(codecs, events);

        var laneTask = (TaskId?)null;
        var turns = new HashSet<TurnId>();                       // Turns de esta Lane
        var calls = new HashSet<ToolCallId>();                  // ToolCalls de esta Lane
        var unattributed = new HashSet<ToolCallId>();            // pedidas sin Turn abierto
        var openTurnLane = new Dictionary<TurnId, LaneId>();     // Turns abiertos y su Lane
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case LaneCreated created when created.LaneId.Equals(lane):
                    laneTask = created.TaskId;
                    break;
                case TurnStarted started:
                    openTurnLane[started.TurnId] = started.LaneId;
                    if (started.LaneId.Equals(lane))
                    {
                        turns.Add(started.TurnId);

                        // La cadena escrita antes del Turn pertenece al Turn que la envuelve.
                        calls.UnionWith(unattributed);
                    }

                    unattributed.Clear();
                    break;
                case TurnCompleted completed:
                    openTurnLane.Remove(completed.TurnId);
                    break;
                case TurnInterrupted interrupted:
                    openTurnLane.Remove(interrupted.TurnId);
                    break;
                case TurnAbandoned abandoned:
                    openTurnLane.Remove(abandoned.TurnId);
                    break;
                case ToolCallRequested requested:
                    if (openTurnLane.Values.Any(owner => owner.Equals(lane)))
                    {
                        calls.Add(requested.ToolCallId); // pedida con un Turn de esta Lane abierto
                    }
                    else if (openTurnLane.Count == 0)
                    {
                        unattributed.Add(requested.ToolCallId); // espera al Turn que la envuelve
                    }

                    break;
            }
        }

        if (laneTask is not { } task)
        {
            return GateResult.Fail("Lane " + lane + " no existe en el journal");
        }

        var laneState = tracker.Lane(lane);
        if (laneState != LaneState.Running)
        {
            return GateResult.Fail("Lane " + lane + " en " + (laneState?.ToString() ?? "inexistente")
                + ": solo una Lane Running pasa los gates");
        }

        var missing = new List<string>();

        // Gate de ToolCalls: ninguna sin resolver (ADR-0004 §2).
        foreach (var call in calls)
        {
            var state = tracker.ToolCall(call) ?? ToolCallState.Requested;
            if (!IsToolCallTerminal(state))
            {
                missing.Add("ToolCall " + call + " de la Lane en " + state);
            }
        }

        // Gate de Turns: ninguno abierto.
        foreach (var turn in turns)
        {
            var state = tracker.Turn(turn) ?? TurnState.Started;
            if (state is TurnState.Started or TurnState.ModelCompleted)
            {
                missing.Add("Turn " + turn + " de la Lane abierto (" + state + ")");
            }
        }

        // Gate de Task: su resultado tiene que poder aceptar el cierre de la Lane.
        var taskState = tracker.Task(task);
        if (taskState is not (TaskState.Running or TaskState.Blocked))
        {
            missing.Add("Task " + task + " de la Lane en " + (taskState?.ToString() ?? "inexistente"));
        }

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", missing));
    }

    /// <summary>Terminal del ciclo durable de una ToolCall (ADR-0004 §2): <c>EffectUnknown</c> aún no lo es.</summary>
    private static bool IsToolCallTerminal(ToolCallState state) => state is
        ToolCallState.Succeeded or ToolCallState.Failed or ToolCallState.Rejected
        or ToolCallState.Cancelled or ToolCallState.Reconciled;
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
