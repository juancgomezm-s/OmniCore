using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed class AgentsProjection
{
    public string? SessionId { get; private set; }
    public AgentsSnapshot? Snapshot { get; private set; }
    public void Activate(string? session)
    {
        if (SessionId == session) return;
        SessionId = session; Snapshot = null;
    }
    public bool Apply(AgentsSnapshot snapshot)
    {
        if (snapshot.SessionId != SessionId || snapshot.BasedOnJournalSequence < (Snapshot?.BasedOnJournalSequence ?? 0)) return false;
        Snapshot = snapshot;
        return true;
    }
    public bool Poll(IOmniClient client, CancellationToken cancellationToken = default)
    {
        var result = client.Query("agents", cancellationToken);
        return result is not null && AgentsJson.Decode(result.Json) is { } snapshot && Apply(snapshot);
    }
}

public static class AgentPresentation
{
    public static ISidebarWidget Build(AgentsSnapshot? snapshot, string locale, bool details)
        => new DataSidebarWidget("core.agents", new Localization(locale).Resolve("sidebar.agents_title"), 75, Rows(snapshot, locale, details));

    public static string Describe(AgentsSnapshot? snapshot, string locale)
        => string.Join("\n", Rows(snapshot, locale, true).Select(row => row.Text));

    private static string State(string? value, Localization text) => value is null ? "" : ShellUiResources.ContainsKey("state." + value) ? text.Resolve("state." + value) : value;

