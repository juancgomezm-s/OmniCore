namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Replays FanOut identity, owner scope, membership, and exactly-once fan-in facts.</summary>
public static class FanOutGroupProjection
{
    public static void Replay(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> journal)
    {
        if (!journal.Any(item => item.Type.Equals(EventType.Of("fanout_group.created"))
            || item.Type.Equals(EventType.Of("fanout_group.resolved"))
            || item.Type.Equals(EventType.Of("fanout_group.member_replaced")))) return;

        var positionByEvent = journal.Select((item, index) => (item.EventId, index))
            .ToDictionary(item => item.EventId, item => item.index);

        var tasks = new Dictionary<TaskId, TaskCreated>();
        var lanes = new Dictionary<LaneId, LaneCreated>();
        var executions = new Dictionary<ExecutionId, (DomainEvent Envelope, AgentExecutionStarted Fact)>();
        var delegations = new Dictionary<DelegationId, (DomainEvent Envelope, DelegationCreated Fact)>();
        var accepted = new Dictionary<DelegationId, (DomainEvent Envelope, DelegationAccepted Fact)>();
        var returned = new HashSet<DelegationId>();
        var latestResults = new Dictionary<ExecutionId, (DomainEvent Envelope, AgentResultProduced Fact)>();
        var dispositions = new List<(DomainEvent Envelope, ResultDispositionRecorded Fact)>();
        var groups = new Dictionary<FanOutGroupId, GroupState>();
        var resolved = new HashSet<FanOutGroupId>();
        var memberOwners = new Dictionary<DelegationId, FanOutGroupId>();

        foreach (var envelope in journal)
        {
            var payload = codecs.Decode(envelope);
            switch (payload)
            {
                case TaskCreated task: tasks[task.TaskId] = task; break;
                case LaneCreated lane: lanes[lane.LaneId] = lane; break;
                case AgentExecutionStarted execution: executions[execution.ExecutionId] = (envelope, execution); break;
                case DelegationCreated created:
                    if (!delegations.TryAdd(created.Delegation.DelegationId, (envelope, created)))
                        throw Invalid("Delegation identity is duplicated while replaying FanOut.");
                    break;
                case DelegationAccepted acceptedFact:
                    accepted[acceptedFact.DelegationId] = (envelope, acceptedFact);
                    break;
                case DelegationReturned returnedFact:
                    returned.Add(returnedFact.DelegationId);
                    break;
                case AgentResultProduced result:
                    if (!latestResults.TryGetValue(result.ExecutionId, out var prior)
                        || result.ResultRevision > prior.Fact.ResultRevision)
                        latestResults[result.ExecutionId] = (envelope, result);
                    break;
                case ResultDispositionRecorded disposition:
                    dispositions.Add((envelope, disposition));
                    break;
                case FanOutGroupCreated createdGroup:
                    createdGroup.Validate();
                    if (envelope.SchemaVersion != 1 || envelope.SessionId.Value == Guid.Empty
                        || groups.ContainsKey(createdGroup.Group.GroupId))
                        throw Invalid("FanOut creation has an invalid schema or duplicate group identity.");
                    ValidateOwnerEnvelope(envelope, createdGroup.ExecutionId, tasks, lanes, executions);
                    var state = new GroupState(envelope, createdGroup);
                    foreach (var memberId in createdGroup.Group.MemberDelegationIds)
                    {
                        if (!delegations.TryGetValue(memberId, out var member)
                            || member.Fact.Delegation.ParentExecutionId != createdGroup.ExecutionId
                            || positionByEvent[member.Envelope.EventId] >= positionByEvent[envelope.EventId]
                            || !memberOwners.TryAdd(memberId, createdGroup.Group.GroupId))
                            throw Invalid("FanOut member must be a prior, unique direct child of its owner.");
                        state.Members.Add(memberId);
                    }
                    groups.Add(createdGroup.Group.GroupId, state);
                    break;
                case FanOutGroupMemberReplaced replacement:
                    replacement.Validate();
                    if (envelope.SchemaVersion != 1 || !groups.TryGetValue(replacement.GroupId, out var replaceState)
                        || replaceState.Resolved || replacement.ExecutionId != replaceState.Created.Group.OwnerExecutionId)
                        throw Invalid("FanOut member replacement must target an unresolved group owned by its executor.");
                    ValidateOwnerEnvelope(envelope, replacement.ExecutionId, tasks, lanes, executions);
                    var slot = replaceState.Members.IndexOf(replacement.PreviousDelegationId);
                    if (slot < 0)
                        throw Invalid("FanOut replacement must name a current member and a queued replacement.");
                    if (!delegations.TryGetValue(replacement.ReplacementDelegationId, out var replacementDelegation)
                        || replacementDelegation.Fact.Delegation.ParentExecutionId != replacement.ExecutionId
                        || positionByEvent[replacementDelegation.Envelope.EventId] >= positionByEvent[envelope.EventId]
                        || !memberOwners.TryAdd(replacement.ReplacementDelegationId, replacement.GroupId))
                        throw Invalid("FanOut replacement must be a prior, unique direct child of its owner.");
                    if (!IsRework(replacement.PreviousDelegationId, accepted, latestResults, dispositions))
                        throw Invalid("Only a member with an explicit ReworkRequested disposition can be replaced.");
                    replaceState.Members[slot] = replacement.ReplacementDelegationId;
                    break;
                case FanOutGroupResolved resolvedGroup:
                    resolvedGroup.Validate();
                    if (envelope.SchemaVersion != 1 || resolved.Contains(resolvedGroup.GroupId)
                        || !groups.TryGetValue(resolvedGroup.GroupId, out var createdGroupState)
                        || createdGroupState.Resolved)
                        throw Invalid("FanOut resolution must follow exactly one creation and occur once.");
                    var group = createdGroupState.Created.Group;
                    ValidateOwnerEnvelope(envelope, resolvedGroup.ExecutionId, tasks, lanes, executions);
                    if (resolvedGroup.ExecutionId != group.OwnerExecutionId
                        || positionByEvent[envelope.EventId] <= positionByEvent[createdGroupState.Envelope.EventId]
                        || (group.FanInPolicy == FanInPolicy.Aggregate) != (resolvedGroup.AggregateRef is not null))
                        throw Invalid("FanOut resolution owner, order, or policy does not match creation.");

                    var expectedResults = new List<ArtifactRef>(createdGroupState.Members.Count);
                    var acceptedDispositions = new List<(DomainEvent Envelope, ResultDispositionRecorded Fact)>();
                    foreach (var memberId in createdGroupState.Members)
                    {
                        if (!returned.Contains(memberId) || !accepted.TryGetValue(memberId, out var childAcceptance)
                            || !latestResults.TryGetValue(childAcceptance.Fact.ChildExecutionId, out var latestResult))
                            throw Invalid("FanOut cannot resolve before every accepted child returns a result.");
                        var latestDisposition = dispositions.LastOrDefault(item =>
                            item.Fact.Disposition.ExecutionId == childAcceptance.Fact.ChildExecutionId
                            && item.Fact.Disposition.ResultRef == latestResult.Fact.ResultRef);
                        if (latestDisposition.Envelope is null
                            || latestDisposition.Fact.Disposition.Outcome != ResultDispositionOutcome.Accepted)
                            throw Invalid("FanOut cannot resolve a result without its explicit Accepted disposition.");
                        expectedResults.Add(latestResult.Fact.ResultRef);
                        acceptedDispositions.Add(latestDisposition);
                    }
                    if (!resolvedGroup.MemberResultRefs.SequenceEqual(expectedResults))
                        throw Invalid("FanOut resolution must preserve member order and exact result references.");
                    var finalAcceptance = acceptedDispositions.OrderBy(item => positionByEvent[item.Envelope.EventId]).Last().Envelope;
                    if (envelope.Causation is not EventCausation cause || cause.EventId != finalAcceptance.EventId)
                        throw Invalid("FanOut resolution causation must name the final member acceptance event.");
                    resolved.Add(resolvedGroup.GroupId);
                    createdGroupState.Resolved = true;
                    break;
            }
        }
    }

