using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Host;

internal sealed record DelegationWork(SessionId Session, RunId Run, Delegation Delegation,
    ExecutionScopeState Scope, AgentProfile Profile, string Objective, TaskBudget Budget,
    WorktreeIdentity? Worktree, TaskPacket? TaskPacket, string EffectiveRoot, string WorkingState);
internal enum DelegationWakeClaimKind { Claimed, AlreadyClaimed, Resolved, Failed, Unavailable }
internal sealed record DelegationWakeClaim(DelegationWakeClaimKind Kind, WakeRequest? Request = null,
    ArtifactRef? ResultRef = null, string? Objective = null);

public sealed partial class OmniServer
{
    private sealed record VerifiedImplementationSource(LaneWorktreeCreated OriginalEvent,
        WorktreeIdentity Identity, WorkflowImplementationBinding Binding);

    internal Delegation ReadCurrentDelegation(DelegationId id)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run) throw new InvalidOperationException("No active Run.");
        return _store.ReadFrom(session, 1).Where(e => e.RunId == run).Select(_codecs.Decode)
            .OfType<DelegationCreated>().Single(e => e.Delegation.DelegationId == id).Delegation;
    }

    internal ModelStepStarted? SourceModelStepForDelegation(Delegation delegation) =>
        DelegationSourceModelStep(_store.ReadFrom(_lastSessionId ?? throw new InvalidOperationException("No Session."), 1),
            delegation);

    internal DelegationWakeClaim ClaimDelegationReturnWake(DelegationId id)
    {
        var session = _lastSessionId ?? throw new InvalidOperationException("No active Session.");
        var run = _lastRunId ?? throw new InvalidOperationException("No active Run.");
        lock (_modeAuthorityMutationGate)
        {
            var journal = _store.ReadFrom(session, 1);
            var delegation = journal.Select(_codecs.Decode).OfType<DelegationCreated>()
                .SingleOrDefault(item => item.Delegation.DelegationId == id)?.Delegation;
            if (delegation is null) return new(DelegationWakeClaimKind.Unavailable);
            if (!journal.Any(evt => evt.RunId == run && _codecs.Decode(evt) is DelegationReturned item
                    && item.DelegationId == id))
                return new(DelegationWakeClaimKind.Unavailable);
            EnsureDelegationReturnWake(session, run, delegation, journal);
            journal = _store.ReadFrom(session, 1);
            var returned = journal.Where(evt => evt.RunId == run).Single(evt =>
                _codecs.Decode(evt) is DelegationReturned item && item.DelegationId == id);
            var resultRef = ((DelegationReturned)_codecs.Decode(returned)).ResultRef;
            var wakeEvent = journal.SingleOrDefault(evt => evt.RunId == run
                && _codecs.Decode(evt) is WakeRequestCreated created
                && created.Request.SourceEvent == new EvidenceEventRef(session, returned.EventId));
            if (wakeEvent is null) return new(DelegationWakeClaimKind.Unavailable);
            var request = ((WakeRequestCreated)_codecs.Decode(wakeEvent)).Request;
            var records = PreM6RecordProjection.Replay(session, _codecs, journal);
            var phase = records.Records["wake:" + request.WakeRequestId].Phase;
            var facts = journal.Where(evt => evt.RunId == run).Select(_codecs.Decode).ToArray();
            var rootTask = facts.OfType<TaskCreated>().SingleOrDefault(task => task.ParentTaskId is null);
            if (rootTask is null) return new(DelegationWakeClaimKind.Unavailable);
            var runProjection = RunProjection.Replay(session, run, _codecs,
                journal.Where(evt => evt.RunId == run).ToArray());
            if (runProjection.State != RunState.Running || runProjection.ModeAuthority?.Mode != RunMode.Orchestrate
                || runProjection.ModeAuthority.Authorization is not { } authorization
                || !runProjection.ModeAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)
                || authorization.IsExpiredAt(DateTimeOffset.UtcNow))
                return new(DelegationWakeClaimKind.Unavailable, request, resultRef, rootTask.Objective);
            if (phase == PreM6RecordPhase.Resolved)
                return new(DelegationWakeClaimKind.Resolved, request, resultRef, rootTask.Objective);
            if (phase == PreM6RecordPhase.Failed)
                return new(DelegationWakeClaimKind.Failed, request, resultRef, rootTask.Objective);
            if (phase == PreM6RecordPhase.Accepted)
                return new(DelegationWakeClaimKind.AlreadyClaimed, request, resultRef, rootTask.Objective);
            var source = DelegationSourceModelStep(journal, delegation);
            if (source is null || !SourceModelAllowsOrchestration(session, journal, source))
                return new(DelegationWakeClaimKind.Unavailable, request, resultRef, rootTask.Objective);
            var rootScope = AgentScope(run, facts, delegation.ParentExecutionId);
            using var attributed = ExecutionScope.Begin(rootScope);
            var createdEvent = journal.Single(evt => _codecs.Decode(evt) is WakeRequestCreated created
                && created.Request.WakeRequestId == request.WakeRequestId);
            new EventStream(_store, _codecs, session).AppendBatch(
                [new WakeRequestAccepted(delegation.ParentExecutionId, request.WakeRequestId)],
                DurabilityClass.Barrier, [rootScope], [new EventCausation(createdEvent.EventId)]);
            return new(DelegationWakeClaimKind.Claimed, request, resultRef, rootTask.Objective);
        }
    }

    internal void CompleteDelegationReturnWake(WakeRequest request, bool failed, string? reason = null)
    {
        var session = _lastSessionId ?? throw new InvalidOperationException("No active Session.");
        var run = _lastRunId ?? throw new InvalidOperationException("No active Run.");
        lock (_modeAuthorityMutationGate)
        {
            var journal = _store.ReadFrom(session, 1);
            var phase = PreM6RecordProjection.Replay(session, _codecs, journal)
                .Records["wake:" + request.WakeRequestId].Phase;
            if (phase is PreM6RecordPhase.Resolved or PreM6RecordPhase.Failed) return;
            var acceptedEvent = journal.Single(evt => _codecs.Decode(evt) is WakeRequestAccepted accepted
                && accepted.WakeRequestId == request.WakeRequestId
                && accepted.ExecutionId == request.TargetExecutionId);
            var facts = journal.Where(evt => evt.RunId == run).Select(_codecs.Decode).ToArray();
            var scope = AgentScope(run, facts, request.TargetExecutionId);
            using var attributed = ExecutionScope.Begin(scope);
            DomainEventPayload terminal = failed
                ? new WakeRequestFailed(request.TargetExecutionId, request.WakeRequestId,
                    string.IsNullOrWhiteSpace(reason) ? "Root continuation stopped before a durable completion receipt." : reason)
                : new WakeRequestResolved(request.TargetExecutionId, request.WakeRequestId);
            new EventStream(_store, _codecs, session).AppendBatch([terminal], DurabilityClass.Barrier,
                [scope], [new EventCausation(acceptedEvent.EventId)]);
        }
    }

    private bool HasExactlyBoundOpenExternalStep(IReadOnlyList<DomainEvent> events, LaneId lane,
        ExecutionId execution)
    {
        var decoded = events.Where(evt => evt.RunId == _lastRunId && evt.LaneId == lane
                && evt.ExecutionId == execution)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
        if (decoded.Select(item => item.Payload).OfType<ExternalLaneSessionBound>().Count() != 1
            || decoded.Any(item => item.Payload is ExternalLaneUsageRecorded)) return false;
        var starts = decoded.Where(item => item.Payload is ModelStepStarted)
            .Select(item => (item.Event, Start: (ModelStepStarted)item.Payload)).ToArray();
        var completed = decoded.Select(item => item.Payload).OfType<ModelStepCompleted>()
            .Select(item => (item.TurnId, item.StepIndex)).ToHashSet();
        var open = starts.Where(item => !completed.Contains((item.Start.TurnId, item.Start.StepIndex))).ToArray();
        if (open.Length != 1) return false;
        var turnId = open[0].Start.TurnId;
        return !decoded.Any(item => item.Payload switch
        {
            TurnCompleted terminal => terminal.TurnId == turnId,
            TurnInterrupted terminal => terminal.TurnId == turnId,
            TurnAbandoned terminal => terminal.TurnId == turnId,
            _ => false,
        });
    }

    private bool HasDurableLaneAcceptance(IReadOnlyList<DomainEvent> events, TaskId task, LaneId lane,
        ExecutionId execution)
    {
        var scoped = events.Where(evt => evt.RunId == _lastRunId && evt.TaskId == task
            && evt.LaneId == lane && evt.ExecutionId == execution)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
        var pending = scoped.Where(item => item.Payload is LaneAcceptancePending).ToArray();
        if (pending.Length != 1) return false;
        var acceptance = (LaneAcceptancePending)pending[0].Payload;
        if (acceptance.TaskId != task || acceptance.LaneId != lane || acceptance.ExecutionId != execution
            || acceptance.ResultRef.Kind != ArtifactKind.Other
            || acceptance.ResultRef.MediaType != "application/vnd.omnicore.agent-result+json"
            || acceptance.ResultRef.Sensitivity != Sensitivity.Sensitive
            || acceptance.ResultRef.Size is <= 0 or > 1_048_576) return false;
        var requests = scoped.Where(item => item.Payload is InteractionRequested request
            && request.InteractionId == acceptance.InteractionId && request.Kind == InteractionKind.AcceptanceConfirmation)
            .ToArray();
        if (requests.Length != 1) return false;
        var resolutions = scoped.Select(item => item.Payload).OfType<InteractionResolved>()
            .Where(item => item.InteractionId == acceptance.InteractionId).ToArray();
        var expired = scoped.Select(item => item.Payload).OfType<InteractionExpired>()
            .Any(item => item.InteractionId == acceptance.InteractionId);
        return !expired && resolutions.Length <= 1
            && resolutions.All(item => item.OptionId is "accept" or "revise");
    }

    /// <summary>Trusted Host composition only. FIFO, one actual slot; every provider/tool boundary
    /// still passes Explorer's routing, profile and hierarchical budget guards.</summary>
    internal CommandAck ExecuteDelegation(DelegationId id,
        Func<DelegationWork, CancellationToken, ExplorerTurn.TurnResult> execute, CancellationToken token,
        bool waitForCapacity = false, WeakSandboxWorkflowContinuation? weakSandboxContinuation = null)
    {
        var session = _lastSessionId ?? throw new InvalidOperationException("No Session.");
        var run = _lastRunId ?? throw new InvalidOperationException("No Run.");
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        var before = _store.CurrentSequence(session);
        using var cause = CausationScope.Begin(new CommandCausation(commandId));
        AgentCapacity.Lease? capacity = null;
        IDisposable? taskPacketPublicationLease = null;
        DelegationWork? work = null;
        string physicalRepositoryRoot;
        string workspaceDataRoot;
        string plannedLaneDestination;
        try
        {
            var initial = _store.ReadFrom(session, 1);
            var initialRun = initial.Where(evt => evt.RunId == run).ToArray();
            var initialProjection = RunProjection.Replay(session, run, _codecs, initialRun);
            var initialAuthority = initialProjection.ModeAuthority;
            var initialDelegation = initial.Select(_codecs.Decode).OfType<DelegationCreated>()
                .SingleOrDefault(evt => evt.Delegation.DelegationId == id)?.Delegation;
            if (initialProjection.State != RunState.Running || initialAuthority?.Mode != RunMode.Orchestrate
                || initialAuthority.Authorization is not { } initialAuthorization
                || !initialAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                return Ack(RuntimeCommandOutcome.Deferred("CoordinationLimitsUnavailable"));
            if (initialDelegation is null) return Ack(RuntimeCommandOutcome.Deferred("DelegationUnavailable"));
            var sourceModelStep = DelegationSourceModelStep(initial, initialDelegation);
            if (sourceModelStep is null || !SourceModelAllowsOrchestration(session, initial, sourceModelStep))
                return Ack(RuntimeCommandOutcome.Deferred("ModelProfileOrchestrationRevoked"));
            if (initialAuthorization.Limits.MaxAgents < 2)
                return Ack(RuntimeCommandOutcome.Deferred("AgentOrDepthLimit"));
            var admittedProfile = ResolveLaneAgentProfile(session, run, initialDelegation.ChildLaneId);
            if (admittedProfile is null) return Ack(RuntimeCommandOutcome.Deferred("ChildProfileUnavailable"));
            var readOnly = AgentPermissionScopeSubset.IsReadOnly(admittedProfile.PermissionCeiling);
            var verifiedRoot = VerifiedWorkspaceRoot(session);
            if (verifiedRoot is null) return Ack(RuntimeCommandOutcome.Deferred("WorkspaceRootUnavailable"));
            try
            {
                physicalRepositoryRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(verifiedRoot);
                workspaceDataRoot = OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), physicalRepositoryRoot);
                plannedLaneDestination = Path.Combine(workspaceDataRoot, "worktrees", initialDelegation.ChildLaneId.ToString());
            }
            catch (Exception)
            {
                return Ack(RuntimeCommandOutcome.Deferred("WorkspaceIdentityUnavailable"));
            }
            var initialFacts = initialRun.Select(_codecs.Decode).ToArray();
            var initialTask = initialFacts.OfType<TaskCreated>()
                .Single(evt => evt.TaskId == initialDelegation.ChildTaskId);
            var workflowStage = ReadWorkflowStageForTask(session, run, initialTask.TaskId);
            VerifiedImplementationSource? verifiedImplementation = null;
            if (workflowStage?.Definition.Stage == WorkflowStage.Verify
                && admittedProfile.Executor == AgentExecutorKind.ClaudeCode)
                return Ack(RuntimeCommandOutcome.Deferred("WorkflowVerifierRequiresNativeToolPipeline"));
            if (readOnly) plannedLaneDestination = physicalRepositoryRoot;
            if (workflowStage?.Definition.Stage == WorkflowStage.Verify)
            {
                if (!AgentPermissionScopeSubset.IsWorkflowVerifierReadOnly(admittedProfile.PermissionCeiling))
                    return Ack(RuntimeCommandOutcome.Deferred("WorkflowVerifyMustBeReadOnly"));
                readOnly = true;
                verifiedImplementation = ResolveVerifiedImplementationSource(session, run, workflowStage,
                    initialRun, initialFacts, physicalRepositoryRoot, workspaceDataRoot, token);
                if (verifiedImplementation is null)
                    return Ack(RuntimeCommandOutcome.Deferred("WorkflowImplementationOwnershipUnavailable"));
                plannedLaneDestination = verifiedImplementation.Identity.WorktreePath;
            }
            var priorProvisioning = initialFacts.OfType<LaneProvisioning>()
                .Any(payload => payload.LaneId == initialDelegation.ChildLaneId);
            var priorLaneStarted = initialFacts.OfType<LaneStarted>()
                .Any(payload => payload.LaneId == initialDelegation.ChildLaneId);
            var priorWorktreeCount = initialFacts.OfType<LaneWorktreeCreated>()
                .Count(payload => payload.LaneId == initialDelegation.ChildLaneId);
            var priorBoundCount = initialFacts.OfType<LaneWorktreeBound>()
                .Count(payload => payload.LaneId == initialDelegation.ChildLaneId);
            if (priorWorktreeCount + priorBoundCount > 1 || priorProvisioning
                && priorWorktreeCount + priorBoundCount == 0
                && !(readOnly && priorLaneStarted))
                return Ack(RuntimeCommandOutcome.Deferred("LaneProvisioningRecoveryRequired"));
            if (priorWorktreeCount == 0 && initialProjection.State != RunState.Running)
                return Ack(RuntimeCommandOutcome.Deferred("RunNotRunning"));
            var durableSequence = initial.Where(evt => evt.RunId == run)
                .Single(evt => _codecs.Decode(evt) is DelegationCreated created
                    && created.Delegation.DelegationId == id).Sequence;
            var scheduler = AgentCapacity.For(_store);
            capacity = waitForCapacity
                ? scheduler.Acquire(session, run, id, initialAuthorization.Limits.MaxAgents,
                    initialDelegation.Priority, readOnly, token, durableSequence, plannedLaneDestination)
                : scheduler.TryAcquire(session, run, id, initialAuthorization.Limits.MaxAgents,
                    initialDelegation.Priority, readOnly, token, durableSequence, plannedLaneDestination);
            if (capacity is null) return Ack(RuntimeCommandOutcome.Deferred("WaitingForCapacity"));

            // Provisioning is a durable phase of its own. A crash after LaneProvisioning but
            // before LaneWorktreeCreated remains recovery-required; we never adopt a directory
            // by guessing from its path or delete a possibly edited orphan.
            var initialDelegationHistory = PreM6RecordProjection.Replay(session, _codecs, initial)
                .Records["delegation:" + id];
            if (initialDelegationHistory.Phase is PreM6RecordPhase.Returned or PreM6RecordPhase.Failed)
            {
                if (initialDelegationHistory.Phase == PreM6RecordPhase.Returned)
                    EnsureDelegationReturnWake(session, run, initialDelegation, initial);
                return Ack(RuntimeCommandOutcome.NoOp());
            }
            if (initialDelegationHistory.Phase == PreM6RecordPhase.Created)
            {
                // Queue order must be decided before provisioning writes LaneProvisioning or
                // creates an owned worktree. Otherwise a rejected later lane would leave a
                // durable half-provisioned record that blocks the predecessor's cancellation
                // and cannot safely be adopted on retry.
                var initialRecords = PreM6RecordProjection.Replay(session, _codecs, initial).Records;
                var firstPending = initialRun
                    .Select((evt, index) => (Event: evt, Payload: _codecs.Decode(evt)))
                    .Where(item => item.Payload is DelegationCreated created
                        && initialRecords["delegation:" + created.Delegation.DelegationId].Phase
                            == PreM6RecordPhase.Created)
                    .OrderByDescending(item => ((DelegationCreated)item.Payload).Delegation.Priority)
                    .ThenBy(item => item.Event.Sequence)
                    .Select(item => ((DelegationCreated)item.Payload).Delegation.DelegationId)
                    .FirstOrDefault();
                if (firstPending != id) return Ack(RuntimeCommandOutcome.Deferred("QueuePredecessor"));
            }
            WorktreeIdentity? worktreeIdentity = null;
            TaskPacket? taskPacket = null;
            LaneWorktreeCreated? worktreeEvent = initialFacts.OfType<LaneWorktreeCreated>()
                .SingleOrDefault(evt => evt.LaneId == initialDelegation.ChildLaneId);
            LaneWorktreeBound? worktreeBoundEvent = initialFacts.OfType<LaneWorktreeBound>()
                .SingleOrDefault(evt => evt.LaneId == initialDelegation.ChildLaneId);
            if (worktreeEvent is not null && worktreeBoundEvent is not null)
                return Ack(RuntimeCommandOutcome.Deferred("LaneOwnershipUnavailable"));
            var hadWorktreeEvent = worktreeEvent is not null || worktreeBoundEvent is not null;
            var existingBinding = (DomainEventPayload?)worktreeEvent ?? worktreeBoundEvent;
            var sharedReadOnlyFallback = false;
            ExecutionId? provisionedExecution = null;
            if (existingBinding is null && initialDelegationHistory.Phase == PreM6RecordPhase.Created)
            {
                provisionedExecution = ExecutionId.New();
                var provisioningScope = new ExecutionScopeState(run, initialTask.TaskId,
                    initialDelegation.ChildLaneId, ExecutionId: provisionedExecution);
                lock (_modeAuthorityMutationGate)
                {
                    var current = _store.ReadFrom(session, 1);
                    var currentRun = current.Where(evt => evt.RunId == run).ToArray();
                    var currentProjection = RunProjection.Replay(session, run, _codecs, currentRun);
                    var currentFacts = currentRun.Select(_codecs.Decode).ToArray();
                    if (_lastSessionId != session || _lastRunId != run
                        || currentProjection.State != RunState.Running
                        || currentFacts.OfType<LaneWorktreeCreated>().Any(evt => evt.LaneId == initialDelegation.ChildLaneId)
                        || currentFacts.OfType<LaneWorktreeBound>().Any(evt => evt.LaneId == initialDelegation.ChildLaneId)
                        || currentFacts.OfType<LaneProvisioning>().Any(evt => evt.LaneId == initialDelegation.ChildLaneId))
                        return Ack(RuntimeCommandOutcome.Deferred("LaneProvisioningRecoveryRequired"));
                    new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[] {
                        new TaskStarted(initialTask.TaskId, initialDelegation.ChildLaneId),
                        new LaneProvisioning(initialDelegation.ChildLaneId),
                    }, DurabilityClass.Barrier, new ExecutionScopeState?[] { provisioningScope, provisioningScope });
                }

                var stateRun = RunProjection.Replay(session, run, _codecs,
                    _store.ReadFrom(session, 1).Where(evt => evt.RunId == run).ToArray());
                var statePlan = PlanProjection.Replay(_codecs,
                    _store.ReadFrom(session, 1).Where(evt => evt.RunId == run).ToArray());
                var compactWorkingState = WorkingStateProjector.Render(
                    WorkingStateProjector.Project(stateRun, statePlan));
                var provisioner = new LaneWorktreeProvisioner(SystemProcessRuntime.Instance(), _artifacts!);
                LaneWorktreeProvisionOutcome provisioned;
                if (verifiedImplementation is { } verifiedSource)
                {
                    var latestSource = RecaptureImplementationBinding(verifiedSource, physicalRepositoryRoot,
                        capacity.Cancellation.Token);
                    if (latestSource is null || latestSource != verifiedSource.Binding)
                        return Ack(RuntimeCommandOutcome.Deferred("WorkflowImplementationChanged"));
                    provisioned = provisioner.BindVerifiedImplementation(verifiedSource.Identity, session,
                        run, initialTask, initialDelegation, provisionedExecution!, admittedProfile,
                        compactWorkingState, verifiedSource.Binding);
                }
                else
                {
                    provisioned = provisioner.CreateAsync(physicalRepositoryRoot, workspaceDataRoot, session, run, initialTask,
                            initialDelegation, provisionedExecution!, admittedProfile, compactWorkingState,
                            capacity.Cancellation.Token)
                        .GetAwaiter().GetResult();
                }
                if (provisioned.Succeeded)
                    provisioned = provisioner.Publish(provisioned, capacity.Cancellation.Token);
                if (!provisioned.Succeeded && readOnly
                    && provisioned.Error == GitWorktreeErrorCode.NoRepository)
                {
                    // Existing read-only delegation remains usable for ordinary folders. This
                    // path exposes no filesystem writer, and writer lanes always require Git.
                    sharedReadOnlyFallback = true;
                }
                else if (!provisioned.Succeeded)
                    return Ack(RuntimeCommandOutcome.Deferred("LaneWorktreeProvisioningFailed"));
                else
                {
                    worktreeIdentity = provisioned.Identity;
                    taskPacket = provisioned.Packet;
                    taskPacketPublicationLease = provisioned.PublicationLease;
                    if (worktreeIdentity is null || taskPacket is null || provisioned.TaskPacketRef is null)
                        return Ack(RuntimeCommandOutcome.Deferred("LaneWorktreeProvisioningFailed"));
                    if (verifiedImplementation is { } source)
                    {
                        var binding = source.Binding;
                        worktreeBoundEvent = new LaneWorktreeBound(initialTask.TaskId,
                            initialDelegation.ChildLaneId, provisionedExecution!, source.OriginalEvent.TaskId,
                            source.OriginalEvent.LaneId, source.OriginalEvent.ExecutionId,
                            worktreeIdentity.OwnershipId, worktreeIdentity.RepositoryRoot,
                            worktreeIdentity.GitCommonDirectory, worktreeIdentity.WorktreePath,
                            worktreeIdentity.SnapshotCommit, worktreeIdentity.SnapshotTree,
                            provisioned.TaskPacketRef, taskPacket.SelectedProjectionRef,
                            binding.WorkflowId, binding.WorkflowVersion, binding.Instance,
                            binding.Tree, binding.Head, binding.Branch);
                    }
                    else
                    {
                        worktreeEvent = new LaneWorktreeCreated(initialTask.TaskId, initialDelegation.ChildLaneId,
                            provisionedExecution!, worktreeIdentity.OwnershipId, worktreeIdentity.RepositoryRoot,
                            worktreeIdentity.GitCommonDirectory, worktreeIdentity.WorktreePath,
                            worktreeIdentity.SnapshotCommit, worktreeIdentity.SnapshotTree,
                            provisioned.TaskPacketRef, taskPacket.SelectedProjectionRef);
                    }
                }
            }
            else if (existingBinding is not null)
            {
                var ownershipId = worktreeEvent?.OwnershipId ?? worktreeBoundEvent!.OwnershipId;
                provisionedExecution = worktreeEvent?.ExecutionId ?? worktreeBoundEvent!.ExecutionId;
                var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
                    .LoadIdentityAsync(physicalRepositoryRoot, workspaceDataRoot, ownershipId,
                        capacity.Cancellation.Token).GetAwaiter().GetResult();
                if (!loaded.Succeeded || loaded.Worktree is not { } durableIdentity
                    || !(worktreeEvent is not null && MatchesCreatedEvent(worktreeEvent, durableIdentity)
                        || worktreeBoundEvent is not null && verifiedImplementation is { } source
                            && MatchesBoundEvent(worktreeBoundEvent, durableIdentity, source.OriginalEvent,
                                source.Binding))
                    || _artifacts?.Verify((worktreeEvent?.TaskPacketRef ?? worktreeBoundEvent!.TaskPacketRef).Hash,
                        (worktreeEvent?.TaskPacketRef ?? worktreeBoundEvent!.TaskPacketRef).Size) != true
                    || _artifacts?.GetText((worktreeEvent?.TaskPacketRef ?? worktreeBoundEvent!.TaskPacketRef).Hash)
                        is not { } packetText)
                    return Ack(RuntimeCommandOutcome.Deferred("LaneOwnershipUnavailable"));
                taskPacket = JsonSerializer.Deserialize(packetText, TaskPacketJsonContext.Default.TaskPacket);
                if (taskPacket is null || !(worktreeEvent is not null
                        ? MatchesTaskPacket(worktreeEvent, taskPacket, session, run, initialTask, initialDelegation, _artifacts)
                        : MatchesTaskPacket(worktreeBoundEvent!, taskPacket, session, run, initialTask,
                            initialDelegation, _artifacts))
                    || verifiedImplementation is { } verifiedSource
                        && (verifiedSource.Binding != taskPacket.WorkflowImplementation
                            || RecaptureImplementationBinding(verifiedSource, physicalRepositoryRoot,
                                capacity.Cancellation.Token) != verifiedSource.Binding)
                    || verifiedImplementation is null && taskPacket.WorkflowImplementation is not null)
                    return Ack(RuntimeCommandOutcome.Deferred("LaneOwnershipUnavailable"));
                worktreeIdentity = durableIdentity;
            }
            else if (readOnly && priorProvisioning && priorLaneStarted
                && initialDelegationHistory.Phase == PreM6RecordPhase.Accepted)
            {
                // A non-Git read-only lane may resume on the verified root; the durable
                // LaneStarted marker is written only after selecting this no-write fallback.
                sharedReadOnlyFallback = true;
                provisionedExecution = initialDelegationHistory.Facts.OfType<DelegationAccepted>()
                    .Single().ChildExecutionId;
            }
            else
            {
                return Ack(RuntimeCommandOutcome.Deferred("LaneProvisioningRecoveryRequired"));
            }
            lock (_modeAuthorityMutationGate)
            {
                var journal = _store.ReadFrom(session, 1);
                var records = PreM6RecordProjection.Replay(session, _codecs, journal);
                var own = journal.Where(e => e.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                var history = records.Records["delegation:" + id];
                var delegation = ReadCurrentDelegation(id);
                if (history.Phase is PreM6RecordPhase.Returned or PreM6RecordPhase.Failed) return Ack(RuntimeCommandOutcome.NoOp());
                var projection = RunProjection.Replay(session, run, _codecs, own);
                var authority = projection.ModeAuthority;
                if (_lastSessionId != session || _lastRunId != run || projection.State != RunState.Running
                    || new RunControlService(_store, _codecs).ActiveRun(session) != run)
                    return Ack(RuntimeCommandOutcome.Deferred("RunNotRunning"));
                if (authority?.Mode != RunMode.Orchestrate || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    return Ack(RuntimeCommandOutcome.Deferred("CoordinationLimitsUnavailable"));
                if (IsExecutionTerminal(facts, delegation.ParentExecutionId))
                {
                    ReconcileTerminatedSupervisors(session, run);
                    return Ack(RuntimeCommandOutcome.Deferred("SupervisorUnavailable"));
                }
                var supervisor = facts.OfType<AgentExecutionStarted>()
                    .SingleOrDefault(e => e.ExecutionId == delegation.ParentExecutionId);
                if (supervisor is null) return Ack(RuntimeCommandOutcome.Deferred("SupervisorUnavailable"));
                var childHasOpenModelStep = HasOpenModelStep(own, delegation.ChildLaneId);
                var canResumeBoundExternal = childHasOpenModelStep
                    && admittedProfile.Executor == AgentExecutorKind.ClaudeCode
                    && history.Phase == PreM6RecordPhase.Accepted
                    && history.Facts.OfType<DelegationAccepted>().SingleOrDefault() is { } acceptedBinding
                    && HasExactlyBoundOpenExternalStep(own, delegation.ChildLaneId, acceptedBinding.ChildExecutionId);
                var canResumeWeakSandboxCall = weakSandboxContinuation is { } weak
                    && history.Phase == PreM6RecordPhase.Accepted
                    && TryMatchOpenWeakSandboxCall(session, run, delegation, own, weak);
                if (HasOpenModelStep(own, supervisor.LaneId) || HasOpenToolCall(own, supervisor.LaneId)
                    || childHasOpenModelStep && !canResumeBoundExternal && !canResumeWeakSandboxCall
                    || HasOpenToolCall(own, delegation.ChildLaneId) && !canResumeWeakSandboxCall)
                    return Ack(RuntimeCommandOutcome.Deferred("ExecutionBoundaryRequired"));
                if (history.Phase == PreM6RecordPhase.Created)
                {
                    // Durable explicit priority, then creation sequence for stable FIFO.
                    var first = own.Select((evt, index) => (Event: evt, Index: index, Payload: _codecs.Decode(evt)))
                        .Where(item => item.Payload is DelegationCreated created
                            && records.Records["delegation:" + created.Delegation.DelegationId].Phase == PreM6RecordPhase.Created)
                        .OrderByDescending(item => ((DelegationCreated)item.Payload).Delegation.Priority)
                        .ThenBy(item => item.Event.Sequence).Select(item => (DelegationCreated)item.Payload).First();
                    if (first.Delegation.DelegationId != id) return Ack(RuntimeCommandOutcome.Deferred("QueuePredecessor"));
                    if (records.Records.Values.Where(h => h.Phase == PreM6RecordPhase.Accepted)
                        .SelectMany(h => h.Facts.OfType<DelegationCreated>())
                        .Any(created => !AgentCapacity.For(_store).IsActive(session, run,
                            created.Delegation.DelegationId)))
                        return Ack(RuntimeCommandOutcome.Deferred("ExecutionOwnershipUnavailable"));
                }
                else if (initialDelegationHistory.Phase == PreM6RecordPhase.Accepted)
                {
                    if (history.Facts.OfType<DelegationCancellationRequested>().Any())
                        return Ack(RuntimeCommandOutcome.Deferred("CancellationRecoveryRequired"));
                    // External CLI recovery must be bound to its first durable init event or to
                    // a final result receipt. Before init, effects are ambiguous and no rerun is safe.
                    if (admittedProfile.Executor == AgentExecutorKind.ClaudeCode)
                    {
                        var childExecution = history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                        var hasExternalBinding = own.Select(_codecs.Decode).OfType<ExternalLaneSessionBound>()
                            .Any(evt => evt.ExecutionId == childExecution && evt.LaneId == delegation.ChildLaneId);
                        var hasExternalReceipt = own.Select(_codecs.Decode).OfType<ExternalLaneUsageRecorded>()
                            .Any(evt => evt.ExecutionId == childExecution && evt.LaneId == delegation.ChildLaneId);
                        if (!hasExternalBinding && !hasExternalReceipt)
                            return Ack(RuntimeCommandOutcome.Deferred("ExternalLaneRecoveryRequiresManualResolution"));
                    }
                    else if (OmniCliRuntime.FindOpenTurnStart(own, _codecs, run, delegation.ChildLaneId) is null
                        && !HasDurableLaneAcceptance(own, delegation.ChildTaskId, delegation.ChildLaneId,
                            history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId))
                        return Ack(RuntimeCommandOutcome.Deferred("ExecutionRecoveryRequired"));
                }
                else if (history.Facts.OfType<DelegationCancellationRequested>().Any())
                    return Ack(RuntimeCommandOutcome.Deferred("CancellationRecoveryRequired"));
                var profile = ResolveLaneAgentProfile(session, run, delegation.ChildLaneId)
                    ?? throw new InvalidOperationException("Child profile unavailable.");
                var parentScope = AgentScope(run, facts, delegation.ParentExecutionId);
                var parentProfile = ResolveLaneAgentProfile(session, run, parentScope.LaneId!)
                    ?? throw new InvalidOperationException("Parent profile unavailable.");
                if (!AgentPermissionScopeSubset.IsSubset(profile.PermissionCeiling, parentProfile.PermissionCeiling))
                    return Ack(RuntimeCommandOutcome.Deferred("ChildPermissionCeilingExceedsParent"));
                var task = facts.OfType<TaskCreated>().Single(e => e.TaskId == delegation.ChildTaskId);
                var executionId = history.Phase == PreM6RecordPhase.Created ? provisionedExecution!
                    : history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                var scope = new ExecutionScopeState(run, task.TaskId, delegation.ChildLaneId, ExecutionId: executionId);
                if (!sharedReadOnlyFallback && (existingBinding is null && worktreeEvent is null && worktreeBoundEvent is null
                    || worktreeIdentity is null || taskPacket is null
                    || (worktreeEvent?.ExecutionId ?? worktreeBoundEvent?.ExecutionId) != executionId))
                    return Ack(RuntimeCommandOutcome.Deferred("LaneOwnershipUnavailable"));
                work = new(session, run, delegation, scope, profile, task.Objective, task.Budget,
                    worktreeIdentity, taskPacket, worktreeIdentity?.WorktreePath ?? physicalRepositoryRoot,
                    taskPacket?.WorkingState ?? "");
                if (_artifacts is not IArtifactPreparationStore || _artifacts is not IArtifactPublicationLease)
                    return Ack(RuntimeCommandOutcome.Deferred("ResultPublicationUnavailable"));
                // A finite elapsed authorization bounds the actual owned runtime cancellation token.
                var remaining = authority.Authorization!.GrantedAtUtc!.Value
                    .AddSeconds(authority.Authorization.Limits.MaxElapsedSeconds) - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return Ack(RuntimeCommandOutcome.Deferred("CoordinationLimitsExpired"));
                capacity.Cancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1d)));
                capacity.Cancellation.Token.ThrowIfCancellationRequested();
                if (history.Phase == PreM6RecordPhase.Created)
                {
                    var currentJournal = _store.ReadFrom(session, 1);
                    var currentSource = DelegationSourceModelStep(currentJournal, delegation);
                    if (currentSource is null || !SourceModelAllowsOrchestration(session, currentJournal, currentSource))
                        return Ack(RuntimeCommandOutcome.Deferred("ModelProfileOrchestrationRevoked"));
                    var started = new AgentExecutionStarted(executionId, delegation.ChildLaneId, delegation.ProfileId,
                        delegation.ParentExecutionId, delegation.Relation, delegation.Supervision);
                    var binding = new SupervisionBinding(BindingId.New(), executionId, delegation.ParentExecutionId, 1);
                    var mailbox = new ExecutionMailbox(MailboxId.New(), executionId);
                    var admissionEvents = new List<DomainEventPayload> {
                        new LaneStarted(delegation.ChildLaneId), started,
                        new SupervisionBindingCreated(executionId, binding),
                        new ExecutionMailboxCreated(executionId, mailbox),
                        new DelegationAccepted(delegation.ParentExecutionId, id, executionId),
                    };
                    if (!hadWorktreeEvent && worktreeEvent is not null) admissionEvents.Insert(0, worktreeEvent);
                    if (!hadWorktreeEvent && worktreeBoundEvent is not null) admissionEvents.Insert(0, worktreeBoundEvent);
                    admissionEvents.AddRange(BindWorkflowStageExecution(session, run, task.TaskId,
                        delegation.ChildLaneId, executionId));
                    var scopes = admissionEvents.Select(evt => evt is DelegationAccepted
                        ? (ExecutionScopeState?)parentScope : scope).ToArray();
                    new EventStream(_store, _codecs, session).AppendBatch(admissionEvents,
                        DurabilityClass.Barrier, scopes);
                    DispatchStartedLaneLifecycle(session, run, task.TaskId, delegation.ChildLaneId);
                    // The journal Barrier now owns all references. Release the CAS GC
                    // publication lease before entering Explorer: the child Turn acquires
                    // its own publication lease and the store lease is intentionally exclusive.
                    taskPacketPublicationLease?.Dispose();
                    taskPacketPublicationLease = null;
                    var scopedSupervisor = SupervisorClientFactory.Create(this, delegation.ParentExecutionId);
                    new InProcessSupervisor(scopedSupervisor, delegation.ParentExecutionId)
                        .AcknowledgeBinding(executionId, binding.BindingId, capacity.Cancellation.Token);
                }
            }
            using var execution = ExecutionScope.Begin(work.Scope);
            var result = execute(work, capacity.Cancellation.Token);
            capacity.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.StopReason == StopReason.InputRequired) return Ack(RuntimeCommandOutcome.Accepted());
            if (result.StopReason != StopReason.EndTurn) throw new InvalidOperationException("Child stopped: " + result.StopReason);
            lock (_modeAuthorityMutationGate)
            {
                var journal = _store.ReadFrom(session, 1);
                var facts = journal.Where(e => e.RunId == run).Select(_codecs.Decode).ToArray();
                if (RunProjection.Replay(session, run, _codecs, journal).IsTerminal()) throw new InvalidOperationException("Run ended before child publication.");
                if (IsExecutionTerminal(facts, work.Delegation.ParentExecutionId)) throw new InvalidOperationException("Supervisor unavailable; no implicit acceptance.");
                var summary = new PiiRedactor().Redact(result.FinalText ?? "");
                var issues = result.ToolCalls.Where(call => !call.Succeeded)
                    .Select(call => new PiiRedactor().Redact(call.ToolName + ": " + call.Summary)).ToArray();
                var resultSchemaId = result.DurableAcceptedResultSchemaId
                    ?? (result.ExternalAgentResult is not null
                        ? ClaudeLaneAgentResultValidator.SchemaId : "core.explorer.v1");
                var resultJson = result.ExternalAgentResult is { } externalResult
                    ? ClaudeLaneAgentResultValidator.SerializeRedacted(externalResult)
                    : JsonSerializer.Serialize(new DelegationResultDocument(1, "Succeeded", summary, [], issues),
                        DelegationResultJson.Default.DelegationResultDocument);
                using var lease = ((IArtifactPublicationLease)_artifacts!).AcquirePublicationLease(CancellationToken.None);
                ArtifactRef resultRef;
                if (result.DurableAcceptedResultRef is { } acceptedResultRef)
                {
                    if (acceptedResultRef.Kind != ArtifactKind.Other
                        || acceptedResultRef.MediaType != "application/vnd.omnicore.agent-result+json"
                        || acceptedResultRef.Sensitivity != Sensitivity.Sensitive
                        || acceptedResultRef.Size is <= 0 or > 1_048_576
                        || _artifacts?.Verify(acceptedResultRef.Hash, acceptedResultRef.Size) != true
                        || _artifacts.GetText(acceptedResultRef.Hash) is not { } acceptedJson
                        || !ValidAcceptedResult(acceptedJson, resultSchemaId))
                        throw new InvalidDataException("Accepted lane result receipt is unavailable.");
                    resultRef = acceptedResultRef;
                }
                else if (result.ExternalResultRef is { } externalResultRef)
                {
                    if (result.ExternalAgentResult is null || externalResultRef.Kind != ArtifactKind.Other
                        || externalResultRef.MediaType != "application/vnd.omnicore.agent-result+json"
                        || externalResultRef.Sensitivity != Sensitivity.Sensitive || externalResultRef.Size is <= 0 or > 1_048_576
                        || _artifacts?.Verify(externalResultRef.Hash, externalResultRef.Size) != true
                        || _artifacts.GetText(externalResultRef.Hash) is not { } durableExternalJson
                        || ClaudeLaneAgentResultValidator.Validate(durableExternalJson) is null)
                        throw new InvalidDataException("External agent result receipt is unavailable.");
                    resultRef = externalResultRef;
                }
                else
                {
                    var prepared = ((IArtifactPreparationStore)_artifacts!).PrepareText(resultJson,
                        "application/vnd.omnicore.agent-result+json", ArtifactKind.Other, Sensitivity.Sensitive);
                    if (prepared.Reference.Size is <= 0 or > 1_048_576
                        || prepared.Publish() != prepared.Reference)
                        throw new InvalidDataException("Result publication reference changed.");
                    resultRef = prepared.Reference;
                }
                var idExecution = work.Scope.ExecutionId!;
                var parentScope = AgentScope(run, facts, work.Delegation.ParentExecutionId);
                var terminalEvents = new List<DomainEventPayload>();
                terminalEvents.Add(new AgentExecutionCompleted(idExecution, work.Scope.LaneId!, work.Profile.Id,
                    work.Delegation.ParentExecutionId, work.Delegation.Relation, work.Delegation.Supervision));
                terminalEvents.Add(new AgentResultProduced(idExecution, resultRef, 1, resultSchemaId));
                terminalEvents.Add(new DelegationReturned(work.Delegation.ParentExecutionId, id, resultRef));
                var terminalScopes = terminalEvents.Select(evt => evt is DelegationReturned
                    ? (ExecutionScopeState?)parentScope : work.Scope).ToArray();
                new EventStream(_store, _codecs, session).AppendBatch(terminalEvents,
                    DurabilityClass.Barrier, terminalScopes);
                // The returned artifact is durable before its wake is published. Recovery of
                // ExecuteDelegation repairs the narrow crash window idempotently.
                EnsureDelegationReturnWake(session, run, work.Delegation,
                    _store.ReadFrom(session, 1));
            }
            return Ack(RuntimeCommandOutcome.Accepted());
        }
        catch (OperationCanceledException) when (capacity is null)
        {
            return Ack(RuntimeCommandOutcome.Deferred("WaitingForCapacityCancelled"));
        }
        catch (Exception failure)
        {
            if (work is not null)
            {
                try { FailOwnedDelegation(work, capacity?.Cancellation.IsCancellationRequested == true || token.IsCancellationRequested); }
                catch (Exception checkpointFailure)
                { return FailedDurableCommandAck(messageId, session, before, failure.Message + "; checkpoint: " + checkpointFailure.Message, false); }
            }
            return FailedDurableCommandAck(messageId, session, before, failure.Message, false);
        }
        finally { taskPacketPublicationLease?.Dispose(); capacity?.Dispose(); }
        CommandAck Ack(RuntimeCommandOutcome outcome) => CommandOutcomeAck(messageId, "ok", null, outcome, session, before, commandId);
    }

    private bool TryMatchOpenWeakSandboxCall(SessionId session, RunId run, Delegation delegation,
        IReadOnlyList<DomainEvent> events, WeakSandboxWorkflowContinuation continuation)
    {
        if (continuation.OptionId is not ("consent_once" or "deny")
            || continuation.TaskId != delegation.ChildTaskId.ToString()
            || continuation.LaneId != delegation.ChildLaneId.ToString()
            || !Guid.TryParseExact(continuation.InvocationId, "D", out var invocationGuid)
            || !Guid.TryParseExact(continuation.InteractionId, "D", out var interactionGuid)
            || !Guid.TryParseExact(continuation.TurnId, "D", out var turnGuid)
            || !Guid.TryParseExact(continuation.ToolCallId, "D", out var toolCallGuid)) return false;
        var invocation = new CommandId(invocationGuid);
        var interaction = new InteractionId(interactionGuid);
        var turn = new TurnId(turnGuid);
        var toolCall = new ToolCallId(toolCallGuid);
        var codecs = _codecs;
        var scoped = events.Where(evt => evt.RunId == run).ToArray();
        if (!scoped.Any(evt => codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
                && admitted.InvocationId == invocation && admitted.SessionId == session && admitted.RunId == run
                && admitted.Kind == "compiledWorkflow")
            || !scoped.Any(evt => codecs.Decode(evt) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
            || scoped.Any(evt => codecs.Decode(evt) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation)) return false;

        var requestEvents = scoped.Where(evt => evt.TaskId == delegation.ChildTaskId
                && evt.LaneId == delegation.ChildLaneId && evt.TurnId == turn && evt.ToolCallId == toolCall
                && codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interaction && request.Kind == InteractionKind.WeakSandboxConsent)
            .ToArray();
        var resolvedEvents = scoped.Where(evt => evt.TaskId == delegation.ChildTaskId
                && evt.LaneId == delegation.ChildLaneId && evt.TurnId == turn && evt.ToolCallId == toolCall
                && codecs.Decode(evt) is InteractionResolved resolved
                && resolved.InteractionId == interaction && !resolved.IsQuestionnaire
                && resolved.Cause == InteractionCause.User && resolved.OptionId == continuation.OptionId)
            .ToArray();
        var resumedEvents = scoped.Where(evt => evt.TaskId == delegation.ChildTaskId
                && evt.LaneId == delegation.ChildLaneId && evt.TurnId == turn && evt.ToolCallId == toolCall
                && codecs.Decode(evt) is RunInteractionResumed resumed
                && resumed.InteractionId == interaction && resumed.CommandId == continuation.ResponseCommandId)
            .ToArray();
        if (requestEvents.Length != 1 || resolvedEvents.Length != 1 || resumedEvents.Length != 1
            || !(requestEvents[0].Sequence < resolvedEvents[0].Sequence
                && resolvedEvents[0].Sequence < resumedEvents[0].Sequence)
            || resolvedEvents[0].Sequence < continuation.ResponseFirstSequence
            || resolvedEvents[0].Sequence > continuation.ResponseLastSequence
            || resumedEvents[0].Sequence < continuation.ResponseFirstSequence
            || resumedEvents[0].Sequence > continuation.ResponseLastSequence
            || requestEvents[0].ExecutionId is not { } executionId
            || resolvedEvents[0].ExecutionId != executionId || resumedEvents[0].ExecutionId != executionId
            || resolvedEvents[0].Causation is not CommandCausation resolvedCause
            || resolvedCause.CommandId.ToString() != continuation.ResponseCommandId
            || resumedEvents[0].Causation is not CommandCausation resumedCause
            || resumedCause.CommandId.ToString() != continuation.ResponseCommandId
            || codecs.Decode(requestEvents[0]) is not InteractionRequested weakRequest
            || !ProtocolMapper.OptionIds(weakRequest.OptionsJson).SequenceEqual(["consent_once", "deny"], StringComparer.Ordinal))
            return false;

        var callEvents = scoped.Where(evt => evt.TaskId == delegation.ChildTaskId
                && evt.LaneId == delegation.ChildLaneId && evt.TurnId == turn && evt.ToolCallId == toolCall
                && evt.ExecutionId == executionId)
            .OrderBy(evt => evt.Sequence).Select(evt => (Event: evt, Payload: codecs.Decode(evt))).ToArray();
        var requested = callEvents.SingleOrDefault(item => item.Payload is ToolCallRequested);
        var prepared = callEvents.SingleOrDefault(item => item.Payload is ToolCallPrepared);
        var authorized = callEvents.LastOrDefault(item => item.Payload is ToolCallAuthorized);
        if (requested.Event is null || requested.Payload is not ToolCallRequested requestedCall
            || requestedCall.ToolName != "dyn.core.verify_integration"
            || prepared.Event is null || authorized.Event is null
            || !(requested.Event.Sequence < prepared.Event.Sequence && prepared.Event.Sequence < requestEvents[0].Sequence
                && prepared.Event.Sequence < authorized.Event.Sequence
                && authorized.Event.Sequence < requestEvents[0].Sequence)
            || callEvents.Any(item => item.Event.Sequence > requestEvents[0].Sequence
                && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallFailed
                    or ToolCallEffectUnknown or ToolCallReconciled)) return false;

        var permissionDenials = callEvents.Where(item => item.Event.Sequence > resolvedEvents[0].Sequence
                && item.Event.Sequence < resumedEvents[0].Sequence
                && item.Payload is PermissionDenied denied && denied.ToolCallId == toolCall)
            .ToArray();
        if (continuation.OptionId == "deny"
            ? permissionDenials.Length != 1
                || permissionDenials[0].Event.Causation is not CommandCausation denialCause
                || denialCause.CommandId.ToString() != continuation.ResponseCommandId
            : permissionDenials.Length != 0) return false;

        // A Verify stage may perform successful reads before asking for weak-sandbox consent.
        // Keep the verifier-only callEvents above for its exact receipt checks, but calculate
        // concurrency from every call in this exact child Task/Lane/Turn/execution. Historical
        // ToolCallRequested events are not evidence of concurrent work once their lifecycle closed.
        var turnExecutionEvents = scoped.Where(evt => evt.TaskId == delegation.ChildTaskId
                && evt.LaneId == delegation.ChildLaneId && evt.TurnId == turn && evt.ExecutionId == executionId)
            .OrderBy(evt => evt.Sequence).Select(evt => (Event: evt, Payload: codecs.Decode(evt))).ToArray();
        var openToolCalls = turnExecutionEvents.Where(item => item.Payload is ToolCallRequested)
            .Select(item => ((ToolCallRequested)item.Payload).ToolCallId)
            .Distinct()
            .Where(callId => HasOpenToolCall(turnExecutionEvents.Where(item => item.Event.ToolCallId == callId)
                .Select(item => item.Event).ToArray()))
            .ToArray();
        if (continuation.OptionId == "deny"
            ? openToolCalls.Length != 0 || HasOpenToolCall(callEvents.Select(item => item.Event).ToArray())
            : openToolCalls.Length != 1 || openToolCalls[0] != toolCall) return false;

        var responseReceipt = ReceiptStore?.ReadCommandReceipt(continuation.ResponseCommandId);
        return responseReceipt is { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
                FirstSequence: not null, LastSequence: not null }
            && responseReceipt.FirstSequence == continuation.ResponseFirstSequence
            && responseReceipt.LastSequence == continuation.ResponseLastSequence
            && string.Equals(responseReceipt.SessionId, session.ToString(), StringComparison.Ordinal);
    }

    private static bool ValidAcceptedResult(string json, string schemaId) => schemaId switch
    {
        ClaudeLaneAgentResultValidator.SchemaId => ClaudeLaneAgentResultValidator.Validate(json) is not null,
        "core.explorer.v1" => JsonSerializer.Deserialize(json,
            DelegationResultJson.Default.DelegationResultDocument) is { Version: 1 },
        _ => false,
    };

    private void EnsureDelegationReturnWake(SessionId session, RunId run, Delegation delegation,
        IReadOnlyList<DomainEvent> journal)
    {
        lock (_modeAuthorityMutationGate)
        {
        journal = _store.ReadFrom(session, 1);
        // Compiled workflow stages have their own typed supervisor/result disposition. They
        // are not user-authorized ORQ delegations and must not wake the conversational root.
        if (ReadWorkflowStageForTask(session, run, delegation.ChildTaskId) is not null) return;
        var returnedEvent = journal.Where(evt => evt.RunId == run)
            .SingleOrDefault(evt => _codecs.Decode(evt) is DelegationReturned returned
                && returned.DelegationId == delegation.DelegationId)
            ?? throw new InvalidOperationException("Delegation return is not durable.");
        var existing = journal.Select(_codecs.Decode).OfType<WakeRequestCreated>()
            .Where(wake => wake.ExecutionId == delegation.ParentExecutionId
                && wake.Request.SourceEvent == new EvidenceEventRef(session, returnedEvent.EventId))
            .ToArray();
        if (existing.Length > 1) throw new InvalidDataException("Delegation return has duplicate wake requests.");
        if (existing.Length == 1) return;
        var rootScope = AgentScope(run, journal.Where(evt => evt.RunId == run).Select(_codecs.Decode),
            delegation.ParentExecutionId);
        var request = new WakeRequest(WakeRequestId.New(), delegation.ParentExecutionId,
            new EvidenceEventRef(session, returnedEvent.EventId),
            "Managed delegation returned a durable result; resume the unchanged root objective with this result as data.",
            1);
        using var attributed = ExecutionScope.Begin(rootScope);
        new EventStream(_store, _codecs, session).AppendBatch(
            [new WakeRequestCreated(delegation.ParentExecutionId, request)], DurabilityClass.Barrier,
            [rootScope], [new EventCausation(returnedEvent.EventId)]);
        }
    }

    private static bool MatchesCreatedEvent(LaneWorktreeCreated evt, WorktreeIdentity identity) =>
        evt.OwnershipId == identity.OwnershipId && evt.LaneId == identity.LaneId
        && SamePhysicalPath(evt.RepositoryRoot, identity.RepositoryRoot)
        && SamePhysicalPath(evt.GitCommonDirectory, identity.GitCommonDirectory)
        && SamePhysicalPath(evt.WorktreePath, identity.WorktreePath)
        && evt.SnapshotCommit == identity.SnapshotCommit && evt.SnapshotTree == identity.SnapshotTree;

    private VerifiedImplementationSource? ResolveVerifiedImplementationSource(SessionId session, RunId run,
        WorkflowStageContract verifyContract, IReadOnlyList<DomainEvent> journal,
        IReadOnlyList<DomainEventPayload> facts, string physicalRepositoryRoot, string workspaceDataRoot,
        CancellationToken cancellationToken)
    {
        if (verifyContract.Definition.Stage != WorkflowStage.Verify) return null;
        var plan = PlanProjection.Replay(_codecs, journal);
        var verifyItem = plan.Items().SingleOrDefault(item => WorkflowStageContract.TryFromPlanItem(item, out var parsed)
            && parsed!.Task == verifyContract.Task);
        if (verifyItem is null || verifyItem.DependsOn.Count != 1
            || !WorkflowStageContract.IsCanonicalAdmission(verifyContract, verifyItem, session, run,
                journal, _codecs, _artifacts)) return null;
        var implementItem = plan.Item(verifyItem.DependsOn[0]);
        if (implementItem is null || implementItem.State != PlanItemState.Completed
            || !WorkflowStageContract.TryFromPlanItem(implementItem, out var implementContract)
            || implementContract!.Definition.Stage != WorkflowStage.Implement
            || implementContract.Workflow != verifyContract.Workflow
            || implementContract.Instance != verifyContract.Instance
            || implementContract.Execution is not { } implementExecution
            || !WorkflowStageContract.IsCanonicalAdmission(implementContract, implementItem, session, run,
                journal, _codecs, _artifacts)) return null;

        var sources = facts.OfType<LaneWorktreeCreated>()
            .Where(value => value.LaneId == implementContract.Lane && value.TaskId == implementContract.Task).ToArray();
        if (sources.Length != 1) return null;
        var sourceEvent = sources[0];
        var completed = journal.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(pair => pair.Payload is TaskCompleted item && item.TaskId == implementContract.Task).ToArray();
        var sourceEnvelope = journal.SingleOrDefault(evt => _codecs.Decode(evt) is LaneWorktreeCreated created
            && created == sourceEvent);
        if (completed.Length != 1 || sourceEvent.ExecutionId != implementExecution
            || sourceEnvelope is null || sourceEnvelope.Sequence >= completed[0].Event.Sequence)
            return null;
        var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
            .LoadIdentityAsync(physicalRepositoryRoot, workspaceDataRoot, sourceEvent.OwnershipId,
                cancellationToken).GetAwaiter().GetResult();
        if (!loaded.Succeeded || loaded.Worktree is not { } identity || !MatchesCreatedEvent(sourceEvent, identity))
            return null;
        var preview = new GitWorktreeStore(SystemProcessRuntime.Instance())
            .PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity), cancellationToken)
            .GetAwaiter().GetResult();
        if (!preview.Succeeded || preview.Preview is not { } capture) return null;
        var binding = new WorkflowImplementationBinding(verifyContract.Workflow.Id,
            verifyContract.Workflow.Version, verifyContract.Instance,
            implementContract.Task, implementContract.Lane, implementExecution, identity.OwnershipId,
            capture.TheirsTree, capture.LaneHeadAtCapture, capture.LaneBranchAtCapture);
        return new(sourceEvent, identity, binding);
    }

    private WorkflowImplementationBinding? RecaptureImplementationBinding(VerifiedImplementationSource source,
        string physicalRepositoryRoot, CancellationToken cancellationToken)
    {
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var loaded = store.LoadIdentityAsync(physicalRepositoryRoot, source.Identity.WorkspaceDataRoot,
            source.Identity.OwnershipId, cancellationToken).GetAwaiter().GetResult();
        if (!loaded.Succeeded || loaded.Worktree is not { } identity
            || !MatchesCreatedEvent(source.OriginalEvent, identity)) return null;
        var preview = store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity), cancellationToken)
            .GetAwaiter().GetResult();
        if (!preview.Succeeded || preview.Preview is not { } capture) return null;
        return source.Binding with
        {
            Tree = capture.TheirsTree,
            Head = capture.LaneHeadAtCapture,
            Branch = capture.LaneBranchAtCapture,
        };
    }

    internal bool VerifyAcceptedImplementationBinding(WorkflowStageContract verifyContract,
        SessionId session, RunId run, IReadOnlyList<DomainEvent> journal, CancellationToken cancellationToken)
    {
        try
        {
            static bool RejectBinding(string _) => false;
            if (verifyContract.Definition.Stage != WorkflowStage.Verify
                || verifyContract.Execution is not { } verifyExecution) return RejectBinding("stage-or-execution");
            var plan = PlanProjection.Replay(_codecs, journal.Where(evt => evt.RunId == run).ToArray());
            var verifyItem = plan.Items().SingleOrDefault(item => WorkflowStageContract.TryFromPlanItem(item,
                out var parsed) && parsed!.Task == verifyContract.Task);
            if (verifyItem is null || verifyItem.DependsOn.Count != 1) return RejectBinding("plan-link");
            var implementItem = plan.Item(verifyItem.DependsOn[0]);
            if (implementItem is null || implementItem.State != PlanItemState.Completed
                || !WorkflowStageContract.TryFromPlanItem(implementItem, out var implementContract)
                || implementContract!.Definition.Stage != WorkflowStage.Implement
                || implementContract.Workflow != verifyContract.Workflow
                || implementContract.Instance != verifyContract.Instance
                || implementContract.Execution is not { } implementExecution
                || !WorkflowStageContract.IsCanonicalAdmission(implementContract, implementItem, session, run,
                    journal, _codecs, _artifacts)) return RejectBinding("implement-contract");

            var facts = journal.Where(evt => evt.SessionId == session && evt.RunId == run)
                .Select(_codecs.Decode).ToArray();
            var original = facts.OfType<LaneWorktreeCreated>().Where(evt => evt.LaneId == implementContract.Lane
                && evt.TaskId == implementContract.Task && evt.ExecutionId == implementExecution).ToArray();
            var bound = facts.OfType<LaneWorktreeBound>().Where(evt => evt.LaneId == verifyContract.Lane
                && evt.TaskId == verifyContract.Task && evt.ExecutionId == verifyExecution).ToArray();
            var verifyTask = facts.OfType<TaskCreated>().SingleOrDefault(evt => evt.TaskId == verifyContract.Task);
            var verifyDelegation = facts.OfType<DelegationCreated>().Select(evt => evt.Delegation)
                .SingleOrDefault(evt => evt.ChildTaskId == verifyContract.Task && evt.ChildLaneId == verifyContract.Lane);
            if (original.Length != 1 || bound.Length != 1 || verifyTask is null || verifyDelegation is null
                ) return RejectBinding("event-count-or-target-facts");
            var identity = LoadIdentityForRecovery(session, original[0], cancellationToken);
            var worktreeBinding = bound[0];
            if (identity is null || !MatchesCreatedEvent(original[0], identity)
                || !MatchesBoundEvent(worktreeBinding, identity, original[0],
                    new WorkflowImplementationBinding(verifyContract.Workflow.Id, verifyContract.Workflow.Version,
                        verifyContract.Instance, implementContract.Task, implementContract.Lane, implementExecution,
                        original[0].OwnershipId, worktreeBinding.SourceTree, worktreeBinding.SourceHead,
                        worktreeBinding.SourceBranch))) return RejectBinding("owner-or-binding-event");
            var boundPacket = ReadTaskPacket(worktreeBinding.TaskPacketRef);
            var binding = boundPacket?.WorkflowImplementation;
            if (boundPacket is null || !MatchesTaskPacket(worktreeBinding, boundPacket, session, run,
                    verifyTask, verifyDelegation, _artifacts)
                || binding is null || binding.WorkflowId != verifyContract.Workflow.Id
                || binding.WorkflowVersion != verifyContract.Workflow.Version
                || binding.Instance != verifyContract.Instance || binding.TaskId != implementContract.Task
                || binding.LaneId != implementContract.Lane || binding.ExecutionId != implementExecution
                || binding.OwnershipId != original[0].OwnershipId) return RejectBinding("task-packet");
            var preview = new GitWorktreeStore(SystemProcessRuntime.Instance())
                .PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity), cancellationToken)
                .GetAwaiter().GetResult();
            return preview.Succeeded && preview.Preview is { } current
                && binding.Tree == current.TheirsTree && binding.Head == current.LaneHeadAtCapture
                && binding.Branch == current.LaneBranchAtCapture
                && worktreeBinding.SourceTree == current.TheirsTree
                && worktreeBinding.SourceHead == current.LaneHeadAtCapture
                && worktreeBinding.SourceBranch == current.LaneBranchAtCapture
                || RejectBinding("source-tree-recapture");
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private WorktreeIdentity? LoadIdentityForRecovery(SessionId session, LaneWorktreeCreated created,
        CancellationToken cancellationToken)
    {
        var verifiedRoot = VerifiedWorkspaceRoot(session);
        if (verifiedRoot is null) return null;
        var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(verifiedRoot);
        var dataRoot = OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), physicalRoot);
        var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
            .LoadIdentityAsync(physicalRoot, dataRoot, created.OwnershipId, cancellationToken)
            .GetAwaiter().GetResult();
        return loaded.Succeeded ? loaded.Worktree : null;
    }

    private TaskPacket? ReadTaskPacket(ArtifactRef reference)
    {
        if (_artifacts is null || reference.Kind != ArtifactKind.Other
            || reference.MediaType != LaneWorktreeProvisioner.TaskPacketMediaType
            || reference.Sensitivity != Sensitivity.Sensitive || reference.Size is <= 0 or > LaneWorktreeProvisioner.MaximumTaskPacketBytes
            || !_artifacts.Verify(reference.Hash, reference.Size)) return null;
        var json = _artifacts.GetText(reference.Hash);
        if (json is null || System.Text.Encoding.UTF8.GetByteCount(json) > LaneWorktreeProvisioner.MaximumTaskPacketBytes)
            return null;
        return JsonSerializer.Deserialize(json, TaskPacketJsonContext.Default.TaskPacket);
    }

    private static bool MatchesBoundEvent(LaneWorktreeBound bound, WorktreeIdentity identity,
        LaneWorktreeCreated original, WorkflowImplementationBinding workflow) =>
        bound.LaneId != identity.LaneId && bound.OwnerLaneId == original.LaneId
        && bound.OwnerTaskId == original.TaskId && bound.OwnerExecutionId == original.ExecutionId
        && bound.OwnershipId == original.OwnershipId && bound.OwnershipId == identity.OwnershipId
        && SamePhysicalPath(bound.RepositoryRoot, identity.RepositoryRoot)
        && SamePhysicalPath(bound.GitCommonDirectory, identity.GitCommonDirectory)
        && SamePhysicalPath(bound.WorktreePath, identity.WorktreePath)
        && bound.SnapshotCommit == identity.SnapshotCommit && bound.SnapshotTree == identity.SnapshotTree
        && bound.WorkflowId == workflow.WorkflowId && bound.WorkflowVersion == workflow.WorkflowVersion
        && bound.WorkflowInstance == workflow.Instance && bound.SourceTree == workflow.Tree
        && bound.SourceHead == workflow.Head && bound.SourceBranch == workflow.Branch;

    private static bool MatchesTaskPacket(LaneWorktreeCreated evt, TaskPacket packet, SessionId session,
        RunId run, TaskCreated task, Delegation delegation, IArtifactStore? artifacts) =>
        packet.SchemaVersion == 1 && packet.SessionId == session && packet.RunId == run
        && packet.TaskId == task.TaskId && packet.LaneId == evt.LaneId
        && packet.ExecutionId == evt.ExecutionId && packet.DelegationId == delegation.DelegationId
        && packet.ParentExecutionId == delegation.ParentExecutionId && packet.ProfileId == delegation.ProfileId
        && packet.Budget == task.Budget
        && packet.OwnershipId == evt.OwnershipId
        && SamePhysicalPath(packet.RepositoryRoot, evt.RepositoryRoot)
        && SamePhysicalPath(packet.GitCommonDirectory, evt.GitCommonDirectory)
        && SamePhysicalPath(packet.WorktreePath, evt.WorktreePath)
        && packet.SnapshotCommit == evt.SnapshotCommit && packet.SnapshotTree == evt.SnapshotTree
        && packet.Base == WorktreeBase.SnapshotOfWorkingTree
        && packet.SelectedProjectionRef == delegation.PacketRef
        && artifacts is not null && artifacts.Verify(evt.TaskPacketRef.Hash, evt.TaskPacketRef.Size)
        && evt.TaskPacketRef.Kind == ArtifactKind.Other
        && evt.TaskPacketRef.MediaType == LaneWorktreeProvisioner.TaskPacketMediaType
        && evt.TaskPacketRef.Sensitivity == Sensitivity.Sensitive
        && evt.TaskPacketRef.Size is > 0 and <= LaneWorktreeProvisioner.MaximumTaskPacketBytes;

    private static bool MatchesTaskPacket(LaneWorktreeBound evt, TaskPacket packet, SessionId session,
        RunId run, TaskCreated task, Delegation delegation, IArtifactStore? artifacts) =>
        packet.SchemaVersion == 1 && packet.SessionId == session && packet.RunId == run
        && packet.TaskId == task.TaskId && packet.LaneId == evt.LaneId
        && packet.ExecutionId == evt.ExecutionId && packet.DelegationId == delegation.DelegationId
        && packet.ParentExecutionId == delegation.ParentExecutionId && packet.ProfileId == delegation.ProfileId
        && packet.Budget == task.Budget && packet.OwnershipId == evt.OwnershipId
        && SamePhysicalPath(packet.RepositoryRoot, evt.RepositoryRoot)
        && SamePhysicalPath(packet.GitCommonDirectory, evt.GitCommonDirectory)
        && SamePhysicalPath(packet.WorktreePath, evt.WorktreePath)
        && packet.SnapshotCommit == evt.SnapshotCommit && packet.SnapshotTree == evt.SnapshotTree
        && packet.Base == WorktreeBase.SnapshotOfWorkingTree
        && packet.WorkflowImplementation is { } workflow
        && workflow.WorkflowId == evt.WorkflowId && workflow.WorkflowVersion == evt.WorkflowVersion
        && workflow.Instance == evt.WorkflowInstance && workflow.TaskId == evt.OwnerTaskId
        && workflow.LaneId == evt.OwnerLaneId && workflow.ExecutionId == evt.OwnerExecutionId
        && workflow.OwnershipId == evt.OwnershipId && workflow.Tree == evt.SourceTree
        && workflow.Head == evt.SourceHead && workflow.Branch == evt.SourceBranch
        && packet.SelectedProjectionRef == delegation.PacketRef
        && artifacts is not null && artifacts.Verify(evt.TaskPacketRef.Hash, evt.TaskPacketRef.Size)
        && evt.TaskPacketRef.Kind == ArtifactKind.Other
        && evt.TaskPacketRef.MediaType == LaneWorktreeProvisioner.TaskPacketMediaType
        && evt.TaskPacketRef.Sensitivity == Sensitivity.Sensitive
        && evt.TaskPacketRef.Size is > 0 and <= LaneWorktreeProvisioner.MaximumTaskPacketBytes;

    private static bool SamePhysicalPath(string left, string right) => string.Equals(
        ProjectIdentity.CanonicalWorkspacePath(left), ProjectIdentity.CanonicalWorkspacePath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal bool FailDeniedNativeWeakWorkflowContinuation(WorkflowRuntimeAuthorization authorization,
        WeakSandboxWorkflowContinuation continuation)
    {
        if (continuation.OptionId != "deny" || authorization.InvocationCommand.ToString() != continuation.InvocationId
            || !Guid.TryParseExact(continuation.TaskId, "D", out var taskGuid)
            || !Guid.TryParseExact(continuation.LaneId, "D", out var laneGuid)
            || !Guid.TryParseExact(continuation.TurnId, "D", out var turnGuid)
            || !Guid.TryParseExact(continuation.ToolCallId, "D", out var toolCallGuid)
            || !Guid.TryParseExact(continuation.ResponseCommandId, "D", out var responseCommandGuid)) return false;
        var taskId = new TaskId(taskGuid);
        var laneId = new LaneId(laneGuid);
        var turnId = new TurnId(turnGuid);
        var toolCallId = new ToolCallId(toolCallGuid);
        lock (_modeAuthorityMutationGate)
        {
            if (!WeakSandboxWorkflowAuthorizationIsCurrent(authorization)) return false;
            ReconcileDurableMailboxReceipts(authorization.Session, authorization.Run);
            var journal = _store.ReadFrom(authorization.Session, 1);
            var found = FindWorkflowStage(ForStage(authorization, WorkflowStage.Verify, null),
                WorkflowStage.Verify, allowAwaitingWeakSandboxConsent: true);
            if (found is not { } stage || stage.Phase != PreM6RecordPhase.Accepted
                || stage.Delegation.ChildTaskId != taskId || stage.Delegation.ChildLaneId != laneId
                || !TryMatchOpenWeakSandboxCall(authorization.Session, authorization.Run, stage.Delegation,
                    journal, continuation)) return false;
            var runEvents = journal.Where(evt => evt.RunId == authorization.Run).ToArray();
            if (RunProjection.Replay(authorization.Session, authorization.Run, _codecs, runEvents).State
                    != RunState.Running
                || OmniCliRuntime.FindOpenTurnStart(runEvents, _codecs, authorization.Run, laneId)?.TurnId != turnId)
                return false;
            var request = journal.SingleOrDefault(evt => evt.RunId == authorization.Run
                && evt.TaskId == taskId && evt.LaneId == laneId && evt.TurnId == turnId
                && evt.ToolCallId == toolCallId && _codecs.Decode(evt) is InteractionRequested item
                && item.InteractionId.ToString() == continuation.InteractionId
                && item.Kind == InteractionKind.WeakSandboxConsent);
            if (request?.ExecutionId is not { } executionId
                || runEvents.Select(_codecs.Decode).OfType<AgentExecutionStarted>().SingleOrDefault(item =>
                    item.ExecutionId == executionId && item.LaneId == laneId
                    && item.ParentExecutionId == stage.Delegation.ParentExecutionId
                    && item.ProfileId == stage.Delegation.ProfileId) is null)
                return false;
            var scope = new ExecutionScopeState(authorization.Run, taskId, laneId, turnId, toolCallId, executionId);
            return FailOwnedDelegationCore(authorization.Session, authorization.Run, stage.Delegation,
                scope, stage.Delegation.ProfileId, cancelled: false, deniedTurn: turnId,
                causeCommandId: new CommandId(responseCommandGuid));
        }
    }

    private void FailOwnedDelegation(DelegationWork work, bool cancelled)
    {
        lock (_modeAuthorityMutationGate)
        {
            ReconcileDurableMailboxReceipts(work.Session, work.Run);
            _ = FailOwnedDelegationCore(work.Session, work.Run, work.Delegation, work.Scope,
                work.Profile.Id, cancelled, deniedTurn: null, causeCommandId: null);
        }
    }

    private bool FailOwnedDelegationCore(SessionId session, RunId run, Delegation delegation,
        ExecutionScopeState scope, ProfileId profileId, bool cancelled, TurnId? deniedTurn,
        CommandId? causeCommandId)
    {
        var journal = _store.ReadFrom(session, 1);
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        if (records.Records["delegation:" + delegation.DelegationId].Phase != PreM6RecordPhase.Accepted) return false;
        // Preserve an unsettled provider/tool receipt as recovery evidence; never falsely settle it.
        var own = journal.Where(e => e.RunId == run).ToArray();
        // A sibling may still be inside its provider/tool boundary. Preserve uncertain
        // receipts for this execution only; run-wide ownership would strand a cancelled
        // child Accepted merely because another Lane remains active.
        if (HasOpenModelStep(own, scope.LaneId!) || HasOpenToolCall(own, scope.LaneId!)) return false;
        if (deniedTurn is { } exactDeniedTurn
            && (scope.TurnId != exactDeniedTurn
                || OmniCliRuntime.FindOpenTurnStart(own, _codecs, run, scope.LaneId!)?.TurnId != exactDeniedTurn
                || own.Any(evt => evt.TurnId == exactDeniedTurn && _codecs.Decode(evt) is TurnAbandoned)))
            return false;
        var facts = own.Select(_codecs.Decode).ToArray();
        var childId = scope.ExecutionId!;
        var bindingHistory = records.Records.Values.Where(history => history.OwnerExecutionId == childId)
            .SelectMany(history => history.Facts.OfType<SupervisionBindingCreated>()).SingleOrDefault();
        if (bindingHistory is not null)
        {
            var binding = bindingHistory.Binding;
            var phase = records.Records["binding:" + binding.BindingId].Phase;
            if (phase == PreM6RecordPhase.Failed) return false;
            if (binding.FailurePolicy == SupervisionFailurePolicy.Wait
                && (phase == PreM6RecordPhase.Created
                    || IsExecutionTerminal(facts, delegation.ParentExecutionId)))
            {
                if (IsExecutionTerminal(facts, delegation.ParentExecutionId))
                    ReconcileTerminatedSupervisors(session, run);
                else RecordWaitBindingFailure(session, run, binding,
                    "Supervisor handshake failed; Wait policy keeps the join unresolved.", cause: null);
                return false;
            }
        }
        var taskState = TaskGraphProjection.Replay(_codecs, own).Get(scope.TaskId!)!.State;
        var laneState = LaneProjection.Replay(_codecs, own).StateOf(scope.LaneId!)!.Value;
        var childEvents = new List<DomainEventPayload>();
        if (deniedTurn is { } turnToAbandon)
        {
            var pendingSteering = SteeringQueue.Pending(_store, _codecs, session, run,
                scope.LaneId!, turnToAbandon);
            childEvents.AddRange(SteeringQueue.DropEvents(pendingSteering, run, scope.LaneId!, turnToAbandon,
                "turn ended without another model step"));
            childEvents.Add(new TurnAbandoned(turnToAbandon, "Weak sandbox consent was denied by the user."));
        }
        if (!StateMachines.IsTaskTerminal(taskState)) childEvents.Add(cancelled
            ? new TaskCancelled(scope.TaskId!) : new TaskFailed(scope.TaskId!, "Child execution failed"));
        if (!StateMachines.IsLaneTerminal(laneState)) childEvents.Add(cancelled
            ? new LaneCancelled(scope.LaneId!) : new LaneFailed(scope.LaneId!, "Child execution failed"));
        // Keep the legacy failure event envelope unchanged. The native denied child carries
        // the exact Turn/ToolCall only for its Turn-scoped terminal; execution/task/lane facts
        // use the canonical child scope without those conversational IDs.
        var childScope = deniedTurn is null ? scope : scope with { TurnId = null, ToolCallId = null };
        var batch = new List<DomainEventPayload>();
        if (deniedTurn is not null)
            batch.AddRange(childEvents.Where(evt => evt is TurnSteeringDropped or TurnAbandoned));
        batch.Add(new AgentExecutionFailed(childId, scope.LaneId!, profileId,
            delegation.ParentExecutionId, delegation.Relation, delegation.Supervision));
        batch.AddRange(childEvents.Where(evt => evt is not (TurnSteeringDropped or TurnAbandoned)));
        batch.Add(new DelegationFailed(delegation.ParentExecutionId, delegation.DelegationId,
            cancelled ? "Explicit cancellation or elapsed limit" : "Child execution failed"));
        var scopes = batch.Take(batch.Count - 1)
            .Select(evt => (ExecutionScopeState?)(evt is TurnAbandoned or TurnSteeringDropped ? scope : childScope))
            .Append(AgentScope(run, facts, delegation.ParentExecutionId)).ToArray();
        using var cause = causeCommandId is { } commandId
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
        new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Barrier, scopes);
        return true;
    }
}
