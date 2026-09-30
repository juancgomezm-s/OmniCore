namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Completion del Run con los gates del runtime (INV-010): el modelo propone DONE, el runtime
/// decide. En M1 la simulación completa las Tasks cuyo trabajo terminó, deja que el reconciler
/// lleve el Plan al punto fijo y después valida. Antes de cada <c>LaneCompleted</c> pasa el
/// <see cref="LaneCompletionPipeline"/> (EPIC-007): una Lane con ToolCalls sin resolver o Turns
/// abiertos no se completa.
///
/// Secuencia canónica (ADR-0036 §1, ADR-0035 §5, ADR-0016 §10):
/// <c>RunValidationStarted</c> → gates → (<c>RunValidationRejected</c> + <c>RunAwaitingInput</c>) o
/// (cierre de la Lane y la Task raíz + <c>RunCompleted</c> / <c>RunFailed</c>). Un item requerido
/// <c>Failed</c> nunca da un <c>Completed</c> limpio: el Run termina <c>Failed</c>, o
/// <c>CompletedWithIssues</c> si la <c>FailurePolicy</c> es <c>AllowPartial</c>.
/// </summary>
public sealed class RunCoupon
{
    private readonly RunProjection _run;

    public RunCoupon(RunProjection run, TaskGraphProjection tasks, PlanProjection plan)
    {
        _run = run;
    }

    public bool CheckCompletionAndGate(PlanService planService, ProgressReconciler reconciler,
        IEventStore store, IEventCodecRegistry codecs, SessionId sessionId, EventStream stream)
    {
        var rootTask = _run.RootTask;
        var pipeline = new LaneCompletionPipeline();
        var converged = false;
        var guard = 0;
        while (!converged && guard < 1024)
        {
            guard += 1;
            var tail = store.ReadFrom(sessionId, 1);
            var planProj = PlanProjection.Replay(codecs, tail);
            var taskProj = TaskGraphProjection.Replay(codecs, tail);
            var laneProj = LaneProjection.Replay(codecs, tail);

            var mutations = reconciler.Reconcile(planProj, taskProj, laneProj,
                _run.FailurePolicy ?? FailurePolicy.BlockDependents);
            var anyAccepted = false;
            foreach (var mutation in mutations)
            {
                var apply = planService.Apply(planProj, taskProj, laneProj, mutation);
                if (apply.Accepted)
                {
                    stream.AppendBatch(apply.Events, DurabilityClass.Standard);
                    anyAccepted = true;
                    break; // re-proyectar: cada mutación se valida contra el estado que dejó la anterior
                }
            }

            if (!anyAccepted)
            {
                // Sin mutaciones: las Tasks de trabajo que siguen Running terminan (cerrando antes
                // sus Lanes) y se vuelve a reconciliar. La Task raíz vive hasta el final del Run.
                // EPIC-007: cada Lane pasa su Lane Completion Pipeline antes de completarse; si un
                // gate falla, la Lane (y su Task) quedan Running y la validación del Run las verá.
                var anyRunning = false;
                foreach (var task in taskProj.Tasks())
                {
                    if (task.State != TaskState.Running || task.Id.Equals(rootTask))
                    {
                        continue;
                    }

                    var runningLanes = laneProj.ForTask(task.Id)
                        .Where(l => l.State == LaneState.Running)
                        .ToArray();
                    if (runningLanes.Any(l => !pipeline.Check(codecs, tail, l.Id).Passed))
                    {
                        continue;
                    }

                    foreach (var lane in runningLanes)
                    {
                        stream.Append(new LaneCompleted(lane.Id, null));
                    }

                    stream.Append(new TaskCompleted(task.Id, null));
                    anyRunning = true;
                }

                converged = !anyRunning;
            }
        }

        var finalTail = store.ReadFrom(sessionId, 1);
        var finalPlan = PlanProjection.Replay(codecs, finalTail);
        var finalTasks = TaskGraphProjection.Replay(codecs, finalTail);
        var finalLanes = LaneProjection.Replay(codecs, finalTail);
        var rootLane = rootTask is null ? null : finalLanes.ForTask(rootTask).FirstOrDefault()?.Id;

        stream.Append(new RunValidationStarted(_run.Id));

        var pendingResult = new PendingTaskGate().Check(finalTasks, finalPlan, rootTask);
        var gateResult = new PlanCompletionGate().Check(finalPlan);

        // EPIC-007: las Lanes de la Task raíz (la conversación) pasan su Lane Completion Pipeline
        // antes de cerrarse con el Run. Si falla no hay evento de rechazo a nivel de Lane
        // (ADR-0016 §10, ADR-0036 §3): el rechazo es el del Run, con gate "lane".
        var laneMissing = new List<string>();
        if (rootTask is not null)
        {
            foreach (var lane in finalLanes.ForTask(rootTask).Where(l => l.State == LaneState.Running))
            {
                var laneResult = pipeline.Check(codecs, finalTail, lane.Id);
                if (!laneResult.Passed)
                {
                    laneMissing.AddRange(laneResult.Missing);
                }
            }
        }

        if (!pendingResult.Passed || !gateResult.Passed || laneMissing.Count > 0)
        {
            var gates = new List<string>();
            var missing = new List<string>();
            if (!pendingResult.Passed)
            {
                gates.Add("tasks");
                missing.AddRange(pendingResult.Missing);
            }

            if (!gateResult.Passed)
            {
                gates.Add("plan");
                missing.AddRange(gateResult.Missing);
            }

            if (laneMissing.Count > 0)
            {
                gates.Add("lane");
                missing.AddRange(laneMissing);
            }

            stream.Append(new RunValidationRejected(_run.Id, gates, missing));
            if (rootLane is not null)
            {
                // La Lane raíz queda esperando al usuario (ADR-0035 §1).
                stream.Append(new RunAwaitingInput(_run.Id, rootLane));
            }

            return false;
        }

        var failedRequired = new PlanCompletionGate().FailedRequired(finalPlan);
        CloseRoot(stream, finalTasks, finalLanes, rootTask, failedRequired.Count == 0);
        if (failedRequired.Count == 0)
        {
            stream.Append(new RunCompleted(_run.Id, RunOutcome.Completed));
            return true;
        }

        if (_run.FailurePolicy == FailurePolicy.AllowPartial)
        {
            stream.Append(new RunCompleted(_run.Id, RunOutcome.CompletedWithIssues));
            return true;
        }

        stream.Append(new RunFailed(_run.Id, "items requeridos fallidos: " + string.Join(", ", failedRequired)));
        return false;
    }

    /// <summary>Cierra la Lane raíz y la Task raíz cuando el Run termina (ADR-0035 §2).</summary>
    private static void CloseRoot(EventStream stream, TaskGraphProjection tasks, LaneProjection lanes,
        TaskId? rootTask, bool succeeded)
    {
        if (rootTask is null)
        {
            return;
        }

        foreach (var lane in lanes.ForTask(rootTask))
        {
            if (lane.State == LaneState.Running)
            {
                stream.Append(succeeded ? new LaneCompleted(lane.Id, null) : new LaneFailed(lane.Id, "run fallido"));
            }
        }

        var state = tasks.StateOf(rootTask);
        if (state == TaskState.Running || state == TaskState.Blocked)
        {
            stream.Append(succeeded ? new TaskCompleted(rootTask, null) : new TaskFailed(rootTask, "run fallido"));
        }
    }
}