    private static void ValidateOwnerEnvelope(DomainEvent envelope, ExecutionId owner,
        IReadOnlyDictionary<TaskId, TaskCreated> tasks, IReadOnlyDictionary<LaneId, LaneCreated> lanes,
        IReadOnlyDictionary<ExecutionId, (DomainEvent Envelope, AgentExecutionStarted Fact)> executions)
    {
        if (!executions.TryGetValue(owner, out var started)
            || !lanes.TryGetValue(started.Fact.LaneId, out var lane)
            || !tasks.TryGetValue(lane.TaskId, out var task)
            || envelope.SessionId != started.Envelope.SessionId
            || envelope.ExecutionId != owner || envelope.RunId != task.RunId
            || envelope.CorrelationId != task.RunId || envelope.TaskId != task.TaskId
            || envelope.LaneId != lane.LaneId)
            throw Invalid("FanOut event envelope does not match its owner Execution/Task/Lane/Run.");
    }

    private static bool IsRework(DelegationId delegationId,
        IReadOnlyDictionary<DelegationId, (DomainEvent Envelope, DelegationAccepted Fact)> accepted,
        IReadOnlyDictionary<ExecutionId, (DomainEvent Envelope, AgentResultProduced Fact)> latestResults,
        IReadOnlyList<(DomainEvent Envelope, ResultDispositionRecorded Fact)> dispositions)
    {
        if (!accepted.TryGetValue(delegationId, out var child)
            || !latestResults.TryGetValue(child.Fact.ChildExecutionId, out var result)) return false;
        var latest = dispositions.LastOrDefault(item => item.Fact.Disposition.ExecutionId == child.Fact.ChildExecutionId
            && item.Fact.Disposition.ResultRef == result.Fact.ResultRef);
        return latest.Envelope is not null
            && latest.Fact.Disposition.Outcome == ResultDispositionOutcome.ReworkRequested;
    }

    private sealed class GroupState(DomainEvent envelope, FanOutGroupCreated created)
    {
        public DomainEvent Envelope { get; } = envelope;
        public FanOutGroupCreated Created { get; } = created;
        public List<DelegationId> Members { get; } = [];
        public bool Resolved { get; set; }
    }

    private static InvalidStateTransitionException Invalid(string reason) =>
        new("fan-out group", reason, "replay");
}
