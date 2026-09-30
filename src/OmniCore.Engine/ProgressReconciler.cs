namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// ProgressReconciler: componente puro y determinista que sincroniza el Plan con el TaskGraph
/// (ADR-0016 §5, R1–R7). Se ejecuta tras cambios en Tasks/Lanes, Turns, outcomes y al reanudar.
/// Devuelve mutaciones con Cause = Reconciler; el PlanService las valida y emite.
/// </summary>
public sealed class ProgressReconciler
{
    /// <summary>Umbral de Turns sin señal de progreso (ADR-0036 §7). Constante en M1.</summary>
    public static readonly int DefaultStallThresholdTurns = 6;

    public IReadOnlyList<PlanMutation> Reconcile(PlanProjection plan, TaskGraphProjection tasks,
        LaneProjection lanes) => Reconcile(plan, tasks, lanes, FailurePolicy.BlockDependents);

    /// <summary>
    /// Reglas R1–R6 sobre las hojas del Plan (los contenedores se derivan de sus hijos, ADR-0036 §4).
    /// Nunca retrocede un item terminal. <paramref name="failurePolicy"/> es la del Run (R5).
    /// </summary>
    public IReadOnlyList<PlanMutation> Reconcile(PlanProjection plan, TaskGraphProjection tasks,
        LaneProjection lanes, FailurePolicy failurePolicy)
    {
        var mutations = new List<PlanMutation>();
        foreach (var item in PlanCompletionGate.Leaves(plan))
        {
            if (StateMachines.IsPlanItemTerminal(item.State))
            {
                continue;
            }

            mutations.AddRange(RulesFor(item, plan, tasks, lanes, failurePolicy));
        }

        return mutations.ToArray();
    }

    /// <summary>Calcula el item actual tras una transición (R7).</summary>
    public PlanItemId? CurrentItem(PlanProjection plan)
    {
        var items = PlanCompletionGate.Leaves(plan);
        var firstInProgress = MinimumByOrder(items, PlanItemState.InProgress);
        if (firstInProgress is not null)
        {
            return firstInProgress;
        }

        return MinimumByOrder(items, PlanItemState.Ready);
    }

    private static PlanItemId? MinimumByOrder(IReadOnlyList<PlanItem> items, PlanItemState wanted)
    {
        PlanItemId? best = null;
        var bestOrder = int.MaxValue;
        foreach (var item in items)
        {
            if (item.State == wanted && item.Order < bestOrder)
            {
                best = item.Id;
                bestOrder = item.Order;
            }
        }

        return best;
    }

    private IReadOnlyList<PlanMutation> RulesFor(PlanItem item, PlanProjection plan, TaskGraphProjection tasks,
        LaneProjection lanes, FailurePolicy failurePolicy)
    {
        var running = HasRunningLinkedTask(item, tasks, lanes);

        // R1: alguna Task vinculada pasa a Running y el item está Pending o Ready → InProgress.
        if ((item.State == PlanItemState.Pending || item.State == PlanItemState.Ready) && running)
        {
            return new[] { PlanMutation.Start(item.Id, MutationCause.Reconciler) };
        }

        // R6: todas las dependencias Completed o Skipped → Pending → Ready. Ready no es progreso:
        // un item sin Tasks vinculadas solo avanza más allá por mutaciones explícitas.
        if (item.State == PlanItemState.Pending && DependenciesSatisfied(item, plan))
        {
            return new[] { PlanMutation.Ready(item.Id, MutationCause.Reconciler) };
        }

        // R5: una Task requerida Failed (recuperación agotada) → Failed o Blocked según la FailurePolicy.
        if ((item.State == PlanItemState.InProgress || item.State == PlanItemState.Blocked)
            && HasFailedLinkedTask(item, tasks))
        {
            return new[] { failurePolicy == FailurePolicy.BlockDependents
                ? PlanMutation.Block(item.Id, MutationCause.Reconciler, "task requerida fallida")
                : PlanMutation.Fail(item.Id, MutationCause.Reconciler, "task requerida fallida") };
        }

        // R3: una Task requerida Blocked → Blocked.
        if (item.State == PlanItemState.InProgress && HasBlockedLinkedTask(item, tasks))
        {
            return new[] { PlanMutation.Block(item.Id, MutationCause.Reconciler, "task bloqueada") };
        }

        // R4: la Task que bloqueaba volvió a Ready o Running → InProgress.
        if (item.State == PlanItemState.Blocked && !HasBlockedLinkedTask(item, tasks) && !HasFailedLinkedTask(item, tasks))
        {
            return new[] { PlanMutation.Unblock(item.Id, MutationCause.Reconciler, "task desbloqueada") };
        }

        // R2: todas las Tasks vinculadas requeridas Completed (las Verifies incluidas) → Completed.
        // Sin Tasks requeridas vinculadas no hay nada que reconciliar: el item no se completa solo.
        if (item.State == PlanItemState.InProgress && LinkedTasksSatisfied(item, tasks))
        {
            return new[] { PlanMutation.Complete(item.Id, MutationCause.Reconciler, "tasks completadas") };
        }

        return Array.Empty<PlanMutation>();
    }

    private static bool DependenciesSatisfied(PlanItem item, PlanProjection plan)
    {
        foreach (var dep in item.DependsOn)
        {
            var depItem = plan.Item(dep);
            if (depItem is null || (depItem.State != PlanItemState.Completed && depItem.State != PlanItemState.Skipped))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRunningLinkedTask(PlanItem item, TaskGraphProjection tasks, LaneProjection lanes)
    {
        foreach (var link in item.LinkedTasks)
        {
            var state = tasks.StateOf(link.TaskId);
            if (state == TaskState.Running)
            {
                return true;
            }

            if (state == TaskState.Pending || state == TaskState.Ready)
            {
                // Algunas lienzas de la task ya arrancaron
                if (lanes.ForTask(link.TaskId).Count > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasBlockedLinkedTask(PlanItem item, TaskGraphProjection tasks)
    {
        foreach (var link in item.LinkedTasks)
        {
            if (link.Required && tasks.StateOf(link.TaskId) == TaskState.Blocked)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LinkedTasksSatisfied(PlanItem item, TaskGraphProjection tasks)
    {
        if (!item.LinkedTasks.Any(link => link.Required))
        {
            return false;
        }

        foreach (var link in item.LinkedTasks)
        {
            if (link.Required && tasks.StateOf(link.TaskId) != TaskState.Completed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasFailedLinkedTask(PlanItem item, TaskGraphProjection tasks)
    {
        foreach (var link in item.LinkedTasks)
        {
            if (link.Required && tasks.StateOf(link.TaskId) == TaskState.Failed)
            {
                return true;
            }
        }

        return false;
    }

}