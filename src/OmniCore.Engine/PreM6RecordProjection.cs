namespace OmniCore.Engine;

using System.Collections.ObjectModel;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>A recorded phase, not an execution decision or readiness calculation.</summary>
public enum PreM6RecordPhase { Created, Accepted, Returned, Resolved, Failed, Recorded }
public sealed record PreM6RecordHistory(ExecutionId OwnerExecutionId, PreM6RecordPhase Phase,
    IReadOnlyList<IPreM6ContractEvent> Facts);

/// <summary>
/// Reconstructs only durable facts and validates identity links. It never creates tasks,
/// schedules workers, satisfies a join, wakes an agent or accepts a Managed result.
/// New contract identities are local to the envelope Session; event references name their Session.
/// </summary>
public sealed class PreM6RecordProjection
{
    private readonly Dictionary<string, PreM6RecordHistory> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<TaskId, TaskCreated> _tasks = new();
    private readonly Dictionary<LaneId, LaneCreated> _lanes = new();
    private readonly Dictionary<ExecutionId, AgentExecutionStarted> _executions = new();
    private readonly Dictionary<EventId, DomainEvent> _events = new();
    private readonly Dictionary<ExecutionId, AgentResultProduced> _latestResults = new();
    private readonly IEventCodecRegistry _codecs;
    private readonly SessionId _session;
    private readonly Func<SessionId, IReadOnlyList<DomainEvent>>? _externalEvents;

    private PreM6RecordProjection(SessionId session, IEventCodecRegistry codecs,
        Func<SessionId, IReadOnlyList<DomainEvent>>? externalEvents)
    { _session = session; _codecs = codecs; _externalEvents = externalEvents; }

    public IReadOnlyDictionary<string, PreM6RecordHistory> Records =>
        new ReadOnlyDictionary<string, PreM6RecordHistory>(new Dictionary<string, PreM6RecordHistory>(_records));

    public static PreM6RecordProjection Replay(SessionId session, IEventCodecRegistry codecs,
        IEnumerable<DomainEvent> events, Func<SessionId, IReadOnlyList<DomainEvent>>? externalEvents = null)
    {
        var projection = new PreM6RecordProjection(session, codecs, externalEvents);
        foreach (var evt in events)
        {
            if (evt.SessionId != session) throw Invalid("Journal contains a foreign session.");
            projection.Apply(evt, codecs.Decode(evt));
            if (!projection._events.TryAdd(evt.EventId, evt))
                throw Invalid("Duplicate event identity in journal.");
        }
        return projection;
    }

    private void Apply(DomainEvent evt, DomainEventPayload payload)
    {
        // Legacy execution facts remain readable. Their links are checked when a new record uses them.
        switch (payload)
        {
            case TaskCreated task: _tasks[task.TaskId] = task; return;
            case LaneCreated lane: _lanes[lane.LaneId] = lane; return;
            case AgentExecutionStarted execution:
                if (_executions.TryGetValue(execution.ExecutionId, out var started) && started != execution)
                    throw Invalid("Execution identity cannot be rebound to different start metadata.");
                _executions[execution.ExecutionId] = execution;
                return;
        }
        if (payload is not IPreM6ContractEvent fact) return;
        fact.Validate();
        var owner = Owner(fact);
        var executor = RequireExecution(owner);
        var laneOwner = _lanes[executor.LaneId];
        var taskOwner = _tasks[laneOwner.TaskId];
        if (evt.ExecutionId != owner || evt.RunId != taskOwner.RunId || evt.CorrelationId != taskOwner.RunId
            || evt.TaskId != laneOwner.TaskId || evt.LaneId != executor.LaneId)
            throw Invalid("Record envelope does not match its execution owner.");

        switch (fact)
        {
            case DelegationCreated e:
                if (!_tasks.TryGetValue(e.Delegation.ChildTaskId, out var task)
                    || task.RunId != taskOwner.RunId || task.ParentTaskId != laneOwner.TaskId
                    || !_lanes.TryGetValue(e.Delegation.ChildLaneId, out var lane)
                    || lane.TaskId != task.TaskId || lane.AgentProfile != e.Delegation.ProfileId)
                    throw Invalid("Delegation child Task/Lane/Profile link mismatch.");
                Record(Key("delegation", e.Delegation.DelegationId), owner, fact, PreM6RecordPhase.Created); break;
            case DelegationAccepted e:
                var delegation = Initial<DelegationCreated>(Key("delegation", e.DelegationId), owner).Delegation;
                var child = RequireExecution(e.ChildExecutionId);
                if (child.LaneId != delegation.ChildLaneId || child.ProfileId != delegation.ProfileId
                    || child.ParentExecutionId != owner || child.Relation != delegation.Relation
                    || child.Supervision != delegation.Supervision)
                    throw Invalid("Accepted child execution does not match delegation.");
                Record(Key("delegation", e.DelegationId), owner, fact, PreM6RecordPhase.Accepted, PreM6RecordPhase.Created); break;
            case DelegationReturned e:
                var accepted = History(Key("delegation", e.DelegationId), owner).Facts.OfType<DelegationAccepted>().SingleOrDefault()
                    ?? throw Invalid("Returned delegation lacks an acceptance fact.");
                if (!_latestResults.TryGetValue(accepted.ChildExecutionId, out var result) || result.ResultRef != e.ResultRef)
                    throw Invalid("Returned result was not produced by the accepted child.");
                Record(Key("delegation", e.DelegationId), owner, fact, PreM6RecordPhase.Returned, PreM6RecordPhase.Accepted); break;
            case DelegationFailed e:
                Record(Key("delegation", e.DelegationId), owner, fact, PreM6RecordPhase.Failed, PreM6RecordPhase.Created, PreM6RecordPhase.Accepted); break;
            case ExecutionJoinCreated e:
                foreach (var member in e.Join.MemberExecutionIds)
                    if (_tasks[_lanes[RequireExecution(member).LaneId].TaskId].RunId != taskOwner.RunId)
                        throw Invalid("Join member belongs to another Run.");
                Record(Key("join", e.Join.JoinId), owner, fact, PreM6RecordPhase.Created); break;
            case ExecutionJoinResolved e:
                var join = Initial<ExecutionJoinCreated>(Key("join", e.JoinId), owner).Join;
                if (e.SatisfyingExecutionIds.Any(id => !join.MemberExecutionIds.Contains(id)))
                    throw Invalid("Recorded join resolution names a foreign member.");
                // No All/Any/Quorum/Explicit evaluator: preserve the explicit resolution fact.
                Record(Key("join", e.JoinId), owner, fact, PreM6RecordPhase.Resolved, PreM6RecordPhase.Created); break;
            case ExecutionJoinFailed e:
                Record(Key("join", e.JoinId), owner, fact, PreM6RecordPhase.Failed, PreM6RecordPhase.Created); break;
            case SupervisionBindingCreated e:
                RequireExecution(e.Binding.SupervisorExecutionId);
                Record(Key("binding", e.Binding.BindingId), owner, fact, PreM6RecordPhase.Created); break;
            case SupervisionBindingAccepted e:
                Record(Key("binding", e.BindingId), owner, fact, PreM6RecordPhase.Accepted, PreM6RecordPhase.Created); break;
            case SupervisionBindingFailed e:
                Record(Key("binding", e.BindingId), owner, fact, PreM6RecordPhase.Failed, PreM6RecordPhase.Created, PreM6RecordPhase.Accepted); break;
            case ExecutionMailboxCreated e:
                Record(Key("mailbox", e.Mailbox.MailboxId), owner, fact, PreM6RecordPhase.Created); break;
            case ExecutionMailboxMessageReceived e:
                History(Key("mailbox", e.Message.MailboxId), owner);
                if (e.Message.SenderExecutionId is {} sender) RequireExecution(sender);
                if (e.Message.CorrelationEvent is {} correlation) ResolveEvent(correlation);
                Record(Key("message", e.Message.MessageId), owner, fact, PreM6RecordPhase.Created); break;
            case ExecutionMailboxMessageAcknowledged e:
                var message = Initial<ExecutionMailboxMessageReceived>(Key("message", e.MessageId), owner).Message;
                if (message.MailboxId != e.MailboxId) throw Invalid("Message ACK mailbox mismatch.");
                Record(Key("message", e.MessageId), owner, fact, PreM6RecordPhase.Accepted, PreM6RecordPhase.Created); break;
            case WakeRequestCreated e:
                ResolveEvent(e.Request.SourceEvent);
                if (e.Request.MessageId is {} messageId) History(Key("message", messageId), owner);
                Record(Key("wake", e.Request.WakeRequestId), owner, fact, PreM6RecordPhase.Created); break;
            case WakeRequestAccepted e:
                Record(Key("wake", e.WakeRequestId), owner, fact, PreM6RecordPhase.Accepted, PreM6RecordPhase.Created); break;
            case WakeRequestResolved e:
                Record(Key("wake", e.WakeRequestId), owner, fact, PreM6RecordPhase.Resolved, PreM6RecordPhase.Accepted); break;
            case WakeRequestFailed e:
                Record(Key("wake", e.WakeRequestId), owner, fact, PreM6RecordPhase.Failed, PreM6RecordPhase.Created, PreM6RecordPhase.Accepted); break;
            case AgentResultProduced e:
                var resultKey = Key("result", e.ResultRef.Id);
                if (_records.ContainsKey(resultKey)) { Record(resultKey, owner, fact, PreM6RecordPhase.Recorded); break; }
                if (_latestResults.TryGetValue(owner, out var previous))
                {
                    if (e.ResultRevision <= previous.ResultRevision || e.SupersedesResultRef != previous.ResultRef)
                        throw Invalid("New result must supersede the prior immutable revision.");
                }
                else if (e.SupersedesResultRef is not null) throw Invalid("First result cannot supersede an unknown result.");
                Record(resultKey, owner, fact, PreM6RecordPhase.Recorded);
                _latestResults[owner] = e; break;
            case ResultDispositionRecorded e:
                if (Initial<AgentResultProduced>(Key("result", e.Disposition.ResultRef.Id), owner).ResultRef != e.Disposition.ResultRef)
                    throw Invalid("Disposition does not reference the immutable produced content.");
                if (e.Disposition.EvaluatorExecutionId is {} evaluator) RequireExecution(evaluator);
                Evidence(e.Disposition.EvidenceRefs);
                Record(Key("disposition", e.Disposition.DispositionId), owner, fact, PreM6RecordPhase.Recorded); break;
            case ValidationStateRecorded e:
                Scope(e.State.Scope); Evidence(e.State.EvidenceRefs);
                if (e.State.Status == ValidationStatus.Passed) RequireToolEvidence(e.State.EvidenceRefs);
                Record(Key("validation", evt.EventId), owner, fact, PreM6RecordPhase.Recorded); break;
            case ValidationDebtCreated e:
                Scope(e.Debt.Scope); Evidence(e.Debt.EvidenceRefs);
                Record(Key("debt", e.Debt.DebtId), owner, fact, PreM6RecordPhase.Created); break;
            case ValidationDebtResolved e:
                Evidence(e.EvidenceRefs); RequireToolEvidence(e.EvidenceRefs);
                Record(Key("debt", e.DebtId), owner, fact, PreM6RecordPhase.Resolved, PreM6RecordPhase.Created); break;
            case IntegrationStatusRecorded e:
                Scope(e.Status.Scope); Evidence(e.Status.EvidenceRefs);
                if (e.Status.Status == IntegrationStatus.Verified) RequireToolEvidence(e.Status.EvidenceRefs);
                Record(Key("integration", evt.EventId), owner, fact, PreM6RecordPhase.Recorded); break;
        }
    }

    private AgentExecutionStarted RequireExecution(ExecutionId id)
    {
        if (!_executions.TryGetValue(id, out var execution) || !_lanes.TryGetValue(execution.LaneId, out var lane)
            || lane.AgentProfile != execution.ProfileId || !_tasks.ContainsKey(lane.TaskId))
            throw Invalid("Execution has no matching durable Task/Lane/Profile.");
        return execution;
    }

    private void Scope(ValidationScope scope)
    {
        if (scope.SessionId != _session) throw Invalid("Validation scope belongs to another session.");
        if (scope.TaskId is {} task && (!_tasks.TryGetValue(task, out var declared) || declared.RunId != scope.RunId))
            throw Invalid("Validation Task/Run mismatch.");
        if (scope.LaneId is {} lane && (!_lanes.TryGetValue(lane, out var declaredLane) || declaredLane.TaskId != scope.TaskId))
            throw Invalid("Validation Lane/Task mismatch.");
    }

    private DomainEvent ResolveEvent(EvidenceEventRef reference)
    {
        var evt = reference.SessionId == _session ? _events.GetValueOrDefault(reference.EventId)
            : _externalEvents?.Invoke(reference.SessionId).SingleOrDefault(item => item.EventId == reference.EventId);
        if (evt is null || evt.SessionId != reference.SessionId) throw Invalid("Referenced event is unavailable in its session.");
        return evt;
    }

