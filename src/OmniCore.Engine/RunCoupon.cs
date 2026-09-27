namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Verifica el completion del Run con los gates del runtime (INV-015): el modelo propone DONE,
/// el runtime decide. En M1 la simulación ejecuta las tasks cuyos items llegaron a terminales
/// y re-proyecta el plan hasta el punto fijo.
/// </summary>
public sealed class RunCoupon
{
    private readonly RunProjection _run;

    private readonly TaskGraphProjection _tasks;

    private readonly PlanProjection _plan;

    public RunCoupon(RunProjection run, TaskGraphProjection tasks, PlanProjection plan)
    {
        _run = run;
        _tasks = tasks;
        _plan = plan;
    }

    public bool CheckCompletionAndGate(PlanService planService, ProgressReconciler reconciler,
        IEventStore store, IEventCodecRegistry codecs, SessionId sessionId, EventStream stream)
    {
        // El flujo de M1: (1) el reconciler lleva los items a InProgress mientras hay tasks
        // Running (R1); (2) las tasks se completan; (3) el reconciler lleva los items a
        // Completed (R2) y re-proyecta hasta el punto fijo.
        var converged = false;
        var guard = 0;
        while (!converged && guard < 128)
        {
            guard += 1;
            var tail = store.ReadFrom(sessionId, 1);
            var planProj = PlanProjection.Replay(codecs, tail);
            var taskProj = TaskGraphProjection.Replay(codecs, tail);

            var mutations = reconciler.Reconcile(planProj, taskProj, LaneProjection.Empty());
            if (mutations.Count == 0)
            {
                // Sin mutaciones: si quedan tasks Running, completarlas y repetir.
                var anyPendingTask = false;
                foreach (var task in taskProj.Tasks())
                {
                    if (task.State == TaskState.Running)
                    {
                        stream.Append(new TaskCompleted(task.Id, null));
                        stream.Append(new LaneCompleted(LaneId.New(), null));
                        anyPendingTask = true;
                    }
                }

                if (!anyPendingTask)
                {
                    converged = true;
                }

                continue;
            }

            foreach (var mutation in mutations)
            {
                var apply = planService.Apply(planProj, taskProj, LaneProjection.Empty(), mutation);
                if (apply.Accepted)
                {
                    foreach (var evt in apply.Events)
                    {
                        stream.Append(evt);
                    }
                }
            }
        }

        var finalPlan = PlanProjection.Replay(codecs, store.ReadFrom(sessionId, 1));
        var finalTasks = TaskGraphProjection.Replay(codecs, store.ReadFrom(sessionId, 1));
        var pending = new PendingTaskGate();
        var pendingResult = pending.Check(finalTasks, finalPlan);
        if (!pendingResult.Passed)
        {
            stream.Append(new RunValidationRejected(_run.Id, new string[] { "tasks" }, pendingResult.Missing));
            stream.Append(new RunAwaitingInput(_run.Id, LaneId.New()));
            return false;
        }

        var gate = new PlanCompletionGate();
        var gateResult = gate.Check(finalPlan);
        if (!gateResult.Passed)
        {
            stream.Append(new RunValidationRejected(_run.Id, new string[] { "plan" }, gateResult.Missing));
            stream.Append(new RunAwaitingInput(_run.Id, LaneId.New()));
            return false;
        }

        stream.Append(new RunValidationStarted(_run.Id));
        stream.Append(new RunCompleted(_run.Id, RunOutcome.Completed));
        return true;
    }
}