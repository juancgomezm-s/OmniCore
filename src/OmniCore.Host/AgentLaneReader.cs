using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using System.Text.Json;

namespace OmniCore.Host;

internal static class AgentLaneReader
{
    internal static AgentsSnapshot Read(IEventStore store, IEventCodecRegistry codecs, SessionId session, RunId? run,
        IArtifactStore? artifacts = null, IReadOnlyList<ChatActivityEvent>? activities = null)
    {
        var journal = store.ReadFrom(session, 1);
        var empty = new AgentsSnapshot(session.ToString(), run?.ToString(), journal.LastOrDefault()?.Sequence ?? 0,
            false, Array.Empty<AgentLaneSnapshot>());
        if (run is null) return empty;
        try
        {
            var records = PreM6RecordProjection.Replay(session, codecs, journal);
            var own = journal.Where(e => e.RunId == run).ToArray();
            if (!own.Any(e => codecs.Decode(e) is RunCreated)) return empty with { ProjectionUnavailable = true };
            var payloads = own.Select(codecs.Decode).ToArray();
            var tasks = TaskGraphProjection.Replay(codecs, own);
            var lanes = LaneProjection.Replay(codecs, own);
            var plan = PlanProjection.Replay(codecs, own);
            _ = payloads.OfType<RunCreated>().Single();
            var authority = RunProjection.Replay(session, run, codecs, own).ModeAuthority;
            var maximumAgents = authority?.Mode == RunMode.Orchestrate
                && authority.Authorization is { } authorization
                && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)
                ? Math.Max(1, authorization.Limits.MaxAgents) : 1;
            var capacity = AgentCapacity.For(store).ReadSnapshot(session, run);
            var rows = new List<AgentLaneSnapshot>();
            foreach (var lane in payloads.OfType<LaneCreated>())
            {
                var task = tasks.Get(lane.TaskId) ?? throw new InvalidDataException("Lane Task is missing.");
                var starts = payloads.OfType<AgentExecutionStarted>().Where(e => e.LaneId == lane.LaneId).Distinct().ToArray();
                // Never choose the latest of multiple executors by chronology.
                var execution = starts.Length == 1 ? starts[0] : null;
                var history = records.Records.Values.SingleOrDefault(h => h.Facts.OfType<DelegationCreated>()
                    .Any(d => d.Delegation.ChildLaneId == lane.LaneId));
                var delegation = history?.Facts.OfType<DelegationCreated>().Single().Delegation;
                var step = own.LastOrDefault(e => e.LaneId == lane.LaneId && codecs.Decode(e) is ModelStepStarted);
                var state = execution is null ? null : payloads.Any(e => e is AgentExecutionFailed f && f.ExecutionId == execution.ExecutionId)
                    ? "Failed" : payloads.Any(e => e is AgentExecutionCompleted c && c.ExecutionId == execution.ExecutionId)
                        ? "Completed" : "Started";
                var definition = payloads.OfType<TaskCreated>().Single(t => t.TaskId == lane.TaskId);
                var stageItem = plan.Items().SingleOrDefault(item => item.LinkedTasks.Any(link => link.TaskId == lane.TaskId));
                WorkflowStageContract? workflowStage = null;
                WorkflowStageEvidence? workflowEvidence = null;
                if (stageItem is not null && WorkflowStageContract.TryFromPlanItem(stageItem, out workflowStage))
                {
                    if (!WorkflowStageContract.IsCanonicalAdmission(workflowStage!, stageItem, session, run,
                        own, codecs, artifacts))
                        throw new InvalidDataException("Workflow stage Plan metadata is not backed by its canonical admission.");
                    if (execution is not null && workflowStage!.Execution == execution.ExecutionId)
                        workflowEvidence = WorkflowStageEvidenceEvaluator.Evaluate(workflowStage!, session, run,
                            lane.TaskId, lane.LaneId, execution.ExecutionId, own, codecs);
                }
                var taskEvents = own.Where(e => e.TaskId == lane.TaskId).ToArray();
                var taskFacts = taskEvents.Select(codecs.Decode).ToArray();
                var tokenState = RunTokenBudgetReader.Read(taskEvents, codecs, run, definition.Budget.MaxTokens);
                decimal? costUsed = null;
                if (artifacts is not null)
                {
                    var spend = new CanonicalSpendReader(codecs, artifacts);
                    var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var primarySpend = spend.ReadPrimary(taskEvents, session, run, today);
                    var metaSpend = spend.ReadMeta(taskEvents, session, run, today);
                    if (!primarySpend.Incomplete && !metaSpend.Incomplete
                        && primarySpend.RunUsd is { } primaryUsd && metaSpend.RunUsd is { } metaUsd)
                        costUsed = primaryUsd + metaUsd;
                }
                var budget = new AgentBudgetSnapshot(definition.Budget.MaxCostUsd, costUsed,
                    definition.Budget.MaxTokens, tokenState.Limitation is null ? tokenState.ObservedSettledTokens : null,
                    definition.Budget.MaxTurns, taskFacts.OfType<TurnStarted>().Select(t => t.TurnId).Distinct().Count(),
                    definition.Budget.MaxToolCalls, taskFacts.OfType<ToolCallRequested>().Select(t => t.ToolCallId).Distinct().Count());
                var result = execution is null ? null : payloads.OfType<AgentResultProduced>().LastOrDefault(e => e.ExecutionId == execution.ExecutionId);
                var disposition = result is null ? null : payloads.OfType<ResultDispositionRecorded>()
                    .LastOrDefault(e => e.Disposition.ResultRef == result.ResultRef)?.Disposition.Outcome.ToString();
                string? summary = null;
                int? resultIssueCount = null;
                string? resultOutcome = null;
                if (result is { ResultSchemaId: "core.explorer.v1" } && artifacts?.GetText(result.ResultRef.Hash) is { } text)
                {
                    var document = JsonSerializer.Deserialize(text, DelegationResultJson.Default.DelegationResultDocument);
                    if (document?.Version == 1)
                    {
                        resultOutcome = document.Outcome;
                        summary = new PiiRedactor().Redact(document.Summary);
                        resultIssueCount = document.RemainingIssues.Count;
                        if (summary.Length > 4096) summary = summary[..4096] + "…";
                    }
                }
                var joins = execution is null ? [] : records.Records.Values.Where(h => h.OwnerExecutionId == execution.ExecutionId
                    && h.Phase == PreM6RecordPhase.Created).SelectMany(h => h.Facts.OfType<ExecutionJoinCreated>())
                    .Select(e => e.Join.JoinId.ToString()).ToArray();
                var ownedBindings = execution is null ? [] : records.Records.Values
                    .Where(h => h.OwnerExecutionId == execution.ExecutionId)
                    .SelectMany(h => h.Facts.OfType<SupervisionBindingCreated>()).ToArray();
                string? supervisionState = null;
                if (ownedBindings.Length > 0)
                {
                    var bindingId = ownedBindings[^1].Binding.BindingId;
                    var phase = records.Records["binding:" + bindingId].Phase;
                    supervisionState = phase.ToString();
                    if (phase == PreM6RecordPhase.Accepted && payloads.Any(e => e is AgentExecutionFailed f
                        && f.ExecutionId == ownedBindings[^1].Binding.SupervisorExecutionId
                        || e is AgentExecutionCompleted c && c.ExecutionId == ownedBindings[^1].Binding.SupervisorExecutionId))
                        supervisionState = "SupervisorTerminal · Wait";
                    else if (phase == PreM6RecordPhase.Failed
                        && ownedBindings[^1].Binding.FailurePolicy == SupervisionFailurePolicy.Wait)
                        supervisionState = "Failed · Wait (subject blocked; joins unresolved)";
                }
                var mailboxIds = execution is null ? [] : records.Records.Values
                    .Where(h => h.OwnerExecutionId == execution.ExecutionId)
                    .SelectMany(h => h.Facts.OfType<ExecutionMailboxCreated>())
                    .Select(e => e.Mailbox.MailboxId).ToHashSet();
                var pendingMessages = records.Records.Values
                    .Where(h => h.OwnerExecutionId == execution?.ExecutionId)
                    .SelectMany(h => h.Facts.OfType<ExecutionMailboxMessageReceived>())
                    .Where(e => mailboxIds.Contains(e.Message.MailboxId))
                    .Count(e => !records.Records["message:" + e.Message.MessageId].Facts.OfType<ExecutionMailboxMessageAcknowledged>().Any());
                var pendingWakes = records.Records.Values
                    .Where(h => h.OwnerExecutionId == execution?.ExecutionId && h.Phase is PreM6RecordPhase.Created or PreM6RecordPhase.Accepted)
                    .SelectMany(h => h.Facts.OfType<WakeRequestCreated>()).Count();
                var scopedFacts = own.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
                    .Where(item => execution is not null && item.Event.ExecutionId == execution.ExecutionId
                        && item.Event.TaskId == lane.TaskId && item.Event.LaneId == lane.LaneId).ToArray();
                var integration = scopedFacts.Where(item => item.Payload is IntegrationStatusRecorded status
                        && status.Status.Scope.RunId == run && status.Status.Scope.TaskId == lane.TaskId
                        && status.Status.Scope.LaneId == lane.LaneId)
                    .OrderBy(item => item.Event.Sequence).LastOrDefault();
                var validation = scopedFacts.Where(item => item.Payload is ValidationStateRecorded state
                        && state.State.Scope.RunId == run && state.State.Scope.TaskId == lane.TaskId
                        && state.State.Scope.LaneId == lane.LaneId && state.State.Level == ValidationLevel.Integration)
                    .OrderBy(item => item.Event.Sequence).LastOrDefault();
                var integrationEvidence = integration.Payload is IntegrationStatusRecorded integrationRecord
                    ? integrationRecord.Status.EvidenceRefs : Array.Empty<EvidenceRef>();
                var validationEvidence = validation.Payload is ValidationStateRecorded validationRecord
                    ? validationRecord.State.EvidenceRefs : Array.Empty<EvidenceRef>();
                bool HasScopedReceipt(EvidenceRef item)
                {
                    if (item.Kind != EvidenceKind.ToolBacked || item.ReceiptRef is not { } receipt
                        || receipt.SessionId != session || execution is null) return false;
                    var eventReceipt = own.SingleOrDefault(evt => evt.SessionId == session && evt.EventId == receipt.EventId);
                    return eventReceipt is not null && eventReceipt.RunId == run && eventReceipt.TaskId == lane.TaskId
                        && eventReceipt.LaneId == lane.LaneId && eventReceipt.ExecutionId == execution.ExecutionId
                        && eventReceipt.ToolCallId == receipt.ToolCallId
                        && codecs.Decode(eventReceipt) is ToolCallSucceeded success && success.ToolCallId == receipt.ToolCallId;
                }
                var integrationEvidenceReady = integrationEvidence.Any(HasScopedReceipt);
                var validationEvidenceReady = validationEvidence.Any(HasScopedReceipt);
                var gateEvidenceCount = integrationEvidence.Concat(validationEvidence).Where(HasScopedReceipt)
                    .Select(item => item.ReceiptRef!.EventId).Distinct().Count();
                var resolvedDebtIds = payloads.OfType<ValidationDebtResolved>().Select(item => item.DebtId).ToHashSet();
                var validationDebts = payloads.OfType<ValidationDebtCreated>()
                    .Where(item => execution is not null && item.ExecutionId == execution.ExecutionId
                        && item.Debt.SubjectExecutionId == execution.ExecutionId
                        && item.Debt.Scope.SessionId == session && item.Debt.Scope.RunId == run
                        && item.Debt.Scope.TaskId == lane.TaskId && item.Debt.Scope.LaneId == lane.LaneId
                        && !resolvedDebtIds.Contains(item.Debt.DebtId))
                    .Select(item => new AgentValidationDebtSnapshot(item.Debt.DebtId.ToString(),
                        item.Debt.RequiredLevel.ToString(), item.Debt.MissingChecks.ToArray())).ToArray();
                var heartbeatActivity = activities?.Where(item => item.ObservedInProcess
                        && item.RunId == run.ToString()
                        && item.LaneId == lane.LaneId.ToString())
                    .OrderBy(item => item.Sequence).LastOrDefault();
                var progressEvent = own.LastOrDefault(evt => evt.RunId == run && evt.LaneId == lane.LaneId
                    && codecs.Decode(evt) is TurnStarted or ModelStepStarted or AssistantMessageRecorded
                        or ToolCallRequested or ToolCallSucceeded or ToolCallFailed or InteractionRequested
                        or InteractionResolved);
                var waitingForCapacity = delegation is not null
                    && capacity.WaitingDelegations.Contains(delegation.DelegationId);
                var openMailboxReceive = execution is not null && scopedFacts
                    .Where(item => item.Payload is ToolCallRequested requested
                        && requested.ToolName == "core.agents.mailbox.receive")
                    .Select(item => ((ToolCallRequested)item.Payload).ToolCallId)
                    .Any(call => !scopedFacts.Any(item => item.Event.ToolCallId == call
                        && item.Payload is ToolCallSucceeded or ToolCallFailed or ToolCallReconciled));
                var waitingForJoin = joins.Length > 0 && task.State == TaskState.Blocked;
                var supervisionWait = supervisionState?.Contains("Wait", StringComparison.Ordinal) == true;
                var heartbeat = new AgentLaneHeartbeatSnapshot(waitingForCapacity ? "WaitingForCapacity"
                    : openMailboxReceive ? "WaitingForMailbox" : waitingForJoin ? "WaitingForJoin"
                    : supervisionWait ? "SupervisionWait" : heartbeatActivity?.Phase.ToString()
                        ?? (state is "Completed" or "Failed" ? state : "Unknown"),
                    heartbeatActivity?.TurnId ?? progressEvent?.TurnId?.ToString(),
                    heartbeatActivity?.AsOf ?? progressEvent?.Timestamp,
                    heartbeatActivity?.Source ?? (progressEvent is null ? "unknown after reopen (journal is not liveness)" : "journal progress (not liveness)"));
                var transcript = starts.Length > 1 ? Array.Empty<AgentTranscriptEntry>()
                    : BuildTranscript(own, codecs, artifacts, run, lane.TaskId, lane.LaneId, execution);
                // Historical status/receipt facts remain in the journal, but a current
                // workflow gate failure or unresolved debt means they cannot be presented
                // as current verification for this lane.
                var currentIntegrationEvidence = workflowEvidence?.Passed != false && validationDebts.Length == 0;
                var integrationVerified = currentIntegrationEvidence
                    && integration.Payload is IntegrationStatusRecorded latestIntegration
                    && latestIntegration.Status.Status == IntegrationStatus.Verified && integrationEvidenceReady;
                var integrationValidationPassed = currentIntegrationEvidence
                    && validation.Payload is ValidationStateRecorded latestValidation
                    && latestValidation.State.Status == ValidationStatus.Passed && validationEvidenceReady;
                rows.Add(new(lane.LaneId.ToString(), lane.TaskId.ToString(), definition.ParentTaskId?.ToString(),
                    new PiiRedactor().Redact(task.Objective), lanes.StateOf(lane.LaneId)!.Value.ToString(), task.State.ToString(),
                    lane.AgentProfile.ToString(), lane.AgentProfileRevision, execution?.ExecutionId.ToString(),
                    execution?.ParentExecutionId?.ToString(), state, delegation?.DelegationId.ToString(),
                    history?.Phase.ToString(), step is null ? null : ((ModelStepStarted)codecs.Decode(step)).ModelId,
                    step is null || ((ModelStepStarted)codecs.Decode(step)).ContextSnapshotRef is null ? null : step.EventId.ToString(),
                    starts.Length > 1, step is null ? null : ContextInheritanceService.SelectableItems(artifacts, codecs, step),
                    result?.ResultRef.Id.ToString(), disposition, summary, joins,
                    history?.Facts.OfType<DelegationCancellationRequested>().Any() == true, budget,
                    supervisionState, pendingMessages, pendingWakes, resultIssueCount,
                    integrationVerified, integrationValidationPassed, gateEvidenceCount,
                    workflowStage?.Workflow.Id, workflowStage?.Definition.Name, workflowStage?.Instance,
                    workflowEvidence?.Passed, workflowEvidence?.Reason, resultOutcome, validationDebts,
                    heartbeat, transcript));
            }
            var waiting = capacity.WaitingDelegations.Select(id => id.ToString()).Order(StringComparer.Ordinal).ToArray();
            var fanOutGroups = payloads.OfType<FanOutGroupCreated>().Select(created =>
            {
                var completed = payloads.OfType<FanOutGroupResolved>().SingleOrDefault(item => item.GroupId == created.Group.GroupId);
                var members = created.Group.MemberDelegationIds.ToList();
                foreach (var replacement in payloads.OfType<FanOutGroupMemberReplaced>()
                    .Where(item => item.GroupId == created.Group.GroupId))
                {
                    var slot = members.IndexOf(replacement.PreviousDelegationId);
                    if (slot >= 0) members[slot] = replacement.ReplacementDelegationId;
                }
                var currentGroup = created.Group with { MemberDelegationIds = members };
                var statuses = members.Select(id => FanOutMemberState(id, records, payloads)).ToArray();
                var fanIn = FanInPolicyEvaluator.Evaluate(currentGroup, statuses);
                return new FanOutGroupSnapshot(created.Group.GroupId.ToString(), created.Group.OwnerExecutionId.ToString(),
                    created.Group.FanInPolicy.ToString(), members.Select(id => id.ToString()).ToArray(),
                    completed is null ? fanIn.WaitingReason : "Resolved",
                    completed?.MemberResultRefs.Select(reference => reference.Id.ToString()).ToArray() ?? [],
                    completed?.AggregateRef?.Id.ToString());
            }).ToArray();
            var runActivities = activities?.Where(item => item.ObservedInProcess
                && item.RunId == run.ToString()).ToArray();
            var activeObservedLanes = runActivities is null ? 0 : runActivities
                .GroupBy(item => item.LaneId ?? "")
                .Where(group => group.Key.Length > 0 && group.OrderBy(item => item.Sequence).Last().Phase
                    is not (ChatActivityPhase.Completed or ChatActivityPhase.Cancelled or ChatActivityPhase.Failed))
                .Select(group => group.Key).Distinct(StringComparer.Ordinal).Count();
            var aggregateHeartbeat = runActivities is not { Length: > 0 }
                ? new AgentHeartbeatSnapshot("Unknown", 0, rows.Count(row => row.Heartbeat?.State == "WaitingForCapacity"),
                    rows.Count(row => row.Heartbeat?.State == "WaitingForJoin"),
                    rows.Count(row => row.Heartbeat?.State == "SupervisionWait"), null,
                    "no transient activity snapshot; journal state cannot establish liveness")
                : new AgentHeartbeatSnapshot(activeObservedLanes > 0 ? "ObservedActive" : "ObservedNoActiveTurn",
                    activeObservedLanes, rows.Count(row => row.Heartbeat?.State == "WaitingForCapacity"),
                    rows.Count(row => row.Heartbeat?.State == "WaitingForJoin"),
                    rows.Count(row => row.Heartbeat?.State == "SupervisionWait"),
                    runActivities.OrderBy(item => item.Sequence).Last().AsOf,
                    "transient in-process lane activity; not a worker lease");
            return empty with { Lanes = rows.ToArray(), Capacity = new AgentCapacitySnapshot(capacity.Active,
                capacity.Waiting, maximumAgents, capacity.WriterActive, waiting), FanOutGroups = fanOutGroups,
                Heartbeat = aggregateHeartbeat };
        }
        catch (Exception failure) when (failure is InvalidDataException or InvalidStateTransitionException or InvalidOperationException or JsonException)
        {
            return empty with { ProjectionUnavailable = true };
        }
    }

    private static FanOutMemberEvaluation FanOutMemberState(DelegationId id, PreM6RecordProjection records,
        IReadOnlyList<DomainEventPayload> payloads)
    {
        if (!records.Records.TryGetValue("delegation:" + id, out var history))
            throw new InvalidDataException("Fan-out member history is missing.");
        if (history.Phase == PreM6RecordPhase.Failed) return new(id, FanOutMemberStatus.Failed);
        var acceptance = history.Facts.OfType<DelegationAccepted>().SingleOrDefault();
        if (history.Phase != PreM6RecordPhase.Returned || acceptance is null)
            return new(id, acceptance is null ? FanOutMemberStatus.Queued : FanOutMemberStatus.Running);
        var result = payloads.OfType<AgentResultProduced>().Where(item => item.ExecutionId == acceptance.ChildExecutionId)
            .OrderBy(item => item.ResultRevision).LastOrDefault();
        var disposition = result is null ? null : payloads.OfType<ResultDispositionRecorded>()
            .LastOrDefault(item => item.Disposition.ExecutionId == acceptance.ChildExecutionId
                && item.Disposition.ResultRef == result.ResultRef)?.Disposition.Outcome;
        var status = disposition switch
        {
            ResultDispositionOutcome.Accepted => FanOutMemberStatus.Accepted,
            ResultDispositionOutcome.Rejected => FanOutMemberStatus.Rejected,
            ResultDispositionOutcome.ReworkRequested => FanOutMemberStatus.ReworkRequested,
            _ => FanOutMemberStatus.Returned,
        };
        return new(id, status, status == FanOutMemberStatus.Accepted ? result!.ResultRef : null);
    }

    private static IReadOnlyList<AgentTranscriptEntry> BuildTranscript(IReadOnlyList<DomainEvent> journal,
        IEventCodecRegistry codecs, IArtifactStore? artifacts, RunId run, TaskId task, LaneId lane,
        AgentExecutionStarted? execution)
    {
        var messages = new List<AgentTranscriptEntry>();
        var ownedTurns = execution is null ? new HashSet<TurnId>() : journal
            .Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
                && item.ExecutionId == execution.ExecutionId && item.TurnId is not null)
            .Select(item => item.TurnId!).ToHashSet();
        foreach (var evt in journal.Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
                     && (execution is null
                         ? item.ExecutionId is null
                         : item.ExecutionId == execution.ExecutionId
                            || item.ExecutionId is null && item.TurnId is { } turn && ownedTurns.Contains(turn))))
        {
            var payload = codecs.Decode(evt);
            string? kind = null;
            string? text = null;
            switch (payload)
            {
                case UserInputReceived input when input.RunId == run:
                    kind = "user"; text = TranscriptInput(input.InputPartsJson); break;
                case AssistantMessageRecorded assistant when assistant.RunId == run && assistant.LaneId == lane:
                    kind = "assistant";
                    if (assistant.ContentRef is { } reference && artifacts is not null)
                    {
                        try
                        {
                            if (artifacts.Verify(reference.Hash, reference.Size))
                                text = artifacts.GetText(reference.Hash);
                        }
                        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
                        { text = null; }
                    }
                    break;
                case ToolCallRequested request:
                    kind = "tool"; text = request.ToolName; break;
                case ToolCallSucceeded:
                    kind = "tool receipt"; text = "succeeded"; break;
                case ToolCallFailed failed:
                    kind = "tool receipt"; text = failed.Cause; break;
            }
            if (kind is null || string.IsNullOrWhiteSpace(text)) continue;
            text = new PiiRedactor().Redact(text);
            if (text.Length > 1024) text = text[..1024] + "…";
            messages.Add(new(evt.Sequence, kind, text, evt.EventId.ToString(), evt.RunId?.ToString(),
                evt.TaskId?.ToString(), evt.LaneId?.ToString(), evt.TurnId?.ToString(),
                evt.ExecutionId?.ToString(), evt.ToolCallId?.ToString()));
        }
        return messages.TakeLast(12).ToArray();
    }

    private static string TranscriptInput(string inputPartsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputPartsJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString() ?? "",
                JsonValueKind.Array => string.Join(" ", document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())),
                _ => "",
            };
        }
        catch (JsonException) { return ""; }
    }
}
