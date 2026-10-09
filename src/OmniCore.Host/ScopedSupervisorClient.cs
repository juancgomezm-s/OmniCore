using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Unserialized process capability. The supervisor identity is fixed when the client is created.</summary>
internal sealed class ScopedSupervisorClient(OmniServer server, SessionId session, RunId run,
    ExecutionId supervisorExecutionId) : IOmniClient
{
    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        => server.SendSupervisorCommand(session, run, supervisorExecutionId, command, cancellationToken);

    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
        => server.SubscribeSupervisorSince(session, run, supervisorExecutionId, fromSequence);

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
        => server.QuerySupervisor(session, run, supervisorExecutionId, name, cancellationToken);
}

public sealed partial class OmniServer
{
    internal IOmniClient CreateScopedSupervisorClient(ExecutionId supervisorExecutionId)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            throw new InvalidOperationException("No active Run for supervision.");
        EnsureSupervisor(session, run, supervisorExecutionId);
        return new ScopedSupervisorClient(this, session, run, supervisorExecutionId);
    }

    internal CommandAck SendSupervisorCommand(SessionId session, RunId run, ExecutionId supervisor,
        WireEnvelope command, CancellationToken token)
    {
        if (command.MessageType != MessageTypes.Command || !Guid.TryParse(command.MessageId, out var commandGuid))
            return new(command.MessageId, "error", "Invalid supervisor command envelope", RuntimeCommandOutcome.Rejected());
        string? name;
        try
        {
            using var document = JsonDocument.Parse(command.PayloadJson);
            name = document.RootElement.TryGetProperty("cmd", out var cmd) ? cmd.GetString() : null;
        }
        catch (JsonException)
        { return new(command.MessageId, "error", "Invalid supervisor command", RuntimeCommandOutcome.Rejected()); }
        if (name is not ("supervisor.binding.accept" or "supervisor.binding.fail"
            or "supervisor.result.disposition" or "supervisor.mailbox.send"))
            return new(command.MessageId, "error", "Supervisor command is outside its scoped capability", RuntimeCommandOutcome.Rejected());

        var before = _store.CurrentSequence(session);
        var commandId = new CommandId(commandGuid);
        try
        {
            using var causation = CausationScope.Begin(new CommandCausation(commandId));
            lock (_modeAuthorityMutationGate)
            {
                token.ThrowIfCancellationRequested();
                EnsureSupervisor(session, run, supervisor);
                var journal = _store.ReadFrom(session, 1);
                if (journal.Any(evt => evt.RunId == run && evt.Causation is CommandCausation cause && cause.CommandId == commandId))
                    return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before, commandId);
                using var doc = JsonDocument.Parse(command.PayloadJson);
                var input = doc.RootElement;
                var records = PreM6RecordProjection.Replay(session, _codecs, journal);
                var own = journal.Where(evt => evt.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                var stream = new EventStream(_store, _codecs, session);
                if (name == "supervisor.result.disposition")
                    ApplySupervisorDisposition(input, command, session, run, supervisor, commandId, before,
                        own, facts, records, stream);
                else if (name is "supervisor.binding.accept" or "supervisor.binding.fail")
                    ApplySupervisorBinding(input, name, session, run, supervisor, own, facts, records, stream);
                else
                    ApplySupervisorMailboxMessage(input, command, session, run, supervisor, commandId, before,
                        own, facts, records, stream);
                return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    session, before, commandId);
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or FormatException or IOException or JsonException)
        {
            return FailedDurableCommandAck(command.MessageId, session, before, failure.Message, restoreRunIdentity: false);
        }
    }

    private void ApplySupervisorBinding(JsonElement input, string name, SessionId session, RunId run, ExecutionId supervisor,
        IReadOnlyList<DomainEvent> own, DomainEventPayload[] facts, PreM6RecordProjection records, EventStream stream)
    {
        var child = new ExecutionId(Guid.Parse(input.GetProperty("executionId").GetString()!));
        var bindingId = new BindingId(Guid.Parse(input.GetProperty("bindingId").GetString()!));
        var binding = RequireBoundChild(records, facts, supervisor, child, requireAccepted: false);
        if (binding.BindingId != bindingId) throw new ArgumentException("Binding identity mismatch.");
        var history = records.Records["binding:" + bindingId];
        if (name == "supervisor.binding.accept")
        {
            if (history.Phase == PreM6RecordPhase.Accepted) return;
            if (history.Phase != PreM6RecordPhase.Created) throw new InvalidOperationException("Binding is not awaiting handshake.");
            var scope = AgentScope(run, facts, child);
            using var execution = ExecutionScope.Begin(scope);
            stream.Append(new SupervisionBindingAccepted(child, bindingId), DurabilityClass.Barrier);
            return;
        }
        var reason = input.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4096) throw new ArgumentException("Binding failure reason is required.");
        if (history.Phase == PreM6RecordPhase.Failed) return;
        if (history.Phase is not (PreM6RecordPhase.Created or PreM6RecordPhase.Accepted))
            throw new InvalidOperationException("Binding is not active.");
        RecordWaitBindingFailure(session, run, binding,
            new PiiRedactor().Redact(reason), cause: null);
    }

    /// <summary>Host policy for Wait supervision: fail the binding and visibly block the subject,
    /// while preserving its execution, delegation, and unresolved joins for explicit recovery.</summary>
    private bool RecordWaitBindingFailure(SessionId session, RunId run, SupervisionBinding binding,
        string reason, EventId? cause)
    {
        var journal = _store.ReadFrom(session, 1);
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        if (!records.Records.TryGetValue("binding:" + binding.BindingId, out var history)
            || history.Phase == PreM6RecordPhase.Failed) return false;
        if (history.Phase is not (PreM6RecordPhase.Created or PreM6RecordPhase.Accepted)
            || binding.FailurePolicy != SupervisionFailurePolicy.Wait) return false;
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        var child = AgentScope(run, facts, binding.SubjectExecutionId);
        var taskId = child.TaskId ?? throw new InvalidDataException("Supervised execution has no Task.");
        var laneId = child.LaneId ?? throw new InvalidDataException("Supervised execution has no Lane.");
        var task = TaskGraphProjection.Replay(_codecs, own).Get(taskId)
            ?? throw new InvalidDataException("Supervised Task is missing.");
        var laneState = LaneProjection.Replay(_codecs, own).StateOf(laneId)
            ?? throw new InvalidDataException("Supervised Lane is missing.");
        if (StateMachines.IsTaskTerminal(task.State) || StateMachines.IsLaneTerminal(laneState)) return false;

        var batch = new List<DomainEventPayload>
        {
            new SupervisionBindingFailed(binding.SubjectExecutionId, binding.BindingId,
                new PiiRedactor().Redact(reason)),
        };
        var scopes = new List<ExecutionScopeState?> { child };
        if (task.State != TaskState.Blocked)
        { batch.Add(new TaskBlocked(taskId, "SupervisionWait")); scopes.Add(child); }
        if (laneState != LaneState.Blocked)
        { batch.Add(new LaneBlocked(laneId, "SupervisionWait")); scopes.Add(child); }
        var causes = cause is null ? null
            : Enumerable.Repeat<CausationId?>(new EventCausation(cause), batch.Count).ToArray();
        using var execution = ExecutionScope.Begin(child);
        new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Barrier, scopes, causes);
        return true;
    }

    /// <summary>Reconcile dead supervisors from their canonical terminal event. The transition
    /// is caused by that event; the dead scoped client cannot grant continuation authority.</summary>
    private void ReconcileTerminatedSupervisors(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1);
        var terminalSupervisors = journal.Where(evt => evt.RunId == run)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(item => item.Payload is AgentExecutionCompleted or AgentExecutionFailed)
            .Select(item => (item.Event, Execution: item.Payload switch
            {
                AgentExecutionCompleted completed => completed.ExecutionId,
                AgentExecutionFailed failed => failed.ExecutionId,
                _ => throw new InvalidOperationException(),
            }))
            .GroupBy(item => item.Execution).Select(group => group.OrderByDescending(item => item.Event.Sequence).First())
            .ToArray();
        foreach (var terminal in terminalSupervisors)
        {
            lock (_modeAuthorityMutationGate)
            {
                var current = _store.ReadFrom(session, 1);
                var records = PreM6RecordProjection.Replay(session, _codecs, current);
                var bindings = records.Records.Values.Where(record => record.OwnerExecutionId is not null)
                    .SelectMany(record => record.Facts.OfType<SupervisionBindingCreated>())
                    .Where(created => created.Binding.SupervisorExecutionId == terminal.Execution
                        && created.Binding.FailurePolicy == SupervisionFailurePolicy.Wait)
                    .Select(created => created.Binding).DistinctBy(binding => binding.BindingId).ToArray();
                foreach (var binding in bindings)
                    RecordWaitBindingFailure(session, run, binding,
                        "Supervisor execution terminated; Wait policy keeps the join unresolved.", terminal.Event.EventId);
            }
        }
    }

    private void ApplySupervisorDisposition(JsonElement input, WireEnvelope command, SessionId session, RunId run,
        ExecutionId supervisor, CommandId commandId, long before, IReadOnlyList<DomainEvent> own,
        DomainEventPayload[] facts, PreM6RecordProjection records, EventStream stream)
    {
        var child = new ExecutionId(Guid.Parse(input.GetProperty("executionId").GetString()!));
        var resultId = input.GetProperty("resultId").GetString() ?? throw new ArgumentException("Result identity is required.");
        var outcome = Enum.Parse<ResultDispositionOutcome>(input.GetProperty("outcome").GetString()!, true);
        var reason = input.GetProperty("reason").GetString();
        if (outcome is not (ResultDispositionOutcome.Accepted or ResultDispositionOutcome.ReworkRequested)
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 4096)
            throw new ArgumentException("Supervisor may record only an explicit Accepted or ReworkRequested disposition.");
        var binding = RequireBoundChild(records, facts, supervisor, child);
        if (binding.FailurePolicy != SupervisionFailurePolicy.Wait)
            throw new InvalidOperationException("Unsupported supervisor failure policy.");
        var history = records.Records["delegation:" + facts.OfType<DelegationAccepted>()
            .Single(e => e.ChildExecutionId == child).DelegationId];
        if (history.Phase != PreM6RecordPhase.Returned) throw new InvalidOperationException("Child result has not returned.");
        var produced = facts.OfType<AgentResultProduced>().LastOrDefault(e => e.ExecutionId == child)
            ?? throw new InvalidDataException("Child result event is missing.");
        if (produced.ResultRef.Id.ToString() != resultId) throw new ArgumentException("Result identity is stale.");
        if (facts.OfType<ResultDispositionRecorded>().Any(e => e.Disposition.ResultRef == produced.ResultRef))
            throw new InvalidOperationException("This immutable result already has a disposition.");
        var result = ReadAgentResult(produced);
        var acceptedDelegation = facts.OfType<DelegationAccepted>().Single(e => e.ChildExecutionId == child);
        var delegation = facts.OfType<DelegationCreated>().Single(e => e.Delegation.DelegationId == acceptedDelegation.DelegationId).Delegation;
        var integrationVerified = HasVerifiedIntegrationEvidence(session, run, child, delegation.ChildTaskId,
            delegation.ChildLaneId, own);
        if (!facts.OfType<AgentExecutionCompleted>().Any(e => e.ExecutionId == child)
            || outcome == ResultDispositionOutcome.Accepted
                && (result.Outcome != AgentOutcome.Succeeded || result.RemainingIssues.Count != 0 || !integrationVerified))
            throw new InvalidOperationException("A failed, issue-bearing, or unverified result cannot be accepted.");
        var created = delegation;
        var disposition = new ResultDisposition(DispositionId.New(), child, produced.ResultRef, outcome,
            supervisor, new PiiRedactor().Redact(reason), []);
        var state = AgentScope(run, facts, child);
        var task = TaskGraphProjection.Replay(_codecs, own).Get(created.ChildTaskId)
            ?? throw new InvalidDataException("Delegated Task is missing.");
        if (StateMachines.IsTaskTerminal(task.State)) throw new InvalidOperationException("Delegated Task is already terminal.");
        using var execution = ExecutionScope.Begin(state);
        var batch = new List<DomainEventPayload> { new ResultDispositionRecorded(child, disposition) };
        if (outcome == ResultDispositionOutcome.Accepted)
        { batch.Add(new LaneCompleted(created.ChildLaneId, result)); batch.Add(new TaskCompleted(created.ChildTaskId, result)); }
        else
        { batch.Add(new LaneBlocked(created.ChildLaneId, outcome.ToString())); batch.Add(new TaskBlocked(created.ChildTaskId, outcome.ToString())); }
        stream.AppendBatch(batch, DurabilityClass.Barrier,
            Enumerable.Repeat<ExecutionScopeState?>(state, batch.Count).ToArray());
        ResolveReadyJoins(session, run);
    }

    private void ApplySupervisorMailboxMessage(JsonElement input, WireEnvelope command, SessionId session, RunId run,
        ExecutionId supervisor, CommandId commandId, long before, IReadOnlyList<DomainEvent> own,
        DomainEventPayload[] facts, PreM6RecordProjection records, EventStream stream)
    {
        if (_artifacts is not IArtifactPreparationStore preparation || _artifacts is not IArtifactPublicationLease publication)
            throw new InvalidOperationException("Mailbox artifact publication is unavailable.");
        var target = new ExecutionId(Guid.Parse(input.GetProperty("targetExecutionId").GetString()!));
        var sourceId = new EventId(Guid.Parse(input.GetProperty("sourceEventId").GetString()!));
        var text = input.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384) throw new ArgumentException("Mailbox content is empty or too large.");
        _ = RequireBoundChild(records, facts, supervisor, target);
        var source = own.SingleOrDefault(evt => evt.EventId == sourceId)
            ?? throw new ArgumentException("Mailbox source event is outside the bound Run.");
        if (source.ExecutionId != supervisor && source.ExecutionId != target)
            throw new ArgumentException("Mailbox source event does not belong to the bound supervision pair.");
        if (IsExecutionTerminal(facts, target)) throw new InvalidOperationException("A terminal child cannot receive a mailbox message.");
        var mailbox = records.Records.Values.Where(history => history.OwnerExecutionId == target)
            .SelectMany(history => history.Facts.OfType<ExecutionMailboxCreated>())
            .SingleOrDefault(e => e.Mailbox.OwnerExecutionId == target)?.Mailbox
            ?? throw new InvalidDataException("The accepted supervision binding has no mailbox.");
        var prepared = preparation.PrepareText(new PiiRedactor().Redact(text), "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        using var lease = publication.AcquirePublicationLease(CancellationToken.None);
        var artifact = prepared.Publish();
        var message = new ExecutionMailboxMessage(MailboxMessageId.New(), mailbox.MailboxId, supervisor,
            artifact, new EvidenceEventRef(session, source.EventId));
        var scope = AgentScope(run, facts, target);
        using var execution = ExecutionScope.Begin(scope);
        stream.Append(new ExecutionMailboxMessageReceived(target, message), DurabilityClass.Barrier);
        var messageEvent = _store.ReadFrom(session, 1).Single(evt => _codecs.Decode(evt) is ExecutionMailboxMessageReceived received
            && received.ExecutionId == target && received.Message.MessageId == message.MessageId);
        // WakePolicy: a message wakes only an actively owned, accepted child at the dedicated
        // mailbox-receive tool boundary. It cannot unblock Wait, join, budget, or human input.
        TryWakeActiveMailboxWait(session, run, target,
            new ExecutionMailboxMessageReceived(target, message), messageEvent);
    }

    private static SupervisionBinding RequireBoundChild(PreM6RecordProjection records,
        DomainEventPayload[] facts, ExecutionId supervisor, ExecutionId child, bool requireAccepted = true)
    {
        var start = facts.OfType<AgentExecutionStarted>().SingleOrDefault(e => e.ExecutionId == child)
            ?? throw new ArgumentException("Unknown supervised execution.");
        if (start.ParentExecutionId != supervisor || IsExecutionTerminal(facts, supervisor))
            throw new InvalidOperationException("Supervisor is not the live parent of this execution.");
        var candidates = records.Records.Values.Where(history => history.OwnerExecutionId == child)
            .SelectMany(history => history.Facts.OfType<SupervisionBindingCreated>())
            .Where(e => e.Binding.SubjectExecutionId == child && e.Binding.SupervisorExecutionId == supervisor).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("Accepted supervisor binding is unavailable.");
        var phase = records.Records["binding:" + candidates[0].Binding.BindingId].Phase;
        if (requireAccepted && phase != PreM6RecordPhase.Accepted
            || !requireAccepted && phase is not (PreM6RecordPhase.Created or PreM6RecordPhase.Accepted))
            throw new InvalidOperationException("Accepted supervisor binding is unavailable.");
        return candidates[0].Binding;
    }

    private bool HasVerifiedIntegrationEvidence(SessionId session, RunId run, ExecutionId execution,
        TaskId task, LaneId lane, IReadOnlyList<DomainEvent> journal)
    {
        var scoped = journal.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(item => item.Event.RunId == run && item.Event.ExecutionId == execution
                && item.Event.TaskId == task && item.Event.LaneId == lane).ToArray();
        var integration = scoped.Where(item => item.Payload is IntegrationStatusRecorded status
                && status.Status.Scope.RunId == run && status.Status.Scope.TaskId == task
                && status.Status.Scope.LaneId == lane)
            .OrderBy(item => item.Event.Sequence).LastOrDefault().Payload as IntegrationStatusRecorded;
        var validation = scoped.Where(item => item.Payload is ValidationStateRecorded state
                && state.State.Scope.RunId == run && state.State.Scope.TaskId == task
                && state.State.Scope.LaneId == lane && state.State.Level == ValidationLevel.Integration)
            .OrderBy(item => item.Event.Sequence).LastOrDefault().Payload as ValidationStateRecorded;
        return integration?.Status.Status == IntegrationStatus.Verified
            && validation?.State.Status == ValidationStatus.Passed
            && HasToolBackedReceipt(integration.Status.EvidenceRefs, session, run, execution, task, lane, journal)
            && HasToolBackedReceipt(validation.State.EvidenceRefs, session, run, execution, task, lane, journal);
    }

    private bool HasToolBackedReceipt(IReadOnlyList<EvidenceRef> evidence, SessionId session, RunId run,
        ExecutionId execution, TaskId task, LaneId lane, IReadOnlyList<DomainEvent> journal)
        => evidence.Where(item => item.Kind == EvidenceKind.ToolBacked && item.ReceiptRef is not null)
            .Any(item =>
            {
                var receipt = item.ReceiptRef!;
                var evt = journal.SingleOrDefault(candidate => candidate.SessionId == session
                    && candidate.EventId == receipt.EventId);
                return receipt.SessionId == session && evt is not null && evt.RunId == run
                    && evt.ExecutionId == execution && evt.TaskId == task && evt.LaneId == lane
                    && evt.ToolCallId == receipt.ToolCallId
                    && _codecs.Decode(evt) is ToolCallSucceeded succeeded && succeeded.ToolCallId == receipt.ToolCallId;
            });

    private void EnsureSupervisor(SessionId session, RunId run, ExecutionId supervisor)
    {
        if (_lastSessionId != session || _lastRunId != run)
            throw new InvalidOperationException("Scoped supervisor Run is no longer current.");
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        if (!facts.OfType<AgentExecutionStarted>().Any(e => e.ExecutionId == supervisor))
            throw new InvalidOperationException("Scoped supervisor execution is unknown.");
        if (IsExecutionTerminal(facts, supervisor))
        {
            ReconcileTerminatedSupervisors(session, run);
            throw new InvalidOperationException("Supervisor terminated; Wait policy keeps the join unresolved.");
        }
        var projection = RunProjection.Replay(session, run, _codecs, own);
        if (projection.State != RunState.Running || projection.ModeAuthority?.Mode != RunMode.Orchestrate
            || projection.ModeAuthority.Authorization is not { } authorization
            || !projection.ModeAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Supervision authorization is unavailable or expired.");
    }

    internal IReadOnlyList<WireEnvelope> SubscribeSupervisorSince(SessionId session, RunId run,
        ExecutionId supervisor, long fromSequence)
    {
        EnsureSupervisor(session, run, supervisor);
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        var allowed = facts.OfType<AgentExecutionStarted>().Where(e => e.ExecutionId == supervisor
            || e.ParentExecutionId == supervisor && HasAcceptedBinding(facts, e.ExecutionId))
            .Select(e => e.ExecutionId).ToHashSet();
        var visible = own.Where(evt => evt.Sequence >= Math.Max(1, fromSequence) && evt.ExecutionId is { } execution
            && allowed.Contains(execution)).ToArray();
        return new ProtocolMapper(_codecs, _artifacts).Map(visible);
    }

    internal SessionQueryResult? QuerySupervisor(SessionId session, RunId run, ExecutionId supervisor,
        string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureSupervisor(session, run, supervisor);
        if (name != "agents") return null;
        var snapshot = AgentLaneReader.Read(_store, _codecs, session, run, _artifacts);
        return new(name, AgentsJson.Encode(snapshot with { Lanes = snapshot.Lanes.Where(lane =>
            lane.ExecutionId == supervisor.ToString() || lane.ParentExecutionId == supervisor.ToString()).ToArray() }));
    }

    private static bool HasAcceptedBinding(IEnumerable<DomainEventPayload> facts, ExecutionId child)
        => facts.OfType<SupervisionBindingCreated>().Any(created => created.Binding.SubjectExecutionId == child
            && facts.OfType<SupervisionBindingAccepted>().Any(accepted => accepted.ExecutionId == child
                && accepted.BindingId == created.Binding.BindingId));
}

internal static class SupervisorClientFactory
{
    internal static IOmniClient Create(OmniServer server, ExecutionId supervisor) => server.CreateScopedSupervisorClient(supervisor);
}