    private static IReadOnlyList<WidgetRowModel> Rows(AgentsSnapshot? snapshot, string locale, bool details)
    {
        var text = new Localization(locale);
        var rows = new List<WidgetRowModel>();
        if (snapshot is null || snapshot.ProjectionUnavailable)
        {
            rows.Add(new(snapshot is null ? text.Resolve("sidebar.no_session") : text.Resolve("sidebar.projection_unavailable"), ThemeRole.Muted));
            return rows;
        }
        if (snapshot.Capacity is { } capacity)
        {
            rows.Add(new(text.Resolve("sidebar.capacity_active") + capacity.Active + "/" + capacity.Maximum
                + text.Resolve("sidebar.waiting") + capacity.Waiting
                + (capacity.WriterActive ? text.Resolve("sidebar.writer_active") : text.Resolve("sidebar.readers_only")),
                capacity.Waiting > 0 ? ThemeRole.Attention : ThemeRole.Info));
            if (details && capacity.WaitingDelegationIds.Count > 0)
                rows.Add(new(text.Resolve("sidebar.waiting_delegations")
                    + string.Join(", ", capacity.WaitingDelegationIds), ThemeRole.Muted));
        }
        if (snapshot.Heartbeat is { } aggregateHeartbeat)
        {
            rows.Add(new(text.Resolve("sidebar.aggregate_heartbeat") + State(aggregateHeartbeat.State, text)
                + text.Resolve("sidebar.observed_active") + aggregateHeartbeat.ObservedActiveLanes
                + text.Resolve("sidebar.capacity_wait") + aggregateHeartbeat.WaitingForCapacity
                + text.Resolve("sidebar.join") + aggregateHeartbeat.WaitingForJoin
                + text.Resolve("sidebar.supervision") + aggregateHeartbeat.SupervisionWaitLanes,
                aggregateHeartbeat.State == "Unknown" ? ThemeRole.Muted : ThemeRole.Info));
            if (details)
                rows.Add(new(text.Resolve("sidebar.heartbeat_source") + aggregateHeartbeat.Source
                    + (aggregateHeartbeat.LastProgressAt is { } progress
                        ? " · " + progress.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        : text.Resolve("sidebar.time_unknown")), ThemeRole.Muted));
        }
        foreach (var group in snapshot.FanOutGroups ?? Array.Empty<FanOutGroupSnapshot>())
        {
            rows.Add(new(text.Resolve("sidebar.fan_out") + group.Policy + " · " + State(group.State, text)
                + " · " + group.DelegationIds.Count + text.Resolve("sidebar.lanes"),
                group.State == "Resolved" ? ThemeRole.Success : ThemeRole.Attention));
            if (details)
            {
                rows.Add(new("Fan-out " + group.GroupId + text.Resolve("sidebar.owner") + group.OwnerExecutionId, ThemeRole.Muted));
                rows.Add(new(text.Resolve("sidebar.delegations") + string.Join(", ", group.DelegationIds), ThemeRole.Muted));
                if (group.AggregateId is { } aggregate) rows.Add(new(text.Resolve("sidebar.aggregate") + aggregate, ThemeRole.Info));
            }
        }
        foreach (var lane in snapshot.Lanes)
        {
            var state = lane.LaneState switch {
                "Queued" => text.Resolve("sidebar.queued_no_worker"),
                "Running" => lane.DelegationState == "Returned" ? text.Resolve("sidebar.result_awaiting_evaluation")
                    : lane.Heartbeat?.State is "ObservedActive" or "WaitingForCapacity" or "WaitingForMailbox"
                        or "WaitingForJoin" or "SupervisionWait" or "WaitingForResponse" or "Reasoning" or "AnswerText" or "Tools" or "WaitingForApproval"
                        ? text.Resolve("sidebar.lane_observed_active")
                        : text.Resolve("sidebar.lane_in_progress_worker_not_observed"),
                "Blocked" => text.Resolve("sidebar.blocked"),
                "Completed" => text.Resolve("sidebar.completed"), "Cancelled" => text.Resolve("sidebar.cancelled"),
                "Failed" => text.Resolve("sidebar.failed"), _ => lane.LaneState,
            };
            var role = lane.LaneState is "Failed" ? ThemeRole.Error : lane.LaneState is "Queued" or "Blocked" ? ThemeRole.Attention : ThemeRole.Agent;
            rows.Add(new((lane.ParentTaskId is null ? text.Resolve("sidebar.root") : text.Resolve("sidebar.delegate")) + " · " + state, role));
            rows.Add(new(lane.Objective, ThemeRole.Muted));
            if (lane.Model is { } model) rows.Add(new(model, ThemeRole.Info));
            if (lane.ExecutionAmbiguous) rows.Add(new(text.Resolve("sidebar.ambiguous_execution_identity"), ThemeRole.Error));
            if (lane.CancellationRequested && lane.DelegationState == "Accepted")
                rows.Add(new(text.Resolve("sidebar.cancellation_requested_checkpoint_pending"), ThemeRole.Attention));
            if (lane.ResultId is { } result) rows.Add(new(text.Resolve("sidebar.result") + result + " · " + (lane.ResultDisposition is { } disposition ? State(disposition, text) : text.Resolve("sidebar.not_accepted")), ThemeRole.Attention));
            if (lane.ResultOutcome is { } resultOutcome)
                rows.Add(new(text.Resolve("sidebar.outcome") + State(resultOutcome, text),
                    resultOutcome == "Succeeded" ? ThemeRole.Info : ThemeRole.Error));
            if (lane.ResultIssueCount is { } issueCount && issueCount > 0)
                rows.Add(new(text.Resolve("sidebar.remaining_issues") + issueCount, ThemeRole.Error));
            if (lane.WorkflowId is { } workflowId)
                rows.Add(new(text.Resolve("sidebar.workflow") + workflowId + " · " + lane.WorkflowStage
                    + " · " + (lane.WorkflowGateSatisfied == true ? text.Resolve("sidebar.gate_passed")
                        : lane.WorkflowGateSatisfied == false ? text.Resolve("sidebar.gate_pending") : text.Resolve("sidebar.gate_unevaluated")),
                    lane.WorkflowGateSatisfied == true ? ThemeRole.Success : ThemeRole.Attention));
            if (lane.WorkflowGateReason is { } workflowReason)
                rows.Add(new(text.Resolve("sidebar.workflow_gate") + workflowReason, ThemeRole.Muted));
            if (lane.Heartbeat is { } heartbeat && heartbeat.State is not ("Completed" or "Cancelled" or "Failed"))
                rows.Add(new(text.Resolve("sidebar.heartbeat") + State(heartbeat.State, text),
                    heartbeat.State is "WaitingForCapacity" or "WaitingForResponse" or "Reasoning" or "Tools"
                        ? ThemeRole.Attention : ThemeRole.Muted));
            if (lane.ExternalActivity is { } externalActivity)
                rows.Add(new(text.Resolve("sidebar.final_external_activity_tools")
                    + externalActivity.ToolUseCount + "/" + externalActivity.ToolResultCount
                    + text.Resolve("sidebar.retries") + externalActivity.ApiRetryCount,
                    externalActivity.PermissionDenialCount > 0 ? ThemeRole.Attention : ThemeRole.Muted));
            if (lane.ValidationDebts is { Count: > 0 } debts)
            {
                rows.Add(new(text.Resolve("sidebar.validation_debt")
                    + debts.Count + text.Resolve("sidebar.pending"), ThemeRole.Attention));
                if (details)
                    foreach (var debt in debts)
                        foreach (var check in debt.MissingChecks)
                            rows.Add(new("  " + debt.RequiredLevel + " · " + check, ThemeRole.Error));
            }
            if (lane.PendingJoinIds is { Count: > 0 } joins) rows.Add(new("Join · " + string.Join(", ", joins), ThemeRole.Attention));
            if (!details) continue;
            rows.Add(new("Lane " + lane.LaneId, ThemeRole.Muted));
            rows.Add(new(text.Resolve("sidebar.task") + lane.TaskId + " · " + State(lane.TaskState, text), ThemeRole.Muted));
            rows.Add(new(text.Resolve("sidebar.profile") + lane.ProfileId + (lane.ProfileRevision is { } revision ? " · rev." + revision : text.Resolve("sidebar.legacy")), ThemeRole.Muted));
            if (lane.ExecutionId is { } execution) rows.Add(new(text.Resolve("sidebar.execution") + execution + " · " + State(lane.ExecutionState, text), ThemeRole.Muted));
            if (lane.Heartbeat is { } detailHeartbeat)
                rows.Add(new(text.Resolve("sidebar.heartbeat") + State(detailHeartbeat.State, text)
                    + (detailHeartbeat.TurnId is { } heartbeatTurn ? " · Turn " + heartbeatTurn : "")
                    + (detailHeartbeat.LastProgressAt is { } progress ? " · " + progress.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : " · " + text.Resolve("state.Unknown"))
                    + " · " + detailHeartbeat.Source, ThemeRole.Muted));
            if (lane.ExternalActivity is { } activityDetail)
                rows.Add(new(text.Resolve("sidebar.final_external_activity")
                    + text.Resolve("sidebar.uses") + activityDetail.ToolUseCount
                    + text.Resolve("sidebar.results") + activityDetail.ToolResultCount
                    + text.Resolve("sidebar.denials") + activityDetail.PermissionDenialCount
                    + text.Resolve("sidebar.api_retries") + activityDetail.ApiRetryCount
                    + text.Resolve("sidebar.deltas") + activityDetail.StreamDeltaCount,
                    activityDetail.PermissionDenialCount > 0 ? ThemeRole.Attention : ThemeRole.Muted));
            if (lane.ParentExecutionId is { } parent) rows.Add(new(text.Resolve("sidebar.parent") + parent, ThemeRole.Muted));
            if (lane.DelegationId is { } delegation) rows.Add(new(text.Resolve("sidebar.delegation") + delegation + " · " + State(lane.DelegationState, text), ThemeRole.Muted));
            if (lane.Budget is { } budget)
                rows.Add(new(text.Resolve("sidebar.budget")
                    + (budget.MaxTokens is { } maxTokens ? $"tokens {budget.TokensUsed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}/{maxTokens}" : text.Resolve("sidebar.tokens_unbounded"))
                    + (budget.MaxTurns is { } maxTurns ? text.Resolve("sidebar.turns") + $"{budget.TurnsUsed}/{maxTurns}" : "")
                    + (budget.MaxToolCalls is { } maxTools ? text.Resolve("sidebar.tools") + $"{budget.ToolCallsUsed}/{maxTools}" : "")
                    + (budget.MaxCostUsd is { } maxCost ? $" · USD {(budget.CostUsedUsd?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "? ")}/{maxCost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" : ""), ThemeRole.Muted));
            if (lane.SupervisionState is { } supervision)
                rows.Add(new(text.Resolve("sidebar.supervision_label") + supervision,
                    supervision.Contains("Failed", StringComparison.Ordinal) || supervision.Contains("Terminal", StringComparison.Ordinal)
                        ? ThemeRole.Error : ThemeRole.Muted));
            if (lane.ParentTaskId is not null)
                rows.Add(new(text.Resolve("sidebar.integration")
                    + (lane.IntegrationVerified ? text.Resolve("sidebar.verified") : text.Resolve("sidebar.pending_label"))
                    + " · " + (lane.IntegrationValidationPassed ? text.Resolve("sidebar.validation_passed") : text.Resolve("sidebar.validation_pending"))
                    + " · " + text.Resolve("sidebar.tool_backed_receipts") + lane.ToolBackedEvidenceCount,
                    lane.IntegrationVerified && lane.IntegrationValidationPassed && lane.ToolBackedEvidenceCount > 0
                        ? ThemeRole.Success : ThemeRole.Attention));
            if (lane.PendingMailboxMessages > 0 || lane.PendingWakeRequests > 0)
                rows.Add(new(text.Resolve("sidebar.mailbox", new Dictionary<string, string> { ["messages"] = lane.PendingMailboxMessages.ToString(), ["wakes"] = lane.PendingWakeRequests.ToString() }), ThemeRole.Attention));
            if (lane.ResultSummary is { } summary) rows.Add(new(summary, ThemeRole.Muted));
            if (lane.LastContextEventId is { } source) rows.Add(new(text.Resolve("sidebar.context_source") + source, ThemeRole.Muted));
            if (lane.SelectableContextItemIds is { Count: > 0 } items)
                rows.Add(new(text.Resolve("sidebar.available_selection") + string.Join(", ", items), ThemeRole.Muted));
            foreach (var transcript in lane.Transcript ?? Array.Empty<AgentTranscriptEntry>())
                rows.Add(new(text.Resolve("sidebar.transcript") + transcript.Kind + " · " + transcript.Text,
                    transcript.Kind == "tool receipt" ? ThemeRole.Info : ThemeRole.Muted));
        }
        if (rows.Count == 0) rows.Add(new(text.Resolve("sidebar.no_lanes_in_this_run"), ThemeRole.Muted));
        return rows;
    }
}

public static class DelegationCommands
{
    public static WireEnvelope Create(DelegationCreateRequest request, string commandId)
        => WireEnvelope.Command(commandId, "{\"cmd\":\"delegation.create\",\"request\":" + AgentsJson.EncodeRequest(request) + "}");
}
