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
        => new DataSidebarWidget("core.agents", locale == "en" ? "AGENTS · LANES" : "AGENTES · LANES", 75, Rows(snapshot, locale, details));

    public static string Describe(AgentsSnapshot? snapshot, string locale)
        => string.Join("\n", Rows(snapshot, locale, true).Select(row => row.Text));

    private static IReadOnlyList<WidgetRowModel> Rows(AgentsSnapshot? snapshot, string locale, bool details)
    {
        string L(string es, string en) => locale == "en" ? en : es;
        var rows = new List<WidgetRowModel>();
        if (snapshot is null || snapshot.ProjectionUnavailable)
        {
            rows.Add(new(snapshot is null ? L("Sin sesión", "No session") : L("Proyección no disponible", "Projection unavailable"), ThemeRole.Muted));
            return rows;
        }
        if (snapshot.Capacity is { } capacity)
        {
            rows.Add(new(L("Capacidad · activos ", "Capacity · active ") + capacity.Active + "/" + capacity.Maximum
                + L(" · espera ", " · waiting ") + capacity.Waiting
                + (capacity.WriterActive ? L(" · escritora activa", " · writer active") : L(" · lectura", " · readers only")),
                capacity.Waiting > 0 ? ThemeRole.Attention : ThemeRole.Info));
            if (details && capacity.WaitingDelegationIds.Count > 0)
                rows.Add(new(L("Delegaciones en espera: ", "Waiting delegations: ")
                    + string.Join(", ", capacity.WaitingDelegationIds), ThemeRole.Muted));
        }
        foreach (var group in snapshot.FanOutGroups ?? Array.Empty<FanOutGroupSnapshot>())
        {
            rows.Add(new(L("Fan-out · ", "Fan-out · ") + group.Policy + " · " + group.State
                + " · " + group.DelegationIds.Count + L(" lanes", " lanes"),
                group.State == "Resolved" ? ThemeRole.Success : ThemeRole.Attention));
            if (details)
            {
                rows.Add(new("Fan-out " + group.GroupId + " · owner " + group.OwnerExecutionId, ThemeRole.Muted));
                rows.Add(new(L("Delegaciones · ", "Delegations · ") + string.Join(", ", group.DelegationIds), ThemeRole.Muted));
                if (group.AggregateId is { } aggregate) rows.Add(new("Aggregate " + aggregate, ThemeRole.Info));
            }
        }
        foreach (var lane in snapshot.Lanes)
        {
            var state = lane.LaneState switch {
                "Queued" => L("en cola · sin worker", "queued · no worker"),
                "Running" => lane.DelegationState == "Returned" ? L("resultado por evaluar", "result awaiting evaluation")
                    : L("lane activa", "active lane"), "Blocked" => L("bloqueada", "blocked"),
                "Completed" => L("completada", "completed"), "Cancelled" => L("cancelada", "cancelled"),
                "Failed" => L("falló", "failed"), _ => lane.LaneState,
            };
            var role = lane.LaneState is "Failed" ? ThemeRole.Error : lane.LaneState is "Queued" or "Blocked" ? ThemeRole.Attention : ThemeRole.Agent;
            rows.Add(new((lane.ParentTaskId is null ? L("Principal", "Root") : L("  Delegado", "  Delegate")) + " · " + state, role));
            rows.Add(new(lane.Objective, ThemeRole.Muted));
            if (lane.Model is { } model) rows.Add(new(model, ThemeRole.Info));
            if (lane.ExecutionAmbiguous) rows.Add(new(L("Identidad de ejecución ambigua", "Ambiguous execution identity"), ThemeRole.Error));
            if (lane.CancellationRequested && lane.DelegationState == "Accepted")
                rows.Add(new(L("Cancelación solicitada · checkpoint pendiente", "Cancellation requested · checkpoint pending"), ThemeRole.Attention));
            if (lane.ResultId is { } result) rows.Add(new("Result " + result + " · " + (lane.ResultDisposition ?? L("sin aceptar", "not accepted")), ThemeRole.Attention));
            if (lane.ResultOutcome is { } resultOutcome)
                rows.Add(new(L("Resultado · ", "Outcome · ") + resultOutcome,
                    resultOutcome == "Succeeded" ? ThemeRole.Info : ThemeRole.Error));
            if (lane.ResultIssueCount is { } issueCount && issueCount > 0)
                rows.Add(new(L("Problemas pendientes · ", "Remaining issues · ") + issueCount, ThemeRole.Error));
            if (lane.WorkflowId is { } workflowId)
                rows.Add(new("Workflow · " + workflowId + " · " + lane.WorkflowStage
                    + " · " + (lane.WorkflowGateSatisfied == true ? L("gate cumplido", "gate passed")
                        : lane.WorkflowGateSatisfied == false ? L("gate pendiente", "gate pending") : L("gate sin evaluar", "gate unevaluated")),
                    lane.WorkflowGateSatisfied == true ? ThemeRole.Success : ThemeRole.Attention));
            if (lane.WorkflowGateReason is { } workflowReason)
                rows.Add(new(L("Workflow gate · ", "Workflow gate · ") + workflowReason, ThemeRole.Muted));
            if (lane.PendingJoinIds is { Count: > 0 } joins) rows.Add(new("Join · " + string.Join(", ", joins), ThemeRole.Attention));
            if (!details) continue;
            rows.Add(new("Lane " + lane.LaneId, ThemeRole.Muted));
            rows.Add(new("Task " + lane.TaskId + " · " + lane.TaskState, ThemeRole.Muted));
            rows.Add(new("Profile " + lane.ProfileId + (lane.ProfileRevision is { } revision ? " · rev." + revision : " · legacy"), ThemeRole.Muted));
            if (lane.ExecutionId is { } execution) rows.Add(new("Execution " + execution + " · " + lane.ExecutionState, ThemeRole.Muted));
            if (lane.ParentExecutionId is { } parent) rows.Add(new("Parent " + parent, ThemeRole.Muted));
            if (lane.DelegationId is { } delegation) rows.Add(new("Delegation " + delegation + " · " + lane.DelegationState, ThemeRole.Muted));
            if (lane.Budget is { } budget)
                rows.Add(new(L("Presupuesto · ", "Budget · ")
                    + (budget.MaxTokens is { } maxTokens ? $"tokens {budget.TokensUsed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}/{maxTokens}" : L("tokens sin tope", "tokens unbounded"))
                    + (budget.MaxTurns is { } maxTurns ? $" · turns {budget.TurnsUsed}/{maxTurns}" : "")
                    + (budget.MaxToolCalls is { } maxTools ? $" · tools {budget.ToolCallsUsed}/{maxTools}" : "")
                    + (budget.MaxCostUsd is { } maxCost ? $" · USD {(budget.CostUsedUsd?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "? ")}/{maxCost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" : ""), ThemeRole.Muted));
            if (lane.SupervisionState is { } supervision)
                rows.Add(new(L("Supervisión · ", "Supervision · ") + supervision,
                    supervision.Contains("Failed", StringComparison.Ordinal) || supervision.Contains("Terminal", StringComparison.Ordinal)
                        ? ThemeRole.Error : ThemeRole.Muted));
            if (lane.ParentTaskId is not null)
                rows.Add(new(L("Integración · ", "Integration · ")
                    + (lane.IntegrationVerified ? L("verificada", "verified") : L("pendiente", "pending"))
                    + " · " + (lane.IntegrationValidationPassed ? L("validación pasada", "validation passed") : L("validación pendiente", "validation pending"))
                    + " · " + L("recibos tool-backed ", "tool-backed receipts ") + lane.ToolBackedEvidenceCount,
                    lane.IntegrationVerified && lane.IntegrationValidationPassed && lane.ToolBackedEvidenceCount > 0
                        ? ThemeRole.Success : ThemeRole.Attention));
            if (lane.PendingMailboxMessages > 0 || lane.PendingWakeRequests > 0)
                rows.Add(new($"Mailbox {lane.PendingMailboxMessages} · wake {lane.PendingWakeRequests}", ThemeRole.Attention));
            if (lane.ResultSummary is { } summary) rows.Add(new(summary, ThemeRole.Muted));
            if (lane.LastContextEventId is { } source) rows.Add(new("Context source " + source, ThemeRole.Muted));
            if (lane.SelectableContextItemIds is { Count: > 0 } items)
                rows.Add(new(L("Selección disponible: ", "Available selection: ") + string.Join(", ", items), ThemeRole.Muted));
        }
        if (rows.Count == 0) rows.Add(new(L("Sin lanes en este Run", "No lanes in this Run"), ThemeRole.Muted));
        return rows;
    }
}

public static class DelegationCommands
{
    public static WireEnvelope Create(DelegationCreateRequest request, string commandId)
        => WireEnvelope.Command(commandId, "{\"cmd\":\"delegation.create\",\"request\":" + AgentsJson.EncodeRequest(request) + "}");
}
