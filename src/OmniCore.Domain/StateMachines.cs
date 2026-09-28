namespace OmniCore.Domain;

/// <summary>
/// Máquinas de estado canónicas (ADR-0036). Funciones puras y deterministas: cada transición
/// válida tiene exactamente un evento; los estados derivados (Waiting*, Stalled, Validating)
/// nunca se persisten y se calculan aparte. Los estados se validan antes de emitir, así que un
/// evento persistido nunca es inválido (ADR-0001 §3).
/// </summary>
public sealed class StateMachines
{
    private StateMachines() { }

    public static bool IsRunTerminal(RunState state) =>
        state == RunState.Completed || state == RunState.CompletedWithIssues || state == RunState.Failed
        || state == RunState.Cancelled;

    public static bool IsTaskTerminal(TaskState state) =>
        state == TaskState.Completed || state == TaskState.Failed || state == TaskState.Skipped
        || state == TaskState.Cancelled;

    public static bool IsLaneTerminal(LaneState state) =>
        state == LaneState.Completed || state == LaneState.Failed || state == LaneState.Cancelled;

    public static bool IsPlanItemTerminal(PlanItemState state) =>
        state == PlanItemState.Completed || state == PlanItemState.Failed || state == PlanItemState.Skipped
        || state == PlanItemState.Cancelled;

    // ---------------------------------------------------------------- Run
    /// <summary>Devuelve el estado destino, o lanza InvalidStateTransitionException si no aplica.</summary>
    public static RunState ApplyRun(RunState from, DomainEventPayload evt)
    {
        if (evt is RunCreated && from == RunState.Created) return RunState.Created;
        if (evt is RunStarted && from == RunState.Created) return RunState.Running;
        if (evt is RunAwaitingInput && from == RunState.Running) return RunState.AwaitingInput;
        if (evt is UserInputReceived && from == RunState.AwaitingInput) return RunState.Running;
        if (evt is RunValidationStarted && from == RunState.Running) return RunState.Validating;
        if (evt is RunValidationRejected && from == RunState.Validating) return RunState.Running;
        if (evt is RunCompleted && from == RunState.Validating) return RunState.Completed;
        if (evt is RunFailed
            && (from == RunState.Running || from == RunState.Validating || from == RunState.AwaitingInput))
        {
            return RunState.Failed;
        }

        if (evt is RunCancelled && !IsRunTerminal(from)) return RunState.Cancelled;
        throw new InvalidStateTransitionException("run", from, evt.Type().ToString());
    }

    // --------------------------------------------------------------- Task
    public static TaskState ApplyTask(TaskState from, DomainEventPayload evt)
    {
        if (evt is TaskCreated && from == TaskState.Pending) return TaskState.Pending;
        if (evt is TaskReady && from == TaskState.Pending) return TaskState.Ready;
        if (evt is TaskStarted && from == TaskState.Ready) return TaskState.Running;
        if (evt is TaskBlocked && from == TaskState.Running) return TaskState.Blocked;
        if (evt is TaskUnblocked && from == TaskState.Blocked)
        {
            return (evt as TaskUnblocked)!.Requeue ? TaskState.Ready : TaskState.Running;
        }

        if (evt is TaskCompleted && (from == TaskState.Running || from == TaskState.Blocked))
        {
            return TaskState.Completed;
        }

        if (evt is TaskFailed && (from == TaskState.Running || from == TaskState.Blocked))
        {
            return TaskState.Failed;
        }

        if (evt is TaskSkipped
            && (from == TaskState.Pending || from == TaskState.Ready || from == TaskState.Blocked))
        {
            return TaskState.Skipped;
        }

        if (evt is TaskCancelled && !IsTaskTerminal(from)) return TaskState.Cancelled;
        throw new InvalidStateTransitionException("task", from, evt.Type().ToString());
    }

    // --------------------------------------------------------------- Lane
    public static LaneState ApplyLane(LaneState from, DomainEventPayload evt)
    {
        if (evt is LaneCreated && from == LaneState.Queued) return LaneState.Queued;
        if (evt is LaneProvisioning && from == LaneState.Queued) return LaneState.Provisioning;
        if (evt is LaneStarted && (from == LaneState.Queued || from == LaneState.Provisioning))
        {
            return LaneState.Running;
        }

        if (evt is LaneBlocked && from == LaneState.Running) return LaneState.Blocked;
        if (evt is LaneUnblocked && from == LaneState.Blocked) return LaneState.Running;
        if (evt is LaneCompleted && from == LaneState.Running) return LaneState.Completed;
        if (evt is LaneFailed
            && (from == LaneState.Running || from == LaneState.Blocked || from == LaneState.Provisioning))
        {
            return LaneState.Failed;
        }

        if (evt is LaneCancelled && !IsLaneTerminal(from)) return LaneState.Cancelled;
        throw new InvalidStateTransitionException("lane", from, evt.Type().ToString());
    }

