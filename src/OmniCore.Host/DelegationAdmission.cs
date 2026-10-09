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
    private CommandAck CreateQueuedDelegation(WireEnvelope command, bool trustedUserAction, CancellationToken cancellationToken)
    {
        if (!trustedUserAction)
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
                || request.MaximumPacketBytes is <= 0 or > ContextInheritanceService.MaximumDelegationPacketBytes)
                throw new ArgumentException("Explicit profile, source, objective and finite budgets are required.");
            var selection = new ContextInheritancePolicy(request.SelectedItemIds);
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                if (HasOpenModelStep(own) || HasOpenToolCall(own)) return Deferred("ExecutionBoundaryRequired");
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
                if (own.Select(_codecs.Decode).Any(e => e is AgentExecutionCompleted c && c.ExecutionId == parentId
                    || e is AgentExecutionFailed f && f.ExecutionId == parentId)) return Deferred("ParentExecutionTerminal");
                var parentProfile = ResolveLaneAgentProfile(session, run, parentLane);
                var profile = _agentProfiles.Registry.Find(new ProfileId(profileId));
                if (parentProfile is null || profile is null) return Deferred("ConfiguredAgentProfileRequired");
                var ceiling = profile.PermissionCeiling;
                // Exact read rules are a conservative subset check. Never invent glob inclusion.
                if (ceiling.Writes.Count != 0 || ceiling.Process.Count != 0 || ceiling.Network.Count != 0
                    || ceiling.Secrets.Count != 0 || ceiling.AllowShell
                    || ceiling.Reads.Any(rule => !parentProfile.PermissionCeiling.Reads.Contains(rule, StringComparer.Ordinal)))
                    return Deferred("ReadOnlyChildCeilingRequired");
                var payloads = own.Select(_codecs.Decode).ToArray();
                var children = payloads.OfType<TaskCreated>().Where(t => t.ParentTaskId is not null).ToArray();
                var limits = authorization.Limits;
                // Count all admitted child ceilings conservatively for further admissions.
                // This is not a RunBudgetPool reservation: a future dispatcher must revalidate
                // remaining resources, and root execution still uses its existing Run guards.
                var executors = payloads.OfType<AgentExecutionStarted>().DistinctBy(e => e.ExecutionId).ToArray();
                var executingLanes = executors.Select(e => e.LaneId).ToHashSet();
                var occupiedAgentSlots = executors.LongLength + payloads.OfType<LaneCreated>()
                    .LongCount(lane => !executingLanes.Contains(lane.LaneId));
                if (limits.MaxDepth < 1 || 1L + occupiedAgentSlots > limits.MaxAgents)
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
                if ((long)request.MaxTurns + children.Sum(t => (long)t.Budget.MaxTurns!.Value)
                        + payloads.OfType<TurnStarted>().LongCount() > limits.MaxTurns
                    || (long)request.MaxToolCalls + children.Sum(t => (long)t.Budget.MaxToolCalls!.Value)
                        + payloads.OfType<ToolCallRequested>().LongCount() > limits.MaxToolCalls
                    || request.MaxCostUsd + children.Sum(t => t.Budget.MaxCostUsd!.Value)
                        + spent > limits.MaxSpendUsd)
                    return Deferred("CoordinationBudgetLimit");
                var rootBudget = payloads.OfType<RunCreated>().Single().Budget;
                if (rootBudget.MaxTokens is { } tokens)
                {
                    var tokenBudget = RunTokenBudgetReader.Read(own, _codecs, run, tokens);
                    if (tokenBudget.Remaining is null || children.Any(t => t.Budget.MaxTokens is null))
                        return Deferred("TokenAccountingUnavailable");
                    if (checked(request.MaxTokens + children.Sum(t => t.Budget.MaxTokens!.Value)) > tokenBudget.Remaining)
                        return Deferred("ParentTokenBudgetLimit");
                }
                if (rootBudget.MaxCostUsd is { } cost && request.MaxCostUsd + children.Sum(t => t.Budget.MaxCostUsd!.Value)
                    + spent > cost) return Deferred("ParentCostBudgetLimit");
                if (rootBudget.MaxTurns is { } turns && (long)request.MaxTurns + children.Sum(t => (long)t.Budget.MaxTurns!.Value)
                    + payloads.OfType<TurnStarted>().LongCount() > turns) return Deferred("ParentTurnBudgetLimit");
                if (rootBudget.MaxToolCalls is { } calls && (long)request.MaxToolCalls + children.Sum(t => (long)t.Budget.MaxToolCalls!.Value)
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
                    profile.Id, prepared.Reference, target.Relation, target.Supervision);
                var childScope = new ExecutionScopeState(run, childTask.TaskId, childLane.LaneId);
                var parentScope = new ExecutionScopeState(run, projection.RootTask, parentLane, ExecutionId: parentId);
                new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[] {
                    childTask, new TaskReady(childTask.TaskId), childLane, new DelegationCreated(parentId, delegation),
                }, DurabilityClass.Barrier, new ExecutionScopeState?[] { childScope, childScope, childScope, parentScope });
                return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value, failure.Message, restoreRunIdentity: false);
        }
    }
}
