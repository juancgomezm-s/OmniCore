using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private CommandAck ControlDelegation(WireEnvelope command, string name, bool trusted, CancellationToken token)
    {
        if (!trusted) return new(command.MessageId, "error", "Trusted user action required", RuntimeCommandOutcome.Rejected());
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new(command.MessageId, "error", "No active Run", RuntimeCommandOutcome.Rejected());
        long? before = null;
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        try
        {
            before = _store.CurrentSequence(session);
            using var json = JsonDocument.Parse(command.PayloadJson);
            var input = json.RootElement;
            lock (_modeAuthorityMutationGate)
            {
                token.ThrowIfCancellationRequested();
                if (_lastSessionId != session || _lastRunId != run)
                    return Ack(RuntimeCommandOutcome.Deferred("RunNotActive"));
                var journal = _store.ReadFrom(session, 1);
                var records = PreM6RecordProjection.Replay(session, _codecs, journal);
                var own = journal.Where(e => e.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                if (journal.Any(e => e.Causation is CommandCausation c && c.CommandId == commandId))
                    return Ack(RuntimeCommandOutcome.NoOp());
                if (RunProjection.Replay(session, run, _codecs, own).IsTerminal())
                    throw new InvalidOperationException("Run is terminal.");
                var stream = new EventStream(_store, _codecs, session);
                if (name == "execution.join")
                {
                    var owner = new ExecutionId(Guid.Parse(input.GetProperty("ownerExecutionId").GetString()!));
                    var start = facts.OfType<AgentExecutionStarted>().Single(e => e.ExecutionId == owner);
                    if (start.ParentExecutionId is not null || IsExecutionTerminal(facts, owner))
                        throw new ArgumentException("Join owner must be the active principal.");
                    var members = input.GetProperty("members").EnumerateArray()
                        .Select(e => new ExecutionId(Guid.Parse(e.GetString()!))).ToArray();
                    foreach (var member in members)
                    {
                        if (!facts.OfType<AgentExecutionStarted>().Any(e => e.ExecutionId == member && e.ParentExecutionId == owner))
                            throw new ArgumentException("Join members must be direct children in this Run.");
                    }
                    var kind = Enum.Parse<JoinKind>(input.GetProperty("kind").GetString()!, true);
                    var required = input.TryGetProperty("requiredCount", out var count) ? count.GetInt32() : (int?)null;
                    var ids = input.TryGetProperty("requiredExecutionIds", out var list)
                        ? list.EnumerateArray().Select(e => new ExecutionId(Guid.Parse(e.GetString()!))).ToArray() : null;
                    var join = new ExecutionJoin(JoinId.New(), owner, members, new(kind, required, ids));
                    join.Policy.Validate(members);
                    var scope = AgentScope(run, facts, owner);
                    using var execution = ExecutionScope.Begin(scope);
                    var pending = PendingJoins(records, owner).Any();
                    var satisfying = ExecutionJoinEvaluator.SatisfyingMembers(join, AcceptedExecutions(facts));
                    var batch = new List<DomainEventPayload> { new ExecutionJoinCreated(owner, join) };
                    if (satisfying.Count != 0) batch.Add(new ExecutionJoinResolved(owner, join.JoinId, satisfying));
                    else if (!pending)
                    {
                        if (TaskGraphProjection.Replay(_codecs, own).Get(scope.TaskId!)?.State != TaskState.Running
                            || LaneProjection.Replay(_codecs, own).StateOf(scope.LaneId!) != LaneState.Running)
                            throw new InvalidOperationException("Principal is not at a join boundary.");
                        if (HasOpenModelStep(own) || HasOpenToolCall(own)) throw new InvalidOperationException("Execution boundary required.");
                        batch.Add(new TaskBlocked(scope.TaskId!, "ExecutionJoin"));
                        batch.Add(new LaneBlocked(scope.LaneId!, "ExecutionJoin"));
                    }
                    stream.AppendBatch(batch, DurabilityClass.Barrier);
                }
                else if (name == "execution.join.cancel")
                {
                    var id = new JoinId(Guid.Parse(input.GetProperty("joinId").GetString()!));
                    var history = records.Records["join:" + id];
                    if (history.Phase != PreM6RecordPhase.Created) return Ack(RuntimeCommandOutcome.NoOp());
                    var owner = history.OwnerExecutionId;
                    using var execution = ExecutionScope.Begin(AgentScope(run, facts, owner));
                    stream.Append(new ExecutionJoinFailed(owner, id, "Explicit user cancellation"), DurabilityClass.Barrier);
                    ResolveReadyJoins(session, run);
                }
                else
                {
                    var id = new DelegationId(Guid.Parse(input.GetProperty("delegationId").GetString()!));
                    var history = records.Records["delegation:" + id];
                    var delegation = history.Facts.OfType<DelegationCreated>().Single().Delegation;
                    if (!facts.OfType<DelegationCreated>().Any(e => e.Delegation == delegation))
                        throw new ArgumentException("Delegation belongs to another Run.");
                    if (name == "delegation.cancel")
                    {
                        if (history.Phase == PreM6RecordPhase.Failed) return Ack(RuntimeCommandOutcome.NoOp());
                        if (history.Phase == PreM6RecordPhase.Returned)
                        {
                            var state = TaskGraphProjection.Replay(_codecs, own).Get(delegation.ChildTaskId)!.State;
                            if (StateMachines.IsTaskTerminal(state)) return Ack(RuntimeCommandOutcome.NoOp());
                            var returnedChild = history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                            var returnedResult = facts.OfType<AgentResultProduced>().Last(e => e.ExecutionId == returnedChild);
                            var priorEvaluation = facts.OfType<ResultDispositionRecorded>().LastOrDefault(e => e.Disposition.ResultRef == returnedResult.ResultRef);
                            using var childExecution = ExecutionScope.Begin(AgentScope(run, facts, returnedChild));
                            var batch = new List<DomainEventPayload>();
                            if (priorEvaluation is null) batch.Add(new ResultDispositionRecorded(returnedChild,
                                new(DispositionId.New(), returnedChild, returnedResult.ResultRef, ResultDispositionOutcome.Rejected,
                                    null, "Explicit user cancellation before acceptance", [])));
                            batch.Add(new TaskCancelled(delegation.ChildTaskId)); batch.Add(new LaneCancelled(delegation.ChildLaneId));
                            stream.AppendBatch(batch, DurabilityClass.Barrier);
                            return Ack(RuntimeCommandOutcome.Accepted());
                        }
                        if (history.Phase == PreM6RecordPhase.Accepted)
                        {
                            var childId = history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                            if (!AgentCapacity.For(_store).Cancel(session, run, id, () => {
                                if (history.Facts.OfType<DelegationCancellationRequested>().Any()) return;
                                using var parentExecution = ExecutionScope.Begin(AgentScope(run, facts, delegation.ParentExecutionId));
                                stream.Append(new DelegationCancellationRequested(delegation.ParentExecutionId, id, childId), DurabilityClass.Barrier);
                            }))
                                return Ack(RuntimeCommandOutcome.Deferred("ExecutionOwnershipUnavailable"));
                            // Cancellation is acknowledged only as a request. Terminal facts are written at the safe boundary.
                            return Ack(RuntimeCommandOutcome.Accepted());
                        }
                        var parent = AgentScope(run, facts, delegation.ParentExecutionId);
                        var child = new ExecutionScopeState(run, delegation.ChildTaskId, delegation.ChildLaneId);
                        stream.AppendBatch(new DomainEventPayload[] { new TaskCancelled(delegation.ChildTaskId),
                            new LaneCancelled(delegation.ChildLaneId), new DelegationFailed(delegation.ParentExecutionId, id, "Explicit user cancellation") },
                            DurabilityClass.Barrier, new ExecutionScopeState?[] { child, child, parent });
                    }
                    else
                    {
                        if (history.Phase != PreM6RecordPhase.Returned) throw new InvalidOperationException("No returned result to evaluate.");
                        var executionId = history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId;
                        var produced = facts.OfType<AgentResultProduced>().Last(e => e.ExecutionId == executionId);
                        // Exact result identity prevents accepting an unseen replacement/rework revision.
                        if (input.GetProperty("resultId").GetString() != produced.ResultRef.Id.ToString())
                            throw new ArgumentException("Result identity is stale.");
                        var result = ReadAgentResult(produced);
                        var outcome = Enum.Parse<ResultDispositionOutcome>(input.GetProperty("outcome").GetString()!, true);
                        if (!Enum.IsDefined(outcome)) throw new ArgumentException("Unknown disposition.");
                        var reason = input.GetProperty("reason").GetString();
                        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4096) throw new ArgumentException("Explicit evaluation reason required.");
                        var prior = facts.OfType<ResultDispositionRecorded>().LastOrDefault(e => e.Disposition.ResultRef == produced.ResultRef);
                        if (prior is not null)
                        {
                            if (prior.Disposition.Outcome == outcome) return Ack(RuntimeCommandOutcome.NoOp());
                            throw new InvalidOperationException("This immutable result was already evaluated; new work requires a new result.");
                        }
                        if (StateMachines.IsTaskTerminal(TaskGraphProjection.Replay(_codecs, own).Get(delegation.ChildTaskId)!.State))
                            throw new InvalidOperationException("Child Task is terminal.");
                        if (!facts.OfType<AgentExecutionCompleted>().Any(e => e.ExecutionId == executionId)
                            || result.Outcome != AgentOutcome.Succeeded && outcome == ResultDispositionOutcome.Accepted)
                            throw new InvalidOperationException("A failed/nonterminal execution cannot be accepted.");
                        using var execution = ExecutionScope.Begin(AgentScope(run, facts, executionId));
                        var disposition = new ResultDisposition(DispositionId.New(), executionId, produced.ResultRef,
                            outcome, null, new PiiRedactor().Redact(reason), []);
                        var batch = new List<DomainEventPayload> { new ResultDispositionRecorded(executionId, disposition) };
                        if (outcome == ResultDispositionOutcome.Accepted)
                        { batch.Add(new LaneCompleted(delegation.ChildLaneId, result)); batch.Add(new TaskCompleted(delegation.ChildTaskId, result)); }
                        else
                        { batch.Add(new LaneBlocked(delegation.ChildLaneId, outcome.ToString())); batch.Add(new TaskBlocked(delegation.ChildTaskId, outcome.ToString())); }
                        stream.AppendBatch(batch, DurabilityClass.Barrier);
                        ResolveReadyJoins(session, run);
                    }
                }
                return Ack(RuntimeCommandOutcome.Accepted());
            }
        }
        catch (Exception failure)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value, failure.Message, restoreRunIdentity: false);
        }
        CommandAck Ack(RuntimeCommandOutcome outcome) => CommandOutcomeAck(command.MessageId, "ok", null,
            outcome, session, before!.Value, commandId);
    }

    private static bool IsExecutionTerminal(IEnumerable<DomainEventPayload> facts, ExecutionId id) => facts.Any(e =>
        e is AgentExecutionCompleted completed && completed.ExecutionId == id || e is AgentExecutionFailed failed && failed.ExecutionId == id);
    private static ExecutionScopeState AgentScope(RunId run, IEnumerable<DomainEventPayload> facts, ExecutionId id)
    {
        var started = facts.OfType<AgentExecutionStarted>().Single(e => e.ExecutionId == id);
        var lane = facts.OfType<LaneCreated>().Single(e => e.LaneId == started.LaneId);
        return new(run, lane.TaskId, lane.LaneId, ExecutionId: id);
    }
    private static IEnumerable<ExecutionJoin> PendingJoins(PreM6RecordProjection records, ExecutionId owner) =>
        records.Records.Values.Where(h => h.OwnerExecutionId == owner && h.Phase == PreM6RecordPhase.Created)
            .SelectMany(h => h.Facts.OfType<ExecutionJoinCreated>()).Select(e => e.Join);
    private static HashSet<ExecutionId> AcceptedExecutions(DomainEventPayload[] facts) => facts.OfType<AgentResultProduced>()
        .GroupBy(e => e.ExecutionId).Select(g => g.OrderBy(e => e.ResultRevision).Last())
        .Where(r => facts.OfType<AgentExecutionCompleted>().Any(e => e.ExecutionId == r.ExecutionId)
            && facts.OfType<ResultDispositionRecorded>().LastOrDefault(e => e.Disposition.ResultRef == r.ResultRef)?.Disposition.Outcome == ResultDispositionOutcome.Accepted)
        .Select(r => r.ExecutionId).ToHashSet();

    private void ResolveReadyJoins(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1);
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        var own = journal.Where(e => e.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        var accepted = AcceptedExecutions(facts);
        foreach (var owner in facts.OfType<ExecutionJoinCreated>().Select(e => e.ExecutionId).Distinct())
        {
            var pending = PendingJoins(records, owner).ToArray();
            var batch = new List<DomainEventPayload>();
            var unresolved = false;
            foreach (var join in pending)
            {
                var satisfying = ExecutionJoinEvaluator.SatisfyingMembers(join, accepted);
                if (satisfying.Count == 0) unresolved = true;
                else batch.Add(new ExecutionJoinResolved(owner, join.JoinId, satisfying));
            }
            var scope = AgentScope(run, facts, owner);
            if (!unresolved && TaskGraphProjection.Replay(_codecs, own).Get(scope.TaskId!)?.State == TaskState.Blocked
                && facts.OfType<TaskBlocked>().LastOrDefault(e => e.TaskId == scope.TaskId)?.Reason == "ExecutionJoin")
            { batch.Add(new TaskUnblocked(scope.TaskId!, false)); batch.Add(new LaneUnblocked(scope.LaneId!)); }
            if (batch.Count == 0) continue;
            using var execution = ExecutionScope.Begin(scope);
            new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Barrier);
        }
    }

    private AgentResult ReadAgentResult(AgentResultProduced produced)
    {
        if (produced.ResultSchemaId != "core.explorer.v1" || _artifacts is null)
            throw new InvalidDataException("Unsupported result schema.");
        var text = _artifacts.GetText(produced.ResultRef.Hash) ?? throw new InvalidDataException("Result artifact missing.");
        if (System.Text.Encoding.UTF8.GetByteCount(text) != produced.ResultRef.Size
            || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),
                produced.ResultRef.Hash.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Result artifact integrity mismatch.");
        var document = JsonSerializer.Deserialize(text, DelegationResultJson.Default.DelegationResultDocument)
            ?? throw new InvalidDataException("Invalid result.");
        if (document.Version != 1 || document.Outcome != "Succeeded" || document.Summary is null
            || document.Findings is null || document.RemainingIssues is null)
            throw new InvalidDataException("Invalid read-only result schema.");
        return new AgentResult(AgentOutcome.Succeeded, document.Summary, document.Findings, [], [],
            document.RemainingIssues, ConfidenceLevel.Low, []);
    }
}
