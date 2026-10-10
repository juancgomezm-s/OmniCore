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
/// <para>Los eventos modernos heredan Lane y Turn en el envelope. Los journals legacy solo se
/// atribuyen por defecto cuando el Run contiene una única Lane; un ToolCall ambiguo en un Run
/// multi-Lane no se carga al worker equivocado.</para>
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
        var ownership = new CanonicalEventOwnership(codecs, events);
        RunId? laneRun = null;
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case LaneCreated created when created.LaneId.Equals(lane):
                    laneTask = created.TaskId;
                    laneRun = evt.RunId ?? evt.CorrelationId;
                    break;
                case TurnStarted started:
                    if (started.LaneId.Equals(lane))
                    {
                        turns.Add(started.TurnId);
                    }
                    break;
                case ToolCallRequested requested:
                    if (ownership.LaneOf(evt) == lane) calls.Add(requested.ToolCallId);
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

        if (laneRun is { } runId)
        {
            foreach (var evt in events.Where(item => (item.RunId ?? item.CorrelationId) == runId
                && ownership.IsAmbiguousToolCall(item) && codecs.Decode(item) is ToolCallRequested))
            {
                var call = (ToolCallRequested)codecs.Decode(evt);
                if (!IsToolCallTerminal(tracker.ToolCall(call.ToolCallId) ?? ToolCallState.Requested))
                    missing.Add("ToolCall " + call.ToolCallId + " tiene Lane ambigua en este Run");
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
        IReadOnlyCollection<LaneId> lanes) => Scan(codecs, events, lanes, null).TurnsSinceSignal;

    /// <summary>
    /// Recorrido del watchdog. Además de los Turns desde la última señal, cuenta los Turns del
    /// episodio actual del item (desde la señal o desde su último <c>ProgressStalled</c>, ADR-0048
    /// §1) y las respuestas ya seleccionadas desde la señal (paso de la cadena, ADR-0048 §2).
    /// </summary>
    internal static WatchdogScan Scan(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events,
        IReadOnlyCollection<LaneId> lanes, PlanItemId? item)
    {
        var turns = 0;
        var episodeTurns = 0;
        var step = 0;
        StallResponseSelected? lastResponse = null;
        DateTimeOffset? lastSignalAt = null;
        IReadOnlyList<string>? lastRejection = null;
        var answeringHuman = false;
        var stallOpen = false;
        var effects = new Dictionary<ToolCallId, EffectClass>();
        var resourceOf = new Dictionary<ToolCallId, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ownership = new CanonicalEventOwnership(codecs, events);
        foreach (var evt in events)
        {
            var payload = codecs.Decode(evt);
            var signal = false;
            if (payload is ProgressStalled) stallOpen = true;
            switch (payload)
            {
                case TurnStarted started when lanes.Contains(started.LaneId):
                    // El Turn que responde a un input humano tiene dirección nueva: no cuenta.
                    if (answeringHuman)
                    {
                        answeringHuman = false;
                        break;
                    }
                    turns += 1;
                    episodeTurns += 1;
                    break;
                case ProgressStalled stalled when item is { } watched && stalled.PlanItemId == watched:
                    episodeTurns = 0;
                    break;
                case StallResponseSelected response when item is { } chained && response.PlanItemId == chained:
                    step += 1;
                    lastResponse = response;
                    break;
                case UserInputReceived input when !IsRuntimeOrigin(input.Origin):
                    // ADR-0048 §1: un input humano es dirección nueva, no un agente girando en falso.
                    // Reinicia el conteo, pero no la cadena ni lastProgressAt: no es progreso del Plan.
                    turns = 0;
                    episodeTurns = 0;
                    answeringHuman = true;
                    break;
                case ToolCallRequested requested:
                    if (ownership.LaneOf(evt) is { } requestedLane && lanes.Contains(requestedLane))
                        resourceOf[requested.ToolCallId] = requested.ToolName + "|" + requested.ArgumentsJson;
                    break;
                case ToolCallStarted started:
                    if (resourceOf.ContainsKey(started.ToolCallId)) effects[started.ToolCallId] = started.EffectClass;
                    break;
                case ToolCallSucceeded succeeded:
                    var applied = effects.TryGetValue(succeeded.ToolCallId, out var effect) && effect != EffectClass.None;
                    var fresh = resourceOf.TryGetValue(succeeded.ToolCallId, out var resource) && seen.Add(resource);
                    signal = applied || fresh;
                    break;
                case RunValidationRejected rejected:
                    // ADR-0048 §1: el resultado de una validación es señal salvo que repita el rechazo
                    // anterior; un rechazo idéntico es justo el bucle que el watchdog debe detectar.
                    signal = lastRejection is null || !lastRejection.SequenceEqual(rejected.Missing, StringComparer.Ordinal);
                    lastRejection = rejected.Missing;
                    break;
                case ToolCallReconciled { Outcome: ReconciliationOutcome.Applied }
                    when ownership.LaneOf(evt) is { } reconciledLane && lanes.Contains(reconciledLane):
                    signal = true;
                    break;
                default:
                    signal = IsPlanTransition(payload)
                        || IsLaneOrTaskTransition(payload) && ownership.LaneOf(evt) is { } transitionLane && lanes.Contains(transitionLane);
                    break;
            }

            if (signal)
            {
                turns = 0;
                episodeTurns = 0;
                step = 0;
                lastResponse = null;
                lastSignalAt = evt.Timestamp;
                stallOpen = false;
            }
        }

        return new WatchdogScan(turns, episodeTurns, step, lastResponse, lastSignalAt, stallOpen);
    }

    /// <summary>Prefijo de origen de los prompts que genera el propio runtime (ADR-0048 §1).</summary>
    public const string RuntimeOriginPrefix = "Runtime(";

    private static bool IsRuntimeOrigin(string? origin) =>
        origin?.StartsWith(RuntimeOriginPrefix, StringComparison.Ordinal) == true;

    private static bool IsPlanTransition(DomainEventPayload payload) => payload is
        PlanItemReady or PlanItemStarted or PlanItemBlocked or PlanItemUnblocked or PlanItemCompleted
        or PlanItemFailed or PlanItemSkipped or PlanItemCancelled or PlanItemReopened;

    private static bool IsLaneOrTaskTransition(DomainEventPayload payload) => payload is
        TaskReady or TaskStarted or TaskBlocked or TaskUnblocked or TaskCompleted or TaskFailed or TaskSkipped
        or TaskCancelled or LaneStarted or LaneBlocked or LaneUnblocked or LaneCompleted or LaneFailed or LaneCancelled;
}

/// <summary>Resultado del recorrido del watchdog (ADR-0048 §1–§2).</summary>
internal sealed record WatchdogScan(int TurnsSinceSignal, int EpisodeTurns, int Step,
    StallResponseSelected? LastResponse, DateTimeOffset? LastSignalAt, bool StallOpen = false);
