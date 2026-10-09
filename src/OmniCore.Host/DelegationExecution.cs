using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

internal sealed record DelegationWork(SessionId Session, RunId Run, Delegation Delegation,
    ExecutionScopeState Scope, AgentProfile Profile, string Objective, TaskBudget Budget);

public sealed partial class OmniServer
{
    internal Delegation ReadCurrentDelegation(DelegationId id)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run) throw new InvalidOperationException("No active Run.");
        return _store.ReadFrom(session, 1).Where(e => e.RunId == run).Select(_codecs.Decode)
            .OfType<DelegationCreated>().Single(e => e.Delegation.DelegationId == id).Delegation;
    }

    /// <summary>Trusted Host composition only. FIFO, one actual slot; every provider/tool boundary
    /// still passes Explorer's routing, profile and hierarchical budget guards.</summary>
    internal CommandAck ExecuteDelegation(DelegationId id,
        Func<DelegationWork, CancellationToken, ExplorerTurn.TurnResult> execute, CancellationToken token,
        bool waitForCapacity = false)
    {
        var session = _lastSessionId ?? throw new InvalidOperationException("No Session.");
        var run = _lastRunId ?? throw new InvalidOperationException("No Run.");
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        var before = _store.CurrentSequence(session);
        using var cause = CausationScope.Begin(new CommandCausation(commandId));
        AgentCapacity.Lease? capacity = null;
        DelegationWork? work = null;
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
            if (initialAuthorization.Limits.MaxAgents < 2)
                return Ack(RuntimeCommandOutcome.Deferred("AgentOrDepthLimit"));
            var admittedProfile = ResolveLaneAgentProfile(session, run, initialDelegation.ChildLaneId);
            if (admittedProfile is null) return Ack(RuntimeCommandOutcome.Deferred("ChildProfileUnavailable"));
            var readOnly = AgentPermissionScopeSubset.IsReadOnly(admittedProfile.PermissionCeiling);
            var durableSequence = initial.Where(evt => evt.RunId == run)
                .Single(evt => _codecs.Decode(evt) is DelegationCreated created
                    && created.Delegation.DelegationId == id).Sequence;
            var scheduler = AgentCapacity.For(_store);
            capacity = waitForCapacity
                ? scheduler.Acquire(session, run, id, initialAuthorization.Limits.MaxAgents,
                    initialDelegation.Priority, readOnly, token, durableSequence)
                : scheduler.TryAcquire(session, run, id, initialAuthorization.Limits.MaxAgents,
                    initialDelegation.Priority, readOnly, token, durableSequence);
            if (capacity is null) return Ack(RuntimeCommandOutcome.Deferred("WaitingForCapacity"));
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
                if (HasOpenModelStep(own, supervisor.LaneId) || HasOpenToolCall(own, supervisor.LaneId)
                    || HasOpenModelStep(own, delegation.ChildLaneId) || HasOpenToolCall(own, delegation.ChildLaneId))
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
                else
                {
                    if (history.Facts.OfType<DelegationCancellationRequested>().Any())
                        return Ack(RuntimeCommandOutcome.Deferred("CancellationRecoveryRequired"));
                    // Only a settled, suspended Turn can be resumed; an unowned live invocation is not retried.
                    if (OmniCliRuntime.FindOpenTurnStart(own, _codecs, run, delegation.ChildLaneId) is null)
                        return Ack(RuntimeCommandOutcome.Deferred("ExecutionRecoveryRequired"));
                }
                var profile = ResolveLaneAgentProfile(session, run, delegation.ChildLaneId)
                    ?? throw new InvalidOperationException("Child profile unavailable.");
                var parentScope = AgentScope(run, facts, delegation.ParentExecutionId);
                var parentProfile = ResolveLaneAgentProfile(session, run, parentScope.LaneId!)
                    ?? throw new InvalidOperationException("Parent profile unavailable.");
                if (!AgentPermissionScopeSubset.IsSubset(profile.PermissionCeiling, parentProfile.PermissionCeiling))
                    return Ack(RuntimeCommandOutcome.Deferred("ChildPermissionCeilingExceedsParent"));
                var task = facts.OfType<TaskCreated>().Single(e => e.TaskId == delegation.ChildTaskId);
                var executionId = history.Phase == PreM6RecordPhase.Created ? ExecutionId.New()
                    : history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                var scope = new ExecutionScopeState(run, task.TaskId, delegation.ChildLaneId, ExecutionId: executionId);
                work = new(session, run, delegation, scope, profile, task.Objective, task.Budget);
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
                    var started = new AgentExecutionStarted(executionId, delegation.ChildLaneId, delegation.ProfileId,
                        delegation.ParentExecutionId, delegation.Relation, delegation.Supervision);
                    var binding = new SupervisionBinding(BindingId.New(), executionId, delegation.ParentExecutionId, 1);
                    var mailbox = new ExecutionMailbox(MailboxId.New(), executionId);
                    var admissionEvents = new List<DomainEventPayload> {
                        new TaskStarted(task.TaskId, delegation.ChildLaneId), new LaneProvisioning(delegation.ChildLaneId), new LaneStarted(delegation.ChildLaneId), started,
                        new SupervisionBindingCreated(executionId, binding),
                        new ExecutionMailboxCreated(executionId, mailbox),
                        new DelegationAccepted(delegation.ParentExecutionId, id, executionId),
                    };
                    admissionEvents.AddRange(BindWorkflowStageExecution(session, run, task.TaskId,
                        delegation.ChildLaneId, executionId));
                    var scopes = admissionEvents.Select(evt => evt is DelegationAccepted
                        ? (ExecutionScopeState?)parentScope : scope).ToArray();
                    new EventStream(_store, _codecs, session).AppendBatch(admissionEvents,
                        DurabilityClass.Barrier, scopes);
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
                var value = new DelegationResultDocument(1, "Succeeded", summary, [], issues);
                var prepared = ((IArtifactPreparationStore)_artifacts!).PrepareText(JsonSerializer.Serialize(value, DelegationResultJson.Default.DelegationResultDocument),
                    "application/vnd.omnicore.agent-result+json", ArtifactKind.Other, Sensitivity.Sensitive);
                using var lease = ((IArtifactPublicationLease)_artifacts!).AcquirePublicationLease(CancellationToken.None);
                if (prepared.Publish() != prepared.Reference) throw new InvalidDataException("Result publication reference changed.");
                var idExecution = work.Scope.ExecutionId!;
                var parentScope = AgentScope(run, facts, work.Delegation.ParentExecutionId);
                new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[] {
                    new AgentExecutionCompleted(idExecution, work.Scope.LaneId!, work.Profile.Id, work.Delegation.ParentExecutionId,
                        work.Delegation.Relation, work.Delegation.Supervision),
                    new AgentResultProduced(idExecution, prepared.Reference, 1, "core.explorer.v1"),
                    new DelegationReturned(work.Delegation.ParentExecutionId, id, prepared.Reference),
                }, DurabilityClass.Barrier, new ExecutionScopeState?[] { work.Scope, work.Scope, parentScope });
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
        finally { capacity?.Dispose(); }
        CommandAck Ack(RuntimeCommandOutcome outcome) => CommandOutcomeAck(messageId, "ok", null, outcome, session, before, commandId);
    }

    private void FailOwnedDelegation(DelegationWork work, bool cancelled)
    {
        lock (_modeAuthorityMutationGate)
        {
            ReconcileDurableMailboxReceipts(work.Session, work.Run);
            var journal = _store.ReadFrom(work.Session, 1);
            var records = PreM6RecordProjection.Replay(work.Session, _codecs, journal);
            if (records.Records["delegation:" + work.Delegation.DelegationId].Phase != PreM6RecordPhase.Accepted) return;
            // Preserve an unsettled provider/tool receipt as recovery evidence; never falsely settle it.
            var own = journal.Where(e => e.RunId == work.Run).ToArray();
            if (HasOpenModelStep(own) || HasOpenToolCall(own)) return;
            var facts = own.Select(_codecs.Decode).ToArray();
            var childId = work.Scope.ExecutionId!;
            var bindingHistory = records.Records.Values.Where(history => history.OwnerExecutionId == childId)
                .SelectMany(history => history.Facts.OfType<SupervisionBindingCreated>()).SingleOrDefault();
            if (bindingHistory is not null)
            {
                var binding = bindingHistory.Binding;
                var phase = records.Records["binding:" + binding.BindingId].Phase;
                if (phase == PreM6RecordPhase.Failed) return;
                if (binding.FailurePolicy == SupervisionFailurePolicy.Wait
                    && (phase == PreM6RecordPhase.Created
                        || IsExecutionTerminal(facts, work.Delegation.ParentExecutionId)))
                {
                    if (IsExecutionTerminal(facts, work.Delegation.ParentExecutionId))
                        ReconcileTerminatedSupervisors(work.Session, work.Run);
                    else RecordWaitBindingFailure(work.Session, work.Run, binding,
                        "Supervisor handshake failed; Wait policy keeps the join unresolved.", cause: null);
                    return;
                }
            }
            var taskState = TaskGraphProjection.Replay(_codecs, own).Get(work.Scope.TaskId!)!.State;
            var laneState = LaneProjection.Replay(_codecs, own).StateOf(work.Scope.LaneId!)!.Value;
            var childEvents = new List<DomainEventPayload>();
            if (!StateMachines.IsTaskTerminal(taskState)) childEvents.Add(cancelled
                ? new TaskCancelled(work.Scope.TaskId!) : new TaskFailed(work.Scope.TaskId!, "Child execution failed"));
            if (!StateMachines.IsLaneTerminal(laneState)) childEvents.Add(cancelled
                ? new LaneCancelled(work.Scope.LaneId!) : new LaneFailed(work.Scope.LaneId!, "Child execution failed"));
            var batch = new List<DomainEventPayload>();
            batch.Add(new AgentExecutionFailed(childId, work.Scope.LaneId!, work.Profile.Id,
                work.Delegation.ParentExecutionId, work.Delegation.Relation, work.Delegation.Supervision));
            batch.AddRange(childEvents);
            batch.Add(new DelegationFailed(work.Delegation.ParentExecutionId, work.Delegation.DelegationId,
                cancelled ? "Explicit cancellation or elapsed limit" : "Child execution failed"));
            var scopes = Enumerable.Repeat<ExecutionScopeState?>(work.Scope, batch.Count - 1)
                .Append(AgentScope(work.Run, facts, work.Delegation.ParentExecutionId)).ToArray();
            new EventStream(_store, _codecs, work.Session).AppendBatch(batch, DurabilityClass.Barrier, scopes);
        }
    }
}
