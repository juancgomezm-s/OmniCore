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
        LaneProjection lanes)
    {
        var mutations = new List<PlanMutation>();
        foreach (var item in plan.Items())
        {
            if (StateMachines.IsPlanItemTerminal(item.State))
            {
                continue;
            }

            mutations.AddRange(RulesFor(item, plan, tasks, lanes));
        }

        return mutations.ToArray();
    }

    /// <summary>Calcula el item actual tras una transición (R7).</summary>
    public PlanItemId? CurrentItem(PlanProjection plan)
    {
        var items = plan.Items();
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
        LaneProjection lanes)
    {
        var mutations = new List<PlanMutation>();
        var alreadyForwarding = false;

        // R6: dependencias completadas/skpidas → Pending → Ready (via Start hacia InProgress,
        //    que es la mutación disponible en M1; se desambiguará con PlanControl en M2).
        if (item.State == PlanItemState.Pending && DependenciesSatisfied(item, plan)
            && !HasRunningLinkedTask(item, tasks, lanes))
        {
            mutations.Add(PlanMutation.Start(item.Id, MutationCause.Reconciler));
            alreadyForwarding = true;
        }

        // R1: alguna Task vinculada Running → InProgress (una sola vez por item).
        if ((item.State == PlanItemState.Pending || item.State == PlanItemState.Ready)
            && HasRunningLinkedTask(item, tasks, lanes) && !alreadyForwarding)
        {
            mutations.Add(PlanMutation.Start(item.Id, MutationCause.Reconciler));
            alreadyForwarding = true;
        }

        // R3: alguna Task requerida Blocked → Blocked
        if (!alreadyForwarding && item.State == PlanItemState.InProgress && HasBlockedLinkedTask(item, tasks))
        {
            mutations.Add(PlanMutation.Block(item.Id, MutationCause.Reconciler, "task bloqueada"));
        }

        // R4: la Task bloqueante volvió a Ready/Running → InProgress
        if (item.State == PlanItemState.Blocked && !HasBlockedLinkedTask(item, tasks))
        {
            mutations.Add(PlanMutation.Unblock(item.Id, MutationCause.Reconciler, "task desbloqueada"));
        }

        // R2: todas las Tasks requeridas Completed y Verifies pasaron → Completed
        if (item.State == PlanItemState.InProgress && LinkedTasksSatisfied(item, tasks))
        {
            mutations.Add(PlanMutation.Complete(item.Id, MutationCause.Reconciler, "tasks completadas"));
        }

        // R5: alguna Task requerida Failed con política de bloqueo → Blocked; FailRun → Failed
        if ((item.State == PlanItemState.InProgress || item.State == PlanItemState.Blocked)
            && HasFailedLinkedTask(item, tasks))
        {
            var runPolicy = PlanFailurePolicyOf(plan);
            if (runPolicy == FailurePolicy.FailRun)
            {
                mutations.Add(PlanMutation.Fail(item.Id, MutationCause.Reconciler, "task requerida fallida"));
            }
            else
            {
                mutations.Add(PlanMutation.Block(item.Id, MutationCause.Reconciler, "task requerida fallida"));
            }
        }

        return mutations.ToArray();
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

    private static FailurePolicy PlanFailurePolicyOf(PlanProjection plan) => FailurePolicy.BlockDependents;
}