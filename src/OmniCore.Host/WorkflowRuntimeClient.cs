namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Execution;
using OmniCore.Tools;

internal sealed record WorkflowInvocation(string CommandId, WorkflowRequested Request,
    SessionId? Session, RunId? Run);

/// <summary>Non-serializable capability minted only for a just-requested compiled workflow.</summary>
internal sealed record WorkflowRuntimeAuthorization(SessionId Session, RunId Run, CommandId InvocationCommand,
    WorkflowRequested Request, Guid AuthorizationId, long AuthorityRevision,
    WorkflowRequestSpecification Specification, string Instance, WorkflowStage? AllowedStage = null,
    PlanItemId? DependsOnPlanItem = null, long MinimumSequenceExclusive = 0);

internal sealed record WorkflowRunContext(SessionId Session, RunId Run, TaskId RootTask, LaneId RootLane,
    ExecutionId RootExecution, EventId RootSnapshotEvent, AgentProfile RootProfile, PlanItemId RootPlanItem,
    ModeSwitchAuthorization Authorization, IReadOnlyList<DomainEvent> Journal);

/// <summary>
/// The workflow runner's ordinary IOmniClient view. It can queue bounded delegated work only;
/// it has no trusted-user marker and every send revalidates the original workflow request.
/// </summary>
internal sealed class WorkflowScopedClient(OmniServer server, WorkflowRuntimeAuthorization authorization)
    : IOmniClient
{
    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
    {
        if (!server.WorkflowAuthorizationIsCurrent(authorization))
            return new(command.MessageId, "error", "Workflow authorization is no longer current.",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
            var name = document.RootElement.TryGetProperty("cmd", out var value) ? value.GetString() : null;
            if (name != "delegation.create")
                return new(command.MessageId, "error", "Workflow client cannot issue this command.",
                    RuntimeCommandOutcome.Rejected());
            var request = document.RootElement.TryGetProperty("request", out var requestValue)
                ? AgentsJson.DecodeRequest(requestValue.GetRawText()) : null;
            if (request is null || !server.WorkflowDelegationIsAuthorized(authorization, request))
                return new(command.MessageId, "error", "Workflow delegation does not match its current compiled stage.",
                    RuntimeCommandOutcome.Rejected());
        }
        catch (System.Text.Json.JsonException)
        {
            return new(command.MessageId, "error", "Invalid workflow command.", RuntimeCommandOutcome.Rejected());
        }
        return server.SendWorkflowCommand(command, authorization, cancellationToken);
    }

    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) =>
        server.SubscribeWorkflowSince(authorization, fromSequence);

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken) =>
        server.QueryWorkflow(authorization, name, cancellationToken);
}

public sealed partial class OmniServer
{
    private const string WorkflowGateIdKey = "workflow.gate.workflow";
    private const string WorkflowGateInstanceKey = "workflow.gate.instance";

    internal void EnsureWorkflowCompletionGate(WorkflowRuntimeAuthorization authorization)
    {
        lock (_modeAuthorityMutationGate)
        {
            if (!WorkflowAuthorizationIsCurrent(authorization))
                throw new InvalidOperationException("Workflow authorization is no longer current.");
            var journal = _store.ReadFrom(authorization.Session, 1);
            var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
            var plan = PlanProjection.Replay(_codecs, own);
            var gate = plan.Items().Where(item => item.Metadata.GetValueOrDefault(WorkflowGateIdKey)
                    == authorization.Request.Workflow.ToString()
                && item.Metadata.GetValueOrDefault(WorkflowGateInstanceKey) == authorization.Instance).ToArray();
            if (gate.Length > 1) throw new InvalidDataException("Workflow has duplicate completion gates.");
            if (gate.Length == 1) return;

            var context = ReadWorkflowContext(authorization);
            var root = plan.Items().Single(item => item.ParentId is null);
            var order = plan.Items().Count == 0 ? 1 : plan.Items().Max(item => item.Order) + 1;
            var mutation = PlanMutation.Add(order, "Workflow completion gate: " + authorization.Specification.Objective,
                root.Id, Array.Empty<PlanItemId>(), MutationCause.Policy,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WorkflowGateIdKey] = authorization.Request.Workflow.ToString(),
                    [WorkflowGateInstanceKey] = authorization.Instance,
                });
            var result = new PlanService().Apply(plan, TaskGraphProjection.Replay(_codecs, own),
                LaneProjection.Replay(_codecs, own), mutation);
            if (!result.Accepted || result.Events.Count == 0)
                throw new InvalidOperationException("Workflow completion gate could not be persisted: " + result.Reason);
            var scopes = Enumerable.Repeat<ExecutionScopeState?>(new ExecutionScopeState(authorization.Run,
                context.RootTask, context.RootLane, ExecutionId: context.RootExecution), result.Events.Count).ToArray();
            var causes = Enumerable.Repeat<CausationId?>(new CommandCausation(authorization.InvocationCommand),
                result.Events.Count).ToArray();
            new EventStream(_store, _codecs, authorization.Session).AppendBatch(result.Events,
                DurabilityClass.Barrier, scopes, causes);
        }
    }

    internal void CompleteWorkflowCompletionGate(WorkflowRuntimeAuthorization authorization)
    {
        lock (_modeAuthorityMutationGate)
        {
            if (!WorkflowAuthorizationIsCurrent(authorization))
                throw new InvalidOperationException("Workflow authorization is no longer current.");
            var journal = _store.ReadFrom(authorization.Session, 1);
            var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
            var plan = PlanProjection.Replay(_codecs, own);
            var gate = plan.Items().SingleOrDefault(item => item.Metadata.GetValueOrDefault(WorkflowGateIdKey)
                == authorization.Request.Workflow.ToString()
                && item.Metadata.GetValueOrDefault(WorkflowGateInstanceKey) == authorization.Instance)
                ?? throw new InvalidDataException("Workflow completion gate is missing.");
            if (gate.State == PlanItemState.Completed) return;

            var stageItems = plan.Items().Select(item => (Item: item,
                    Valid: WorkflowStageContract.TryFromPlanItem(item, out var contract), Contract: contract))
                .Where(value => value.Valid && value.Contract!.Workflow == authorization.Request.Workflow
                    && value.Contract.Instance == authorization.Instance).ToArray();
            if (stageItems.Length != Enum.GetValues<WorkflowStage>().Length
                || stageItems.Select(value => value.Contract!.Definition.Stage).Distinct().Count() != stageItems.Length)
                throw new InvalidOperationException("Workflow gate cannot close before the three unique compiled stages are present.");
            foreach (var value in stageItems)
            {
                var item = value.Item;
                var contract = value.Contract!;
                if (item.State != PlanItemState.Completed
                    || !WorkflowStageContract.IsCanonicalAdmission(contract, item, authorization.Session,
                        authorization.Run, own, _codecs, _artifacts)
                    || contract.Execution is not { } execution)
                    throw new InvalidOperationException("Workflow gate cannot close before every stage is durably accepted and reconciled.");
                var produced = own.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
                    .Where(pair => pair.Event.ExecutionId == execution && pair.Payload is AgentResultProduced)
                    .Select(pair => (pair.Event, Result: (AgentResultProduced)pair.Payload)).ToArray();
                if (produced.Length != 1) throw new InvalidDataException("Workflow stage result is missing or ambiguous.");
                var disposition = own.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
                    .Where(pair => pair.Event.ExecutionId == execution && pair.Payload is ResultDispositionRecorded)
                    .Select(pair => (pair.Event, Value: ((ResultDispositionRecorded)pair.Payload).Disposition))
                    .Where(pair => pair.Value.ResultRef.Id == produced[0].Result.ResultRef.Id).ToArray();
                if (disposition.Length != 1 || disposition[0].Value.Outcome != ResultDispositionOutcome.Accepted)
                    throw new InvalidOperationException("Workflow gate cannot close without one exact Accepted disposition for each stage result.");
                var evidence = WorkflowStageEvidenceEvaluator.Evaluate(contract, authorization.Session,
                    authorization.Run, contract.Task, contract.Lane, execution, own, _codecs);
                if (!evidence.Passed)
                    throw new InvalidOperationException("Workflow gate remains open: " + evidence.Reason);
            }

            var tasks = TaskGraphProjection.Replay(_codecs, own);
            var lanes = LaneProjection.Replay(_codecs, own);
            var events = new List<DomainEventPayload>();
            var projected = own.Select(_codecs.Decode).Cast<DomainEventPayload>().ToList();
            var service = new PlanService();
            foreach (var mutation in new[]
                {
                    PlanMutation.Ready(gate.Id, MutationCause.Policy),
                    PlanMutation.Start(gate.Id, MutationCause.Policy),
                    PlanMutation.Complete(gate.Id, MutationCause.Policy,
                        "Explore, Implement, and Verify accepted with current integration evidence."),
                })
            {
                var current = PlanProjection.FromPayloads(projected);
                var transition = service.Apply(current, tasks, lanes, mutation);
                if (!transition.Accepted || transition.Events.Count == 0)
                    throw new InvalidOperationException("Workflow completion gate transition was rejected: " + transition.Reason);
                events.AddRange(transition.Events);
                projected.AddRange(transition.Events);
            }
            var context = ReadWorkflowContext(authorization);
            var scopes = Enumerable.Repeat<ExecutionScopeState?>(new ExecutionScopeState(authorization.Run,
                context.RootTask, context.RootLane, ExecutionId: context.RootExecution), events.Count).ToArray();
            var causes = Enumerable.Repeat<CausationId?>(new CommandCausation(authorization.InvocationCommand),
                events.Count).ToArray();
            new EventStream(_store, _codecs, authorization.Session).AppendBatch(events,
                DurabilityClass.Barrier, scopes, causes);
        }
    }

    internal void ReconcileWorkflowPlanProgress(SessionId session, RunId run)
    {
        lock (_modeAuthorityMutationGate)
        {
            var stream = new EventStream(_store, _codecs, session);
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var journal = _store.ReadFrom(session, 1);
                var own = journal.Where(evt => evt.RunId == run).ToArray();
                var plan = PlanProjection.Replay(_codecs, own);
                var tasks = TaskGraphProjection.Replay(_codecs, own);
                var lanes = LaneProjection.Replay(_codecs, own);
                var mutation = new ProgressReconciler().Reconcile(plan, tasks, lanes)
                    .FirstOrDefault(value => (value.Kind is PlanMutationKind.Complete or PlanMutationKind.Block
                        or PlanMutationKind.Fail or PlanMutationKind.Ready or PlanMutationKind.Start
                        or PlanMutationKind.Unblock)
                        && value.ItemId is { } itemId
                        && plan.Item(itemId)?.Metadata.ContainsKey(WorkflowStageContract.WorkflowIdKey) == true);
                if (mutation is null) return;
                var result = new PlanService().Apply(plan, tasks, lanes, mutation);
                if (!result.Accepted || result.Events.Count == 0)
                    throw new InvalidOperationException("Workflow Plan reconciliation was rejected: " + result.Reason);
                var item = mutation.ItemId is { } id ? plan.Item(id) : null;
                var taskId = item?.LinkedTasks.FirstOrDefault(link => link.Required)?.TaskId;
                var trigger = taskId is null ? null : own.LastOrDefault(evt => evt.TaskId == taskId
                    && _codecs.Decode(evt) is TaskCompleted or TaskStarted or TaskReady);
                var cause = trigger is null ? null : new EventCausation(trigger.EventId);
                var scopes = Enumerable.Repeat<ExecutionScopeState?>(new ExecutionScopeState(run), result.Events.Count).ToArray();
                var causes = Enumerable.Repeat<CausationId?>(cause, result.Events.Count).ToArray();
                stream.AppendBatch(result.Events, DurabilityClass.Barrier, scopes, causes);
            }
            throw new InvalidOperationException("Workflow Plan reconciliation exceeded its deterministic event bound.");
        }
    }

    internal IReadOnlyList<DomainEventPayload> BindWorkflowStageExecution(SessionId session, RunId run,
        TaskId task, LaneId lane, ExecutionId execution)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var plan = PlanProjection.Replay(_codecs, own);
        var item = plan.Items().SingleOrDefault(candidate => candidate.LinkedTasks.Any(link => link.TaskId == task));
        if (item is null || !WorkflowStageContract.TryFromPlanItem(item, out var contract)) return [];
        if (contract!.Task != task || contract.Lane != lane || contract.Execution is not null
            || !WorkflowStageContract.IsCanonicalAdmission(contract, item, session, run, own, _codecs, _artifacts))
            throw new InvalidDataException("Workflow Task/Lane admission is invalid or already bound.");
        var updatedMetadata = item.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        updatedMetadata[WorkflowStageContract.ExecutionKey] = execution.ToString();
        var events = new List<DomainEventPayload>();
        var tasks = TaskGraphProjection.Replay(_codecs, own);
        var lanes = LaneProjection.Replay(_codecs, own);
        var service = new PlanService();
        var update = service.Apply(plan, tasks, lanes,
            PlanMutation.Update(item.Id, MutationCause.Policy, item.Description, updatedMetadata));
        if (!update.Accepted) throw new InvalidOperationException("Workflow execution binding was rejected: " + update.Reason);
        events.AddRange(update.Events);
        var projected = PlanProjection.FromPayloads(own.Select(_codecs.Decode).Concat(events));
        var start = service.Apply(projected, tasks, lanes, PlanMutation.Start(item.Id, MutationCause.Reconciler));
        if (!start.Accepted) throw new InvalidOperationException("Workflow Plan item could not enter progress: " + start.Reason);
        events.AddRange(start.Events);
        return events;
    }

    internal WorkflowStageContract? ReadWorkflowStageForTask(SessionId session, RunId run, TaskId task)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var item = PlanProjection.Replay(_codecs, own).Items()
            .SingleOrDefault(candidate => candidate.LinkedTasks.Any(link => link.TaskId == task));
        if (item is null || !WorkflowStageContract.TryFromPlanItem(item, out var contract)) return null;
        if (contract!.Task != task || !WorkflowStageContract.IsCanonicalAdmission(contract, item, session, run, own, _codecs, _artifacts))
            throw new InvalidDataException("Workflow stage is not backed by its compiled Plan and delegation admission.");
        return contract;
    }

    internal IReadOnlyList<WireEnvelope> SubscribeWorkflowSince(WorkflowRuntimeAuthorization authorization,
        long fromSequence)
    {
        if (!WorkflowAuthorizationIsCurrent(authorization)) return [];
        var events = _store.ReadFrom(authorization.Session, Math.Max(1, fromSequence))
            .Where(evt => evt.SessionId == authorization.Session && evt.RunId == authorization.Run).ToArray();
        return new ProtocolMapper(_codecs, _artifacts).Map(events);
    }

    internal SessionQueryResult? QueryWorkflow(WorkflowRuntimeAuthorization authorization, string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowAuthorizationIsCurrent(authorization)
            || name is not ("agents" or "workingState")) return null;
        return Query(name, cancellationToken);
    }

    internal bool WorkflowDelegationIsAuthorized(WorkflowRuntimeAuthorization authorization,
        DelegationCreateRequest request)
    {
        if (authorization.AllowedStage is not { } stage || !WorkflowAuthorizationIsCurrent(authorization)) return false;
        WorkflowStageDefinition definition;
        try { definition = CompiledWorkflowCatalog.Stage(stage); }
        catch (ArgumentOutOfRangeException) { return false; }
        string expectedObjective;
        try { expectedObjective = WorkflowStageObjective(authorization, definition); }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or KeyNotFoundException or ArgumentException) { return false; }
        if (!string.Equals(request.Objective, expectedObjective, StringComparison.Ordinal)
            || request.Priority != 0 || request.MaximumPacketBytes is <= 0 or > ContextInheritanceService.MaximumDelegationPacketBytes
            || request.MaxTurns != 1 || request.MaxToolCalls is < 1 or > 4
            || request.MaxTokens < 8_448 || request.MaxCostUsd < 0) return false;
        var context = ReadWorkflowContext(authorization);
        if (!Guid.TryParse(request.ProfileId, out var profileId) || profileId != context.RootProfile.Id.Value)
            return false;
        var ceiling = context.RootProfile.PermissionCeiling;
        if (stage == WorkflowStage.Explore && ceiling.Reads.Count == 0
            || stage == WorkflowStage.Implement && ceiling.Writes.Count == 0
            || stage == WorkflowStage.Verify && !CanRunDeclaredVerifier(context.RootProfile, authorization.Specification))
            return false;
        var journal = context.Journal.Where(evt => evt.RunId == context.Run).ToArray();
        var facts = journal.Select(_codecs.Decode).ToArray();
        var source = journal.Where(evt => evt.TaskId == context.RootTask && evt.LaneId == context.RootLane
                && evt.ExecutionId == context.RootExecution)
            .LastOrDefault(evt => _codecs.Decode(evt) is ModelStepStarted { ContextSnapshotRef: not null });
        if (source is null || source.EventId.ToString() != request.SourceEventId) return false;
        var duplicate = facts.OfType<TaskCreated>().Any(task => task.ParentTaskId == context.RootTask
            && string.Equals(task.Objective, expectedObjective, StringComparison.Ordinal));
        return !duplicate;
    }

    private bool CanRunDeclaredVerifier(AgentProfile profile, WorkflowRequestSpecification specification)
    {
        var arguments = "{\"executable\":\"" + JsonObj.Escape(specification.Executable)
            + "\",\"argv\":" + specification.ArgvJson + ",\"cwd\":\""
            + JsonObj.Escape(specification.WorkingDirectory) + "\",\"timeoutSeconds\":120}";
        var claims = new ResourceClaims(Array.Empty<string>(), new[] { specification.WorkingDirectory },
            Array.Empty<NetworkGrant>(), new ProcessClaim(specification.Executable, specification.Argv,
                "External", NetworkRequired: false, WorkingDirectory: specification.WorkingDirectory), Array.Empty<string>());
        var intent = new ToolIntent(ToolCallId.New(), new ToolId("dyn.core.verify_integration"), arguments,
            EffectClass.NonIdempotent, claims, ToolRisk.High, null);
        var policy = new AgentProfilePermissionPolicy(
            ScriptedPermissionPolicy.WithTool("dyn.core.verify_integration", PermissionDecision.Allow),
            profile, new PathBoundaryValidator(), _workspaceRoot ?? specification.WorkingDirectory);
        return policy.Evaluate(intent).Final == PermissionDecision.Allow;
    }

    internal static string WorkflowStageObjective(string objective, WorkflowStageDefinition stage,
        string? priorContext = null)
    {
        var text = stage.Name + ": " + objective + Environment.NewLine + stage.Instruction
            + (priorContext is null ? "" : Environment.NewLine + Environment.NewLine + priorContext);
        if (text.Length > 4096) throw new ArgumentException("Workflow stage input exceeds the bounded Task objective.");
        return text;
    }

    internal string WorkflowStageObjective(WorkflowRuntimeAuthorization authorization, WorkflowStageDefinition stage)
    {
        if (stage.Stage == WorkflowStage.Explore)
            return WorkflowStageObjective(authorization.Specification.Objective, stage);
        if (authorization.DependsOnPlanItem is not { } dependency)
            throw new InvalidOperationException("Workflow stage requires the prior accepted Plan item.");
        var journal = _store.ReadFrom(authorization.Session, 1);
        var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
        var plan = PlanProjection.Replay(_codecs, own);
        var previous = plan.Item(dependency)
            ?? throw new InvalidOperationException("Workflow predecessor Plan item is missing.");
        var expectedPreviousStage = stage.Stage switch
        {
            WorkflowStage.Implement => WorkflowStage.Explore,
            WorkflowStage.Verify => WorkflowStage.Implement,
            _ => throw new InvalidOperationException("Explore cannot inherit a predecessor stage."),
        };
        if (!WorkflowStageContract.TryFromPlanItem(previous, out var previousContract)
            || previousContract!.Workflow != authorization.Request.Workflow
            || previousContract.Instance != authorization.Instance
            || previousContract.Definition.Stage != expectedPreviousStage
            || previous.State != PlanItemState.Completed
            || !WorkflowStageContract.IsCanonicalAdmission(previousContract, previous,
                authorization.Session, authorization.Run, own, _codecs, _artifacts))
            throw new InvalidOperationException("Workflow predecessor is not a canonical accepted stage.");
        var priorResult = WorkflowStageContract.ReadAcceptedPriorResult(previous, previousContract,
            authorization.Session, authorization.Run, own, _codecs, _artifacts)
            ?? throw new InvalidOperationException("Workflow predecessor result is unavailable or unaccepted.");
        return WorkflowStageObjective(authorization.Specification.Objective, stage, priorResult.ContextText);
    }

    internal DelegationCreateRequest WorkflowDelegationRequest(WorkflowRuntimeAuthorization authorization,
        WorkflowStage stage, WorkflowRunContext context, IReadOnlyList<string> selectedItems,
        int maximumPacketBytes, long maxTokens, decimal maxCostUsd)
    {
        var definition = CompiledWorkflowCatalog.Stage(stage);
        return new DelegationCreateRequest(context.RootProfile.Id.ToString(),
            context.RootSnapshotEvent.ToString(),
            WorkflowStageObjective(authorization, definition), selectedItems,
            maximumPacketBytes, 1, 4, maxTokens, maxCostUsd, 0);
    }

    internal WorkflowRuntimeAuthorization ForStage(WorkflowRuntimeAuthorization authorization,
        WorkflowStage stage, PlanItemId? dependsOn, long minimumSequenceExclusive = 0) =>
        authorization with { AllowedStage = stage, DependsOnPlanItem = dependsOn,
            MinimumSequenceExclusive = minimumSequenceExclusive };

    internal (PlanItem Item, Delegation Delegation, PreM6RecordPhase Phase)? FindWorkflowStage(
        WorkflowRuntimeAuthorization authorization, WorkflowStage stage)
    {
        if (!WorkflowAuthorizationIsCurrent(authorization)) return null;
        var journal = _store.ReadFrom(authorization.Session, 1);
        var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
        var plan = PlanProjection.Replay(_codecs, own);
        var matching = plan.Items().Select(item => (Item: item,
                Contract: WorkflowStageContract.TryFromPlanItem(item, out var contract) ? contract : null))
            .Where(pair => pair.Contract is not null
                && pair.Contract.Workflow == authorization.Request.Workflow
                && pair.Contract.Instance == authorization.Instance
                && pair.Contract.Definition.Stage == stage).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Length != 1) throw new InvalidDataException("Workflow instance has duplicate stage contracts.");
        if (!WorkflowStageContract.IsCanonicalAdmission(matching[0].Contract!, matching[0].Item,
            authorization.Session, authorization.Run, own, _codecs, _artifacts))
            throw new InvalidDataException("Workflow Plan stage is not backed by its compiled delegation admission.");
        var taskId = matching[0].Contract!.Task;
        var records = PreM6RecordProjection.Replay(authorization.Session, _codecs, journal);
        var history = records.Records.Values.SingleOrDefault(record => record.Facts.OfType<DelegationCreated>()
            .Any(created => created.Delegation.ChildTaskId == taskId));
        if (history is null) throw new InvalidDataException("Workflow Plan stage has no canonical delegation admission.");
        var delegation = history.Facts.OfType<DelegationCreated>().Single().Delegation;
        return (matching[0].Item, delegation, history.Phase);
    }

    internal WorkflowRuntimeAuthorization AuthorizeWorkflowRequest(WorkflowRequested request, string commandId)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!Guid.TryParse(commandId, out var commandGuid)
            || _lastWorkflowInvocation is not { } invocation
            || !string.Equals(invocation.CommandId, commandId, StringComparison.Ordinal)
            || invocation.Session is not { } session || invocation.Run is not { } run
            || !SameWorkflowRequest(invocation.Request, request)
            || !CompiledWorkflowCatalog.TryResolve(request.Workflow, out _))
            throw new InvalidOperationException("Workflow request is not the current typed command outcome.");
        var journal = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, journal.Where(evt => evt.RunId == run).ToArray());
        var authority = projection.ModeAuthority;
        if (projection.State != RunState.Running || authority?.Mode != RunMode.Orchestrate
            || authority.Authorization is not { } granted
            || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Workflow requires an active Run with current Orchestrate authorization.");
        var specification = WorkflowRequestSpecification.Parse(request.Arguments, _workspaceRoot ?? Environment.CurrentDirectory);
        var instance = CompiledWorkflowCatalog.InstanceKey(run, request.Workflow, specification);
        return new(session, run, new CommandId(commandGuid), request, granted.AuthorizationId, authority.Revision,
            specification, instance);
    }

    internal bool WorkflowAuthorizationIsCurrent(WorkflowRuntimeAuthorization authorization)
    {
        if (_lastSessionId != authorization.Session || _lastRunId != authorization.Run
            || _lastWorkflowInvocation is not { } invocation
            || !string.Equals(invocation.CommandId, authorization.InvocationCommand.ToString(), StringComparison.Ordinal)
            || !SameWorkflowRequest(invocation.Request, authorization.Request)) return false;
        var journal = _store.ReadFrom(authorization.Session, 1);
        var projection = RunProjection.Replay(authorization.Session, authorization.Run, _codecs,
            journal.Where(evt => evt.RunId == authorization.Run).ToArray());
        var authority = projection.ModeAuthority;
        return projection.State == RunState.Running && authority?.Mode == RunMode.Orchestrate
            && authority.Authorization is { } grant && grant.AuthorizationId == authorization.AuthorizationId
            && authority.Revision == authorization.AuthorityRevision
            && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow);
    }

    internal WorkflowRunContext ReadWorkflowContext(WorkflowRuntimeAuthorization authorization)
    {
        lock (_modeAuthorityMutationGate)
        {
            if (!WorkflowAuthorizationIsCurrent(authorization))
                throw new InvalidOperationException("Workflow authorization is no longer current.");
            var journal = _store.ReadFrom(authorization.Session, 1);
            var own = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
            var projection = RunProjection.Replay(authorization.Session, authorization.Run, _codecs, own);
            var root = projection.RootTask
                ?? throw new InvalidDataException("Workflow Run has no root Task.");
            var facts = own.Select(_codecs.Decode).ToArray();
            var rootTask = facts.OfType<TaskCreated>().Single(item => item.TaskId == root);
            var rootLane = facts.OfType<LaneCreated>().Single(item => item.TaskId == root);
            var rootExecution = facts.OfType<AgentExecutionStarted>()
                .SingleOrDefault(item => item.LaneId == rootLane.LaneId && item.ParentExecutionId is null)
                ?? throw new InvalidOperationException("Workflow root execution is ambiguous or absent.");
            if (facts.Any(item => item is AgentExecutionCompleted completed && completed.ExecutionId == rootExecution.ExecutionId
                || item is AgentExecutionFailed failed && failed.ExecutionId == rootExecution.ExecutionId))
                throw new InvalidOperationException("Workflow root execution has already terminated.");
            if (HasOpenModelStep(own, rootLane.LaneId) || HasOpenToolCall(own, rootLane.LaneId))
                throw new InvalidOperationException("Workflow can start only at a settled root execution boundary.");
            var profile = ResolveLaneAgentProfile(authorization.Session, authorization.Run, rootLane.LaneId)
                ?? throw new InvalidOperationException("Workflow root AgentProfile is unavailable.");
            var rootSnapshot = own.LastOrDefault(evt => evt.TaskId == root && evt.LaneId == rootLane.LaneId
                && evt.ExecutionId == rootExecution.ExecutionId
                && _codecs.Decode(evt) is ModelStepStarted { ContextSnapshotRef: not null })
                ?? throw new InvalidOperationException("Workflow requires a durable root context snapshot.");
            var plan = PlanProjection.Replay(_codecs, own).Latest()
                ?? throw new InvalidOperationException("Workflow Run has no Plan.");
            var rootItem = plan.Items.Single(item => item.ParentId is null);
            return new(authorization.Session, authorization.Run, root, rootLane.LaneId,
                rootExecution.ExecutionId, rootSnapshot.EventId, profile, rootItem.Id, projection.ModeAuthority?.Authorization
                    ?? throw new InvalidDataException("Workflow authorization missing."), journal);
        }
    }

    internal IArtifactStore? AcquireArtifacts() => _artifacts;

    internal IOmniClient CreateWorkflowClient(WorkflowRuntimeAuthorization authorization) =>
        new WorkflowScopedClient(this, authorization);

    internal CommandAck SendWorkflowCommand(WireEnvelope command, WorkflowRuntimeAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (!WorkflowAuthorizationIsCurrent(authorization))
            return new(command.MessageId, "error", "Workflow authorization is no longer current.",
                RuntimeCommandOutcome.Rejected());
        return SafeCommandAck(SendCore(command, cancellationToken, trustedUserAction: false,
            workflowAuthorization: authorization));
    }

    private static bool SameWorkflowRequest(WorkflowRequested left, WorkflowRequested right) =>
        left.Workflow == right.Workflow && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal);
}