    // ------------------------------------------------------------ PlanItem
    public static PlanItemState ApplyPlanItem(PlanItemState from, DomainEventPayload evt)
    {
        if (evt is PlanItemAdded && from == PlanItemState.Pending) return PlanItemState.Pending;
        if (evt is PlanItemReady && from == PlanItemState.Pending) return PlanItemState.Ready;
        if (evt is PlanItemStarted && (from == PlanItemState.Pending || from == PlanItemState.Ready))
        {
            return PlanItemState.InProgress;
        }

        if (evt is PlanItemBlocked && from == PlanItemState.InProgress) return PlanItemState.Blocked;
        if (evt is PlanItemUnblocked && from == PlanItemState.Blocked) return PlanItemState.InProgress;
        if (evt is PlanItemCompleted && from == PlanItemState.InProgress) return PlanItemState.Completed;
        if (evt is PlanItemFailed && (from == PlanItemState.InProgress || from == PlanItemState.Blocked))
        {
            return PlanItemState.Failed;
        }

        if (evt is PlanItemReopened && from == PlanItemState.Failed) return PlanItemState.Ready;
        if (evt is PlanItemSkipped
            && (from == PlanItemState.Pending || from == PlanItemState.Ready || from == PlanItemState.Blocked))
        {
            return PlanItemState.Skipped;
        }

        if (evt is PlanItemCancelled && !IsPlanItemTerminal(from)) return PlanItemState.Cancelled;
        throw new InvalidStateTransitionException("plan_item", from, evt.Type().ToString());
    }

    // --------------------------------------------------------------- Turn
    public static TurnState ApplyTurn(TurnState from, DomainEventPayload evt)
    {
        if (evt is TurnStarted && from == TurnState.Started) return TurnState.Started;
        if (evt is ModelCompleted && from == TurnState.Started) return TurnState.ModelCompleted;
        if (evt is TurnCompleted && (from == TurnState.ModelCompleted || from == TurnState.Started))
        {
            return TurnState.Completed;
        }

        if (evt is TurnInterrupted && from == TurnState.Started) return TurnState.Interrupted;
        if (evt is TurnAbandoned && (from == TurnState.Started || from == TurnState.Interrupted))
        {
            return TurnState.Abandoned;
        }

        throw new InvalidStateTransitionException("turn", from, evt.Type().ToString());
    }

    // ------------------------------------------------------------ ToolCall
    /// <summary>Devuelve el siguiente estado del ciclo de vida durable de una ToolCall (ADR-0004 §2).</summary>
    public static ToolCallState ApplyToolCall(ToolCallState from, DomainEventPayload evt)
    {
        if (evt is ToolCallRequested && from == ToolCallState.Requested) return ToolCallState.Requested;
        if (evt is ToolCallPrepared && from == ToolCallState.Requested) return ToolCallState.Prepared;
        if (evt is ToolCallRejected && from == ToolCallState.Requested) return ToolCallState.Rejected;
        if (evt is PermissionEvaluated && from == ToolCallState.Prepared) return ToolCallState.Prepared;
        if (evt is PermissionRequested && from == ToolCallState.Prepared) return ToolCallState.AwaitingPermission;
        if (evt is PermissionGranted
            && (from == ToolCallState.AwaitingPermission || from == ToolCallState.Prepared))
        {
            return ToolCallState.Authorized;
        }

        if (evt is PermissionDenied
            && (from == ToolCallState.AwaitingPermission || from == ToolCallState.Prepared
                || from == ToolCallState.Authorized)) return ToolCallState.Rejected;
        // Authorized puede llegar desde Prepared (Allow directo, INP) o Authorized (par idempotente).
        if (evt is ToolCallAuthorized
            && (from == ToolCallState.Prepared || from == ToolCallState.Authorized))
        {
            return ToolCallState.Authorized;
        }
        if (evt is ToolCallStarted
            && (from == ToolCallState.Authorized || from == ToolCallState.Started))
        {
            return ToolCallState.Started;
        }

        if (evt is ToolCallSucceeded && from == ToolCallState.Started) return ToolCallState.Succeeded;
        if (evt is ToolCallFailed
            && (from == ToolCallState.Started || from == ToolCallState.Authorized))
        {
            return ToolCallState.Failed;
        }

        if (evt is ToolCallEffectUnknown && from == ToolCallState.Started) return ToolCallState.EffectUnknown;
        if (evt is ToolCallReconciled
            && (from == ToolCallState.EffectUnknown || from == ToolCallState.Started))
        {
            return ToolCallState.Reconciled;
        }

        if (evt is ToolCallCancelled
            && (from == ToolCallState.Requested || from == ToolCallState.Prepared
            || from == ToolCallState.AwaitingPermission || from == ToolCallState.Authorized))
        {
            return ToolCallState.Cancelled;
        }

        throw new InvalidStateTransitionException("toolcall", from, evt.Type().ToString());
    }
}

/// <summary>Excepción tipada de dominio cuando una transición no es válida (spec §71).</summary>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public string Entity { get; }

    public string From { get; }

    public string To { get; }

    public InvalidStateTransitionException(string entity, object from, string to)
    {
        Entity = entity;
        From = from is null ? "null" : from.ToString() ?? "null";
        To = to;
    }
}