    private void Evidence(IReadOnlyList<EvidenceRef> evidence)
    {
        foreach (var item in evidence.Where(item => item.Kind == EvidenceKind.ToolBacked))
        {
            var receipt = item.ReceiptRef!;
            var evt = ResolveEvent(new(receipt.SessionId, receipt.EventId));
            if (_codecs.Decode(evt) is not ToolCallSucceeded succeeded || succeeded.ToolCallId != receipt.ToolCallId)
                throw Invalid("Evidence receipt is not the referenced canonical ToolCallSucceeded.");
        }
    }

    private static void RequireToolEvidence(IReadOnlyList<EvidenceRef> evidence)
    { if (!evidence.Any(item => item.Kind == EvidenceKind.ToolBacked)) throw Invalid("Verification requires tool-backed evidence."); }

    private PreM6RecordHistory History(string key, ExecutionId owner)
    {
        if (!_records.TryGetValue(key, out var history) || history.OwnerExecutionId != owner)
            throw Invalid("Record is absent or belongs to another execution.");
        return history;
    }
    private T Initial<T>(string key, ExecutionId owner) where T : class, IPreM6ContractEvent =>
        History(key, owner).Facts.OfType<T>().FirstOrDefault() ?? throw Invalid("Record has an incompatible creation type.");

    private void Record(string key, ExecutionId owner, IPreM6ContractEvent fact, PreM6RecordPhase phase,
        params PreM6RecordPhase[] allowedPrevious)
    {
        if (_records.TryGetValue(key, out var prior))
        {
            if (prior.OwnerExecutionId != owner) throw Invalid("Record identity reused by another execution.");
            var codec = _codecs.CodecFor(fact.Type());
            var encoded = codec.Encode(fact);
            if (prior.Facts.Any(item => item.Type().ToString() == fact.Type().ToString() && codec.Encode(item) == encoded)) return;
            if (!allowedPrevious.Contains(prior.Phase)) throw Invalid("Conflicting identity or recorded phase.");
            _records[key] = new(owner, phase, Array.AsReadOnly(prior.Facts.Append(fact).ToArray()));
        }
        else
        {
            if (allowedPrevious.Length != 0) throw Invalid("Record transition lacks a creation fact.");
            _records.Add(key, new(owner, phase, Array.AsReadOnly(new[] { fact })));
        }
    }

    private static string Key(string family, object id) => family + ":" + id;
    private static InvalidStateTransitionException Invalid(string reason) => new("pre-M6 record", reason, "replay");
    private static ExecutionId Owner(IPreM6ContractEvent fact) => fact switch
    {
        DelegationCreated e => e.ExecutionId, DelegationAccepted e => e.ExecutionId,
        DelegationReturned e => e.ExecutionId, DelegationFailed e => e.ExecutionId,
        ExecutionJoinCreated e => e.ExecutionId, ExecutionJoinResolved e => e.ExecutionId,
        ExecutionJoinFailed e => e.ExecutionId, SupervisionBindingCreated e => e.ExecutionId,
        SupervisionBindingAccepted e => e.ExecutionId, SupervisionBindingFailed e => e.ExecutionId,
        ExecutionMailboxCreated e => e.ExecutionId, ExecutionMailboxMessageReceived e => e.ExecutionId,
        ExecutionMailboxMessageAcknowledged e => e.ExecutionId, WakeRequestCreated e => e.ExecutionId,
        WakeRequestAccepted e => e.ExecutionId, WakeRequestResolved e => e.ExecutionId,
        WakeRequestFailed e => e.ExecutionId, AgentResultProduced e => e.ExecutionId,
        ResultDispositionRecorded e => e.ExecutionId, ValidationStateRecorded e => e.ExecutionId,
        ValidationDebtCreated e => e.ExecutionId, ValidationDebtResolved e => e.ExecutionId,
        IntegrationStatusRecorded e => e.ExecutionId, _ => throw Invalid("Unknown record contract.")
    };
}
