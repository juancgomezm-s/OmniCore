namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Estado canónico de cada entidad de una sesión (Run, Task, Lane, Turn, ToolCall, PlanItem) y
/// validación de cada evento contra las máquinas de estado de ADR-0036. Lo usan
/// <see cref="EventStream"/> antes de persistir (un evento inválido nunca llega al journal) y las
/// proyecciones al reconstruir (un journal inválido falla con error tipado en vez de producir un
/// estado inventado). Un evento sobre una entidad que no existe también es inválido.
/// </summary>
public sealed class CanonicalStateTracker
{
    private readonly Dictionary<RunId, RunState> _runs;

    private readonly Dictionary<TaskId, TaskState> _tasks;

    private readonly Dictionary<LaneId, LaneState> _lanes;

    private readonly Dictionary<TurnId, TurnState> _turns;

    private readonly Dictionary<ToolCallId, ToolCallState> _toolCalls;

    private readonly Dictionary<PlanItemId, PlanItemState> _planItems;

    public CanonicalStateTracker()
        : this(new(), new(), new(), new(), new(), new())
    {
    }

    private CanonicalStateTracker(Dictionary<RunId, RunState> runs, Dictionary<TaskId, TaskState> tasks,
        Dictionary<LaneId, LaneState> lanes, Dictionary<TurnId, TurnState> turns,
        Dictionary<ToolCallId, ToolCallState> toolCalls, Dictionary<PlanItemId, PlanItemState> planItems)
    {
        _runs = runs;
        _tasks = tasks;
        _lanes = lanes;
        _turns = turns;
        _toolCalls = toolCalls;
        _planItems = planItems;
    }

    /// <summary>Reconstruye el estado aplicando (y validando) todos los eventos en orden.</summary>
    public static CanonicalStateTracker Replay(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events)
    {
        var tracker = new CanonicalStateTracker();
        foreach (var evt in events)
        {
            tracker.Apply(codecs.Decode(evt));
        }

        return tracker;
    }

    /// <summary>Copia independiente (para validar un lote sin tocar el estado si falla).</summary>
    public CanonicalStateTracker Clone() => new(new(_runs), new(_tasks), new(_lanes), new(_turns),
        new(_toolCalls), new(_planItems));

    public RunState? Run(RunId id) => _runs.TryGetValue(id, out var s) ? s : null;

    public TaskState? Task(TaskId id) => _tasks.TryGetValue(id, out var s) ? s : null;

    public LaneState? Lane(LaneId id) => _lanes.TryGetValue(id, out var s) ? s : null;

    public TurnState? Turn(TurnId id) => _turns.TryGetValue(id, out var s) ? s : null;

    public ToolCallState? ToolCall(ToolCallId id) => _toolCalls.TryGetValue(id, out var s) ? s : null;

    public PlanItemState? PlanItem(PlanItemId id) => _planItems.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Aplica un evento: calcula el estado destino con <see cref="StateMachines"/> o lanza
    /// <see cref="InvalidStateTransitionException"/> sin modificar nada.
    /// </summary>
    public void Apply(DomainEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload)
        {
            // ── Run ──
            case RunCreated created:
                Create(_runs, created.RunId, RunState.Created, "run", payload);
                break;
            case RunStarted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunAwaitingInput e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case UserInputReceived e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunValidationStarted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunValidationRejected e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunCompleted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunFailed e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunCancelled e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunModeChanged e: RequireNonTerminalRun(e.RunId, payload); break;

            // ── Task ──
            case TaskCreated created:
                RequireNonTerminalRun(created.RunId, payload);
                Create(_tasks, created.TaskId, TaskState.Pending, "task", payload);
                break;
            case TaskReady e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskStarted e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskBlocked e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskUnblocked e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskCompleted e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskFailed e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskSkipped e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskCancelled e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;

            // ── Lane ──
            case LaneCreated created:
                Require(_tasks, created.TaskId, "task", payload);
                Create(_lanes, created.LaneId, LaneState.Queued, "lane", payload);
                break;
            case LaneProvisioning e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneStarted e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneBlocked e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneUnblocked e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneCompleted e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneFailed e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneCancelled e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;

            // ── Turn ──
            case TurnStarted started:
                Create(_turns, started.TurnId, TurnState.Started, "turn", payload);
                break;
            case ModelCompleted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnCompleted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnInterrupted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnAbandoned e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;

            // ── ToolCall (ADR-0004 §2, ADR-0036 §5) ──
            case ToolCallRequested requested:
                Create(_toolCalls, requested.ToolCallId, ToolCallState.Requested, "toolcall", payload);
                break;
            case ToolCallPrepared e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallRejected e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionEvaluated e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionRequested e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionGranted e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionDenied e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallAuthorized e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallStarted e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallSucceeded e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallFailed e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallEffectUnknown e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallReconciled e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallCancelled e: ToolCallTransition(e.ToolCallId, payload); break;

            // ── PlanItem (ADR-0036 §4) ──
            case PlanCreated created:
                RequireNonTerminalRun(created.RunId, payload);
                Create(_planItems, created.RootItemId, PlanItemState.Pending, "plan_item", payload);
                break;
            case PlanItemAdded added:
                Create(_planItems, added.PlanItemId, PlanItemState.Pending, "plan_item", payload);
                break;
            case PlanItemReady e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemStarted e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemBlocked e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemUnblocked e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemCompleted e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemFailed e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemSkipped e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemCancelled e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemReopened e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemUpdated e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;
            case PlanItemLinked e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;
            case PlanItemUnlinked e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;

            // Resto (sesión, interacciones, audit, mensajes, reordenación…): no son transiciones de
            // estado canónico de una entidad.
        }
    }

    private void ToolCallTransition(ToolCallId id, DomainEventPayload payload) =>
        Transition(_toolCalls, id, "toolcall", payload, StateMachines.ApplyToolCall);

    private void PlanItemTransition(PlanItemId id, DomainEventPayload payload) =>
        Transition(_planItems, id, "plan_item", payload, StateMachines.ApplyPlanItem);

    private void RequireNonTerminalRun(RunId id, DomainEventPayload payload)
    {
        var state = Require(_runs, id, "run", payload);
        if (StateMachines.IsRunTerminal(state))
        {
            throw new InvalidStateTransitionException("run", state, payload.Type().ToString());
        }
    }

    private static void Create<TId, TState>(Dictionary<TId, TState> map, TId id, TState initial, string entity,
        DomainEventPayload payload) where TId : notnull
    {
        if (map.TryGetValue(id, out var existing))
        {
            // Crear dos veces la misma entidad no es una transición válida.
            throw new InvalidStateTransitionException(entity, existing!, payload.Type().ToString());
        }

        map[id] = initial;
    }

    private static TState Require<TId, TState>(Dictionary<TId, TState> map, TId id, string entity,
        DomainEventPayload payload) where TId : notnull
    {
        if (!map.TryGetValue(id, out var state))
        {
            throw new InvalidStateTransitionException(entity, "inexistente", payload.Type().ToString());
        }

        return state;
    }

    private static void Transition<TId, TState>(Dictionary<TId, TState> map, TId id, string entity,
        DomainEventPayload payload, Func<TState, DomainEventPayload, TState> apply) where TId : notnull
    {
        var from = Require(map, id, entity, payload);
        map[id] = apply(from, payload);
    }
}
