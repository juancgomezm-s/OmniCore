using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    /// <summary>Trusted-user admission of bounded, read-only work to the durable queue.
    /// No child execution, route selection, capacity claim, join or result acceptance.</summary>
    private CommandAck CreateQueuedDelegation(WireEnvelope command, bool trustedUserAction,
        CancellationToken cancellationToken, WorkflowRuntimeAuthorization? workflowAuthorization = null)
    {
        if (!trustedUserAction && workflowAuthorization is null)
            return new(command.MessageId, "error", "Delegation requires a trusted user action", RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new(command.MessageId, "error", "No active Run", RuntimeCommandOutcome.Rejected());
        long? before = null;
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        CommandAck Deferred(string reason) => CommandOutcomeAck(command.MessageId, "ok", null,
            RuntimeCommandOutcome.Deferred(reason), session, before!.Value, commandId);
        try
        {
            before = _store.CurrentSequence(session);
            using var document = JsonDocument.Parse(command.PayloadJson);
            var request = AgentsJson.DecodeRequest(document.RootElement.GetProperty("request").GetRawText())
                ?? throw new ArgumentException("Delegation request is missing.");
            if (!Guid.TryParse(request.ProfileId, out var profileId) || profileId == Guid.Empty
                || !Guid.TryParse(request.SourceEventId, out var sourceId) || sourceId == Guid.Empty
                || string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 4096
                || request.SelectedItemIds is null || request.MaxTurns <= 0 || request.MaxToolCalls <= 0
                || request.MaxTokens <= 0 || request.MaxCostUsd < 0
                || request.Priority is < -1000 or > 1000
                || request.MaximumPacketBytes is <= 0 or > ContextInheritanceService.MaximumDelegationPacketBytes)
                throw new ArgumentException("Explicit profile, source, objective and finite budgets are required.");
            var selection = new ContextInheritancePolicy(request.SelectedItemIds);
            lock (_modeAuthorityMutationGate)
            {
                // Lock order: mode authority -> run budget admission -> EventStream writer.
                // Explorer holds only the latter two for its short reservation/start boundary.
                using var budgetAdmission = RunBudgetPool.For(_store).EnterAdmission(session, run);
                cancellationToken.ThrowIfCancellationRequested();
                if (workflowAuthorization is not null
                    && (workflowAuthorization.Session != session || workflowAuthorization.Run != run
                        || !WorkflowAuthorizationIsCurrent(workflowAuthorization)))
                    return Deferred("WorkflowAuthorizationUnavailable");
                if (_lastSessionId != session || _lastRunId != run
                    || new RunControlService(_store, _codecs).ActiveRun(session) != run)
                    return Deferred("RunNotActive");
                var journal = _store.ReadFrom(session, 1);
                if (journal.Any(e => e.Causation is CommandCausation cause && cause.CommandId == commandId))
                    throw new ArgumentException("Command identity already recorded; inspect /agents before retrying.");
                _ = PreM6RecordProjection.Replay(session, _codecs, journal);
                var own = journal.Where(e => e.RunId == run).ToArray();
                var projection = RunProjection.Replay(session, run, _codecs, own);
                var authority = projection.ModeAuthority;
                if (authority?.Mode != RunMode.Orchestrate) return Deferred("DelegationRequiresOrq");
                if (authority.Authorization is not { } authorization
                    || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    return Deferred("CoordinationLimitsUnavailable");
                if (projection.State != RunState.Running) return Deferred("RunNotRunning");
                if (_artifacts is not IArtifactPublicationLease publications) return Deferred("PacketPublicationUnavailable");
                var source = own.SingleOrDefault(e => e.EventId.Value == sourceId)
                    ?? throw new ArgumentException("Source is not in the current Run.");
                if (_codecs.Decode(source) is not ModelStepStarted { ContextSnapshotRef: { } snapshot }
                    || source.ExecutionId is not { } parentId || source.LaneId is not { } parentLane
                    || source.TaskId != projection.RootTask)
                    throw new ArgumentException("Source must identify an attributed root context snapshot.");
                var parent = own.Select(_codecs.Decode).OfType<AgentExecutionStarted>()
                    .FirstOrDefault(e => e.ExecutionId == parentId);
                if (parent is null || parent.ParentExecutionId is not null || parent.LaneId != parentLane)
                    throw new ArgumentException("Source parent is not the root executor.");
                // A child may be admitted while reader siblings are active, but the supervisor's
                // own provider/tool boundary must be settled before it forks more work.
                if (HasOpenModelStep(own, parentLane) || HasOpenToolCall(own, parentLane))
                    return Deferred("ExecutionBoundaryRequired");
                if (own.Select(_codecs.Decode).Any(e => e is AgentExecutionCompleted c && c.ExecutionId == parentId
                    || e is AgentExecutionFailed f && f.ExecutionId == parentId)) return Deferred("ParentExecutionTerminal");
                var parentProfile = ResolveLaneAgentProfile(session, run, parentLane);
                var profile = _agentProfiles.Registry.Find(new ProfileId(profileId));
                if (parentProfile is null || profile is null) return Deferred("ConfiguredAgentProfileRequired");
                if (!AgentPermissionScopeSubset.IsSubset(profile.PermissionCeiling, parentProfile.PermissionCeiling))
                    return Deferred("ChildPermissionCeilingExceedsParent");
                var payloads = own.Select(_codecs.Decode).ToArray();
                var children = payloads.OfType<TaskCreated>().Where(t => t.ParentTaskId is not null).ToArray();
                var limits = authorization.Limits;
                var allocations = RunBudgetPool.For(_store).ReadChildAllocations(journal, _codecs,
                    _artifacts, _store, null, session, run);
                // Existing children hold only their unspent ceilings. Completed/cancelled
                // executions release the unused balance on replay; their actual receipts remain
                // in the canonical totals above.
                // Capacity is scheduler state, not an admission rejection. Bounded work may
                // queue durably and will wait for an actual slot; only impossible root+child
                // capacity and depth are rejected here.
                if (limits.MaxDepth < 1 || limits.MaxAgents < 2)
                    return Deferred("AgentOrDepthLimit");
                if (children.Any(t => t.Budget.MaxTurns is null || t.Budget.MaxToolCalls is null || t.Budget.MaxCostUsd is null))
                    return Deferred("ChildBudgetAccountingUnavailable");
                var spendReader = new CanonicalSpendReader(_codecs, _artifacts);
                var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var primary = spendReader.ReadPrimary(own, session, run, day);
                var meta = spendReader.ReadMeta(own, session, run, day);
                if (primary.Incomplete || meta.Incomplete || primary.RunUsd is null || meta.RunUsd is null)
                    return Deferred("SpendAccountingUnavailable");
                var spent = primary.RunUsd.Value + meta.RunUsd.Value;
                if ((long)request.MaxTurns + allocations.Turns
                        + payloads.OfType<TurnStarted>().LongCount() > limits.MaxTurns
                    || (long)request.MaxToolCalls + allocations.ToolCalls
                        + payloads.OfType<ToolCallRequested>().LongCount() > limits.MaxToolCalls
                    || request.MaxCostUsd + allocations.CostUsd
                        + spent > limits.MaxSpendUsd)
                    return Deferred("CoordinationBudgetLimit");
                var rootBudget = payloads.OfType<RunCreated>().Single().Budget;
                if (rootBudget.MaxTokens is { } tokens)
                {
                    var tokenBudget = RunTokenBudgetReader.Read(own, _codecs, run, tokens);
                    if (tokenBudget.Remaining is null || children.Any(t => t.Budget.MaxTokens is null))
                        return Deferred("TokenAccountingUnavailable");
                    if (checked(request.MaxTokens + allocations.Tokens) > tokenBudget.Remaining)
                        return Deferred("ParentTokenBudgetLimit");
                }
                if (rootBudget.MaxCostUsd is { } cost && request.MaxCostUsd + allocations.CostUsd
                    + spent > cost) return Deferred("ParentCostBudgetLimit");
                if (rootBudget.MaxTurns is { } turns && (long)request.MaxTurns + allocations.Turns
                    + payloads.OfType<TurnStarted>().LongCount() > turns) return Deferred("ParentTurnBudgetLimit");
                if (rootBudget.MaxToolCalls is { } calls && (long)request.MaxToolCalls + allocations.ToolCalls
                    + payloads.OfType<ToolCallRequested>().LongCount() > calls) return Deferred("ParentToolBudgetLimit");
                var childTask = new TaskCreated(TaskId.New(), run, request.Objective, Array.Empty<TaskDependency>(),
                    new TaskBudget(request.MaxCostUsd, request.MaxTokens, request.MaxTurns, request.MaxToolCalls), projection.RootTask);
                var childLane = new LaneCreated(LaneId.New(), childTask.TaskId, profile.Id,
                    profile.Revision, AgentProfileFingerprint.Hash(profile));
                var target = new DelegationContextTarget(DelegationId.New(), parentId, childLane.LaneId,
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                var prepared = new ContextInheritanceService(_store, _codecs, _artifacts)
                    .PrepareNewChildPacket(session, run, target, snapshot, selection,
                        request.MaximumPacketBytes, cancellationToken, childTask, childLane);
                cancellationToken.ThrowIfCancellationRequested();
                // Publish and root under one lease; admission events are one atomic Barrier batch.
                if (authorization.IsExpiredAt(DateTimeOffset.UtcNow)) return Deferred("CoordinationLimitsExpired");
                using var lease = publications.AcquirePublicationLease(cancellationToken);
                if (prepared.Publish() != prepared.Reference) throw new InvalidDataException("Packet publication changed its reference.");
                var delegation = new Delegation(target.DelegationId, parentId, childTask.TaskId, childLane.LaneId,
                    profile.Id, prepared.Reference, target.Relation, target.Supervision, request.Priority);
                var childScope = new ExecutionScopeState(run, childTask.TaskId, childLane.LaneId);
                var parentScope = new ExecutionScopeState(run, projection.RootTask, parentLane, ExecutionId: parentId);
                var admissionEvents = new List<DomainEventPayload> {
                    childTask, new TaskReady(childTask.TaskId), childLane, new DelegationCreated(parentId, delegation),
                };
                if (workflowAuthorization is not null)
                {
                    var planEvents = BuildWorkflowPlanAdmissionEvents(workflowAuthorization, childTask, childLane,
                        request.Objective, payloads, own, session, run);
                    admissionEvents.AddRange(planEvents);
                }
                var eventScopes = admissionEvents.Select(evt => evt is DelegationCreated
                    ? (ExecutionScopeState?)parentScope : childScope).ToArray();
                new EventStream(_store, _codecs, session).AppendBatch(admissionEvents,
                    DurabilityClass.Barrier, eventScopes);
                return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value, failure.Message, restoreRunIdentity: false);
        }
    }

    private IReadOnlyList<DomainEventPayload> BuildWorkflowPlanAdmissionEvents(
        WorkflowRuntimeAuthorization authorization, TaskCreated task, LaneCreated lane, string objective,
        IReadOnlyList<DomainEventPayload> existingPayloads, IReadOnlyList<DomainEvent> existingEvents,
        SessionId session, RunId run)
    {
        if (authorization.AllowedStage is not { } stage || authorization.Session != session
            || authorization.Run != run || authorization.Instance.Length != 64
            || !authorization.Instance.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Workflow stage admission is not scoped to the current compiled request.");
        var definition = CompiledWorkflowCatalog.Stage(stage);
        if (!string.Equals(objective, WorkflowStageObjective(authorization, definition),
            StringComparison.Ordinal))
            throw new InvalidOperationException("Workflow stage objective differs from its compiled definition.");
        var plan = PlanProjection.Replay(_codecs, existingEvents);
        var current = plan.Latest() ?? throw new InvalidDataException("Workflow admission requires the durable Run Plan.");
        var root = plan.Items().Single(item => item.ParentId is null);
        WorkflowPriorResult? priorResult = null;
        PlanItemId[] dependencies = authorization.DependsOnPlanItem is { } dependency ? [dependency] : [];
        if (stage == WorkflowStage.Explore && dependencies.Length != 0
            || stage is WorkflowStage.Implement or WorkflowStage.Verify && dependencies.Length != 1)
            throw new InvalidOperationException("Workflow dependency does not match the compiled stage order.");
        foreach (var id in dependencies)
        {
            var dependencyItem = plan.Item(id) ?? throw new InvalidOperationException("Workflow stage dependency is absent from the Plan.");
            if (dependencyItem.State != PlanItemState.Completed
                || !WorkflowStageContract.TryFromPlanItem(dependencyItem, out var priorContract)
                || priorContract!.Instance != authorization.Instance
                || !WorkflowStageContract.IsCanonicalAdmission(priorContract, dependencyItem, session, run,
                    existingEvents, _codecs, _artifacts))
                throw new InvalidOperationException("Workflow stage dependency has not been reconciled to Completed.");
            priorResult = WorkflowStageContract.ReadAcceptedPriorResult(dependencyItem, priorContract,
                session, run, existingEvents, _codecs, _artifacts)
                ?? throw new InvalidOperationException("Workflow stage dependency has no accepted result projection.");
        }
        var metadata = definition.Metadata(authorization.Request.Workflow, authorization.Instance,
            task.TaskId, lane.LaneId, authorization.Specification.Objective, lane.AgentProfile.ToString(),
            authorization.Specification.Executable, authorization.Specification.ArgvJson,
            authorization.Specification.WorkingDirectory,
            authorization.MinimumSequenceExclusive, priorResult?.Event.EventId.ToString(),
            priorResult?.Result.ResultRef.Id.ToString());
        var nextOrder = plan.Items().Count == 0 ? 1 : plan.Items().Max(item => item.Order) + 1;
        var planService = new PlanService();
        var taskGraph = TaskGraphProjection.FromPayloads(existingPayloads.Concat<DomainEventPayload>([
            task, new TaskReady(task.TaskId), lane]));
        var lanes = LaneProjection.FromPayloads(existingPayloads.Concat<DomainEventPayload>([
            task, new TaskReady(task.TaskId), lane]));
        var add = planService.Apply(plan, taskGraph, lanes,
            PlanMutation.Add(nextOrder, definition.Name + ": " + authorization.Specification.Objective,
                root.Id, dependencies, MutationCause.User, metadata));
        if (!add.Accepted) throw new InvalidOperationException("Workflow Plan stage could not be added: " + add.Reason);
        var output = add.Events.ToList();
        var itemId = output.OfType<PlanItemAdded>().Single().PlanItemId;
        var projectedPayloads = existingPayloads.Concat<DomainEventPayload>([
            task, new TaskReady(task.TaskId), lane]).Concat(output).ToArray();
        plan = PlanProjection.FromPayloads(projectedPayloads);
        taskGraph = TaskGraphProjection.FromPayloads(projectedPayloads);
        lanes = LaneProjection.FromPayloads(projectedPayloads);
        var link = planService.Apply(plan, taskGraph, lanes,
            PlanMutation.Link(itemId, task.TaskId, LinkRole.Implements, true, MutationCause.User));
        if (!link.Accepted) throw new InvalidOperationException("Workflow Plan stage could not link its Task: " + link.Reason);
        output.AddRange(link.Events);
        projectedPayloads = projectedPayloads.Concat(link.Events).ToArray();
        plan = PlanProjection.FromPayloads(projectedPayloads);
        taskGraph = TaskGraphProjection.FromPayloads(projectedPayloads);
        lanes = LaneProjection.FromPayloads(projectedPayloads);
        var ready = planService.Apply(plan, taskGraph, lanes, PlanMutation.Ready(itemId, MutationCause.Reconciler));
        if (!ready.Accepted) throw new InvalidOperationException("Workflow stage is not ready: " + ready.Reason);
        output.AddRange(ready.Events);
        return output;
    }
}
