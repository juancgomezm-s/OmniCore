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
            if (lane.PendingJoinIds is { Count: > 0 } joins) rows.Add(new("Join · " + string.Join(", ", joins), ThemeRole.Attention));
            if (!details) continue;
            rows.Add(new("Lane " + lane.LaneId, ThemeRole.Muted));
            rows.Add(new("Task " + lane.TaskId + " · " + lane.TaskState, ThemeRole.Muted));
            rows.Add(new("Profile " + lane.ProfileId + (lane.ProfileRevision is { } revision ? " · rev." + revision : " · legacy"), ThemeRole.Muted));
            if (lane.ExecutionId is { } execution) rows.Add(new("Execution " + execution + " · " + lane.ExecutionState, ThemeRole.Muted));
            if (lane.ParentExecutionId is { } parent) rows.Add(new("Parent " + parent, ThemeRole.Muted));
            if (lane.DelegationId is { } delegation) rows.Add(new("Delegation " + delegation + " · " + lane.DelegationState, ThemeRole.Muted));
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
