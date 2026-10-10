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
public sealed record ExternalCompletionGateResult(string Key, bool Passed, string Summary,
    InteractionRequested? PendingInteraction = null, ArtifactRef? OutputArtifact = null);

public sealed class RunCoupon
{
    private readonly RunProjection _run;

    public RunCoupon(RunProjection run, TaskGraphProjection tasks, PlanProjection plan)
    {
        _run = run;
    }

    public bool CheckCompletionAndGate(PlanService planService, ProgressReconciler reconciler,
        IEventStore store, IEventCodecRegistry codecs, SessionId sessionId, EventStream stream,
        Func<IReadOnlyList<ExternalCompletionGateResult>>? runExternalGates = null,
        MutationLedger? mutationLedger = null)
    {
        var rootTask = _run.RootTask;
        var pipeline = new LaneCompletionPipeline();
        var converged = false;
        var guard = 0;
        while (!converged && guard < 1024)
        {
            guard += 1;
            var tail = RunEvents(codecs, store.ReadFrom(sessionId, 1));
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

        var finalTail = RunEvents(codecs, store.ReadFrom(sessionId, 1));
        var finalPlan = PlanProjection.Replay(codecs, finalTail);
        var finalTasks = TaskGraphProjection.Replay(codecs, finalTail);
        var finalLanes = LaneProjection.Replay(codecs, finalTail);
        var rootLane = rootTask is null ? null : finalLanes.ForTask(rootTask).FirstOrDefault()?.Id;

        stream.Append(new RunValidationStarted(_run.Id));

        var coveredEdits = PostEditValidationProjection.Pending(_run.Id, codecs, store.ReadFrom(sessionId, 1));
        var coveredMemory = mutationLedger?.PendingValidations().ToArray()
            ?? Array.Empty<PendingEditValidation>();

        // Host-owned gates execute only after validation starts. They return evidence, never
        // authorization; process effects must already have crossed the normal Security pipeline.
        var externalResults = runExternalGates?.Invoke() ?? Array.Empty<ExternalCompletionGateResult>();
        var successfulValidation = externalResults.FirstOrDefault(result => result.Passed && result.Key is "build" or "test");
        if (successfulValidation is not null)
        {
            // runExternalGates se ejecuta después de la última actividad del modelo en este intento:
            // un Build/Test que pasa valida las ediciones pendientes. Las ediciones de Turns
            // posteriores volverán a añadirse al ledger y exigirán otro gate.
            if (coveredEdits.Count > 0)
                stream.Append(new PostEditValidationConsumed(_run.Id, coveredEdits, successfulValidation.Key),
                    DurabilityClass.Barrier);
            mutationLedger?.ConsumePendingValidations(coveredMemory);
        }

        var failedExternal = externalResults.Where(result => !result.Passed).ToList();
        // Un Build/Test fallido sobre ediciones publicadas es una rotura atribuible al modelo
        // (FileMutationReliability, ADR-0044 §4); solo cuenta lo que la política exigió validar.
        if (failedExternal.Any(result => result.Key is "build" or "test"))
            mutationLedger?.RecordValidationBreak(coveredMemory);
        if (mutationLedger?.PendingValidations() is { Count: > 0 }
            || PostEditValidationProjection.Pending(_run.Id, codecs, store.ReadFrom(sessionId, 1)).Count > 0)
        {
            failedExternal.Add(new ExternalCompletionGateResult("post-edit-validation", false,
                LocalizedText.Of("coder.postEditValidation.required").Render()));
        }
        var pendingExternal = failedExternal.Where(result => result.PendingInteraction is not null).ToArray();

        var pendingResult = new PendingTaskGate().Check(finalTasks, finalPlan, rootTask);

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

        // El Plan v1 crea un único item raíz que representa el objetivo del Run. Cuando ese
        // objetivo no se descompuso en hojas ni tiene Tasks vinculadas, una propuesta de fin
        // aceptada por todos los gates permite al PlanService cerrarlo explícitamente.
        if (pendingResult.Passed && laneMissing.Count == 0 && failedExternal.Count == 0)
        {
            finalPlan = CompleteUnlinkedRunObjective(finalPlan, finalTasks, finalLanes, stream, planService, codecs);
        }
        var gateResult = new PlanCompletionGate().Check(finalPlan);

        if (!pendingResult.Passed || !gateResult.Passed || laneMissing.Count > 0 || failedExternal.Count > 0)
        {
            var gates = new List<string>();
            var missing = new List<string>();
            foreach (var pending in pendingExternal)
            {
                stream.Append(pending.PendingInteraction!);
            }
            var outputArtifacts = new List<ArtifactRef>();
            foreach (var failed in failedExternal)
            {
                gates.Add(failed.Key);
                if (failed.OutputArtifact is not null) outputArtifacts.Add(failed.OutputArtifact);
                var evidence = failed.OutputArtifact is null ? "" : " [artifact:"
                    + failed.OutputArtifact.Hash + "]";
                missing.Add(failed.Key == "post-edit-validation"
                    ? failed.Summary : failed.Key + ": " + failed.Summary + evidence);
            }
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

            stream.Append(new RunValidationRejected(_run.Id, gates, missing, outputArtifacts));
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

    private PlanProjection CompleteUnlinkedRunObjective(PlanProjection plan, TaskGraphProjection tasks,
        LaneProjection lanes, EventStream stream, PlanService planService, IEventCodecRegistry codecs)
    {
        if (string.IsNullOrWhiteSpace(_run.Objective)) return plan;
        var candidate = PlanCompletionGate.Leaves(plan).FirstOrDefault(item => item.ParentId is null
            && item.Description.Equals(_run.Objective, StringComparison.Ordinal)
            && item.LinkedTasks.Count == 0
            && item.State is PlanItemState.Pending or PlanItemState.Ready or PlanItemState.InProgress);
        if (candidate is null) return plan;

        if (candidate.State is PlanItemState.Pending or PlanItemState.Ready)
        {
            var start = planService.Apply(plan, tasks, lanes,
                PlanMutation.Start(candidate.Id, MutationCause.Policy));
            if (!start.Accepted) return plan;
            stream.AppendBatch(start.Events, DurabilityClass.Standard);
            var events = RunEvents(codecs, stream.EventsSince(1));
            plan = PlanProjection.Replay(codecs, events);
            tasks = TaskGraphProjection.Replay(codecs, events);
            lanes = LaneProjection.Replay(codecs, events);
        }

        var complete = planService.Apply(plan, tasks, lanes,
            PlanMutation.Complete(candidate.Id, MutationCause.Policy, "Run completion gates passed"));
        if (complete.Accepted) stream.AppendBatch(complete.Events, DurabilityClass.Standard);
        return PlanProjection.Replay(codecs, RunEvents(codecs, stream.EventsSince(1)));
    }

    // Projections participating in one Run's completion must not reconcile or close entities
    // from another sequential Run in the same Session. Session sequence remains global; the
    // correlated event subsequence is the canonical boundary for this evaluation.
    private IReadOnlyList<DomainEvent> RunEvents(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events) =>
        events.Where(evt => evt.CorrelationId?.Equals(_run.Id) == true).ToArray();

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
