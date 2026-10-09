using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private CommandAck CreateFanOutGroup(WireEnvelope command, bool trustedUserAction,
        CancellationToken cancellationToken)
    {
        if (!trustedUserAction)
            return new(command.MessageId, "error", "Fan-out requires a trusted user action", RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new(command.MessageId, "error", "No active Run", RuntimeCommandOutcome.Rejected());
        long? before = null;
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        try
        {
            before = _store.CurrentSequence(session);
            using var document = JsonDocument.Parse(command.PayloadJson);
            var input = document.RootElement;
            var owner = new ExecutionId(Guid.Parse(input.GetProperty("ownerExecutionId").GetString()!));
            var members = input.GetProperty("delegationIds").EnumerateArray()
                .Select(item => new DelegationId(Guid.Parse(item.GetString()!))).ToArray();
            var policy = Enum.Parse<FanInPolicy>(input.GetProperty("policy").GetString()!, true);
            if (members.Length is < 2 or > 32 || members.Distinct().Count() != members.Length
                || !Enum.IsDefined(policy)) throw new ArgumentException("Fan-out requires 2 to 32 unique delegations and a known policy.");
            using var causation = CausationScope.Begin(new CommandCausation(commandId));
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_lastSessionId != session || _lastRunId != run)
                    throw new InvalidOperationException("Fan-out Run is no longer current.");
                var journal = _store.ReadFrom(session, 1);
                if (journal.Any(evt => evt.Causation is CommandCausation cause && cause.CommandId == commandId))
                    return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
                var own = journal.Where(evt => evt.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                var projection = RunProjection.Replay(session, run, _codecs, own);
                if (projection.State != RunState.Running || projection.ModeAuthority?.Mode != RunMode.Orchestrate
                    || projection.ModeAuthority.Authorization is not { } authorization
                    || !projection.ModeAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("Fan-out requires active ORQ authorization.");
                var parent = facts.OfType<AgentExecutionStarted>().SingleOrDefault(item => item.ExecutionId == owner)
                    ?? throw new ArgumentException("Unknown fan-out owner.");
                if (parent.ParentExecutionId is not null || parent.LaneId is not { } ownerLane
                    || IsExecutionTerminal(facts, owner) || HasOpenModelStep(own, ownerLane) || HasOpenToolCall(own, ownerLane))
                    throw new InvalidOperationException("Fan-out owner must be the active principal at a settled boundary.");
                if (authorization.Limits.MaxAgents < 2)
                    throw new InvalidOperationException("Coordination capacity does not admit child lanes.");
                var records = PreM6RecordProjection.Replay(session, _codecs, journal);
                var delegations = facts.OfType<DelegationCreated>().Select(item => item.Delegation)
                    .ToDictionary(item => item.DelegationId);
                foreach (var id in members)
                {
                    if (!delegations.TryGetValue(id, out var delegation) || delegation.ParentExecutionId != owner
                        || records.Records["delegation:" + id].Phase != PreM6RecordPhase.Created)
                        throw new ArgumentException("Fan-out members must be distinct, queued direct children of the owner.");
                }
                var existingMembers = facts.OfType<FanOutGroupCreated>().SelectMany(item => item.Group.MemberDelegationIds)
                    .Concat(facts.OfType<FanOutGroupMemberReplaced>().Select(item => item.ReplacementDelegationId)).ToHashSet();
                if (members.Any(existingMembers.Contains))
                    throw new ArgumentException("A delegation cannot belong to more than one fan-out group.");
                var group = new FanOutGroup(FanOutGroupId.New(), owner, members, policy);
                group.Validate();
                using var execution = ExecutionScope.Begin(AgentScope(run, facts, owner));
                new EventStream(_store, _codecs, session).Append(new FanOutGroupCreated(owner, group), DurabilityClass.Barrier);
            }
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or FormatException or IOException or JsonException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value, failure.Message, restoreRunIdentity: false);
        }
    }

    private CommandAck ReplaceFanOutMember(WireEnvelope command, bool trustedUserAction,
        CancellationToken cancellationToken)
    {
        if (!trustedUserAction)
            return new(command.MessageId, "error", "Fan-out rework requires a trusted user action", RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new(command.MessageId, "error", "No active Run", RuntimeCommandOutcome.Rejected());
        long? before = null;
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        try
        {
            before = _store.CurrentSequence(session);
            using var document = JsonDocument.Parse(command.PayloadJson);
            var input = document.RootElement;
            var groupId = new FanOutGroupId(Guid.Parse(input.GetProperty("groupId").GetString()!));
            var previousId = new DelegationId(Guid.Parse(input.GetProperty("previousDelegationId").GetString()!));
            var replacementId = new DelegationId(Guid.Parse(input.GetProperty("replacementDelegationId").GetString()!));
            using var causation = CausationScope.Begin(new CommandCausation(commandId));
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_lastSessionId != session || _lastRunId != run)
                    throw new InvalidOperationException("Fan-out Run is no longer current.");
                var journal = _store.ReadFrom(session, 1);
                if (journal.Any(evt => evt.Causation is CommandCausation cause && cause.CommandId == commandId))
                    return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
                var own = journal.Where(evt => evt.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                var projection = RunProjection.Replay(session, run, _codecs, own);
                if (projection.State != RunState.Running || projection.ModeAuthority?.Mode != RunMode.Orchestrate
                    || projection.ModeAuthority.Authorization is null
                    || !projection.ModeAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("Fan-out rework requires active ORQ authorization.");
                var group = facts.OfType<FanOutGroupCreated>().SingleOrDefault(item => item.Group.GroupId == groupId)?.Group
                    ?? throw new ArgumentException("Unknown fan-out group.");
                var parent = facts.OfType<AgentExecutionStarted>().SingleOrDefault(item => item.ExecutionId == group.OwnerExecutionId)
                    ?? throw new ArgumentException("Unknown fan-out owner.");
                if (parent.ParentExecutionId is not null || parent.LaneId is not { } ownerLane
                    || IsExecutionTerminal(facts, group.OwnerExecutionId) || HasOpenModelStep(own, ownerLane) || HasOpenToolCall(own, ownerLane))
                    throw new InvalidOperationException("Fan-out owner must be active at a settled boundary.");
                var members = group.MemberDelegationIds.ToList();
                foreach (var prior in facts.OfType<FanOutGroupMemberReplaced>().Where(item => item.GroupId == groupId))
                {
                    var slot = members.IndexOf(prior.PreviousDelegationId);
                    if (slot >= 0) members[slot] = prior.ReplacementDelegationId;
                }
                var slotToReplace = members.IndexOf(previousId);
                if (slotToReplace < 0) throw new ArgumentException("Delegation is not a current member of this group.");
                var allGroupMembers = facts.OfType<FanOutGroupCreated>().SelectMany(item => item.Group.MemberDelegationIds)
                    .Concat(facts.OfType<FanOutGroupMemberReplaced>().Select(item => item.ReplacementDelegationId)).ToHashSet();
                if (allGroupMembers.Contains(replacementId))
                    throw new ArgumentException("Replacement delegation already belongs to a fan-out group.");
                var delegationFacts = facts.OfType<DelegationCreated>().Select(item => item.Delegation)
                    .ToDictionary(item => item.DelegationId);
                var records = PreM6RecordProjection.Replay(session, _codecs, journal);
                if (!delegationFacts.TryGetValue(replacementId, out var replacement)
                    || replacement.ParentExecutionId != group.OwnerExecutionId
                    || records.Records["delegation:" + replacementId].Phase != PreM6RecordPhase.Created)
                    throw new ArgumentException("Replacement must be a distinct queued direct child of the group owner.");
                var priorHistory = records.Records["delegation:" + previousId];
                var accepted = priorHistory.Facts.OfType<DelegationAccepted>().SingleOrDefault();
                var latestResult = accepted is null ? null : facts.OfType<AgentResultProduced>()
                    .Where(item => item.ExecutionId == accepted.ChildExecutionId).OrderBy(item => item.ResultRevision).LastOrDefault();
                var priorDisposition = latestResult is null ? null : facts.OfType<ResultDispositionRecorded>()
                    .LastOrDefault(item => item.Disposition.ExecutionId == accepted?.ChildExecutionId
                        && item.Disposition.ResultRef == latestResult.ResultRef);
                if (priorHistory.Phase != PreM6RecordPhase.Returned
                    || priorDisposition?.Disposition.Outcome != ResultDispositionOutcome.ReworkRequested)
                    throw new InvalidOperationException("Only an explicitly ReworkRequested member may be replaced.");
                using var execution = ExecutionScope.Begin(AgentScope(run, facts, group.OwnerExecutionId));
                new EventStream(_store, _codecs, session).Append(new FanOutGroupMemberReplaced(group.OwnerExecutionId,
                    groupId, previousId, replacementId), DurabilityClass.Barrier);
            }
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or FormatException or IOException or JsonException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value, failure.Message, restoreRunIdentity: false);
        }
    }

    private void ResolveReadyFanOutGroups(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        var resolved = facts.OfType<FanOutGroupResolved>().Select(item => item.GroupId).ToHashSet();
        var delegations = facts.OfType<DelegationCreated>().Select(item => item.Delegation)
            .ToDictionary(item => item.DelegationId);
        var histories = PreM6RecordProjection.Replay(session, _codecs, journal);
        foreach (var created in facts.OfType<FanOutGroupCreated>())
        {
            var effectiveMembers = created.Group.MemberDelegationIds.ToList();
            foreach (var replacement in facts.OfType<FanOutGroupMemberReplaced>()
                .Where(item => item.GroupId == created.Group.GroupId))
            {
                var slot = effectiveMembers.IndexOf(replacement.PreviousDelegationId);
                if (slot >= 0) effectiveMembers[slot] = replacement.ReplacementDelegationId;
            }
            var group = created.Group with { MemberDelegationIds = effectiveMembers };
            if (resolved.Contains(group.GroupId)) continue;
            var memberStates = new List<FanOutMemberEvaluation>(group.MemberDelegationIds.Count);
            foreach (var delegationId in group.MemberDelegationIds)
            {
                if (!delegations.ContainsKey(delegationId)
                    || !histories.Records.TryGetValue("delegation:" + delegationId, out var history))
                    throw new InvalidDataException("Fan-out member delegation is missing from replay.");
                if (history.Phase == PreM6RecordPhase.Failed)
                { memberStates.Add(new(delegationId, FanOutMemberStatus.Failed)); continue; }
                var childAcceptance = history.Facts.OfType<DelegationAccepted>().SingleOrDefault();
                if (history.Phase != PreM6RecordPhase.Returned || childAcceptance is null)
                {
                    memberStates.Add(new(delegationId, childAcceptance is null
                        ? FanOutMemberStatus.Queued : FanOutMemberStatus.Running));
                    continue;
                }
                var result = facts.OfType<AgentResultProduced>()
                    .Where(item => item.ExecutionId == childAcceptance.ChildExecutionId)
                    .OrderBy(item => item.ResultRevision).LastOrDefault();
                if (result is null)
                    throw new InvalidDataException("Returned fan-out member has no produced result.");
                var outcome = facts.OfType<ResultDispositionRecorded>().LastOrDefault(item =>
                    item.Disposition.ExecutionId == childAcceptance.ChildExecutionId
                    && item.Disposition.ResultRef == result.ResultRef)?.Disposition.Outcome;
                var status = outcome switch
                {
                    ResultDispositionOutcome.Accepted => FanOutMemberStatus.Accepted,
                    ResultDispositionOutcome.Rejected => FanOutMemberStatus.Rejected,
                    ResultDispositionOutcome.ReworkRequested => FanOutMemberStatus.ReworkRequested,
                    _ => FanOutMemberStatus.Returned,
                };
                memberStates.Add(new(delegationId, status,
                    status == FanOutMemberStatus.Accepted ? result.ResultRef : null));
            }
            var evaluation = FanInPolicyEvaluator.Evaluate(group, memberStates);
            if (!evaluation.Ready) continue;
            var memberResults = group.MemberDelegationIds.Select((delegationId, index) =>
            {
                var accepted = histories.Records["delegation:" + delegationId].Facts
                    .OfType<DelegationAccepted>().Single();
                return facts.OfType<AgentResultProduced>().Single(item => item.ExecutionId == accepted.ChildExecutionId
                    && item.ResultRef == evaluation.MemberResults[index]);
            }).ToList();

            ArtifactRef? aggregate = null;
            IPreparedArtifact? prepared = null;
            if (evaluation.RequiresAggregate)
            {
                if (_artifacts is not IArtifactPreparationStore preparation || _artifacts is not IArtifactPublicationLease)
                    throw new InvalidOperationException("Aggregate artifact publication is unavailable.");
                var members = memberResults.Select((result, index) =>
                {
                    var childResult = ReadAgentResult(result);
                    return new FanOutAggregateMember(group.MemberDelegationIds[index].ToString(),
                        childResult.Summary, childResult.Findings.ToArray());
                }).ToArray();
                var document = new FanOutAggregateDocument(1, group.GroupId.ToString(), members);
                prepared = preparation.PrepareText(JsonSerializer.Serialize(document, FanOutAggregateJson.Default.FanOutAggregateDocument),
                    "application/vnd.omnicore.agent-fanout+json", ArtifactKind.Other, Sensitivity.Sensitive);
                aggregate = prepared.Reference;
            }
            var resolvedEvent = new FanOutGroupResolved(group.OwnerExecutionId, group.GroupId,
                evaluation.MemberResults, aggregate);
            var acceptedReferences = evaluation.MemberResults.ToHashSet();
            var finalAcceptance = journal.Where(evt => evt.RunId == run).LastOrDefault(evt =>
                _codecs.Decode(evt) is ResultDispositionRecorded disposition
                && disposition.Disposition.Outcome == ResultDispositionOutcome.Accepted
                && acceptedReferences.Contains(disposition.Disposition.ResultRef))
                ?? throw new InvalidDataException("Fan-out accepted result has no canonical disposition event.");
            using var execution = ExecutionScope.Begin(AgentScope(run, facts, group.OwnerExecutionId));
            using var causation = CausationScope.Begin(new EventCausation(finalAcceptance.EventId));
            if (prepared is not null)
            {
                using var lease = ((IArtifactPublicationLease)_artifacts!).AcquirePublicationLease(CancellationToken.None);
                if (prepared.Publish() != prepared.Reference) throw new InvalidDataException("Aggregate artifact reference changed.");
                new EventStream(_store, _codecs, session).Append(resolvedEvent, DurabilityClass.Barrier);
            }
            else new EventStream(_store, _codecs, session).Append(resolvedEvent, DurabilityClass.Barrier);
        }
    }
}

internal sealed record FanOutAggregateMember(string DelegationId, string Summary, IReadOnlyList<string> Findings);
internal sealed record FanOutAggregateDocument(int Version, string GroupId, IReadOnlyList<FanOutAggregateMember> Members);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FanOutAggregateDocument))]
internal partial class FanOutAggregateJson : JsonSerializerContext;
