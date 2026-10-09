using System.Globalization;
using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Session-scoped canonical sidebar state, separate from non-canonical observability.</summary>
public sealed class SessionSidebarProjection
{
    public string? SessionId { get; private set; }
    public SessionSidebarSnapshot? Snapshot { get; private set; }
    public void Activate(string? sessionId)
    {
        if (SessionId == sessionId) return;
        SessionId = sessionId; Snapshot = null;
    }
    public bool Apply(SessionSidebarSnapshot snapshot)
    {
        if (snapshot.SessionId != SessionId || snapshot.BasedOnJournalSequence < (Snapshot?.BasedOnJournalSequence ?? 0)
            || snapshot.Plan is { } plan && plan.RunId != snapshot.RunId) return false;
        Snapshot = snapshot;
        return true;
    }
    public bool Poll(IOmniClient client, CancellationToken cancellationToken = default)
    {
        var query = client.Query("sessionSidebar", cancellationToken);
        return query is not null && SidebarJson.Decode(query.Json) is { } snapshot && Apply(snapshot);
    }
}

/// <summary>Pure declarative widgets. Unavailable measurements are not zero; estimates remain marked.</summary>
public static class SessionSidebarPresentation
{
    public static IReadOnlyList<ISidebarWidget> Build(SessionSidebarSnapshot? canonical,
        SessionObservabilitySnapshot? observation, string model, string mode, string locale, bool details)
    {
        string L(string es, string en) => locale == "en" ? en : es;
        var widgets = new List<ISidebarWidget>();
        var sessionRows = new List<WidgetRowModel>
        {
            new(string.IsNullOrWhiteSpace(canonical?.Title) ? L("Nueva sesión", "New session") : canonical.Title),
            new(L("Modelo: ", "Model: ") + model, ThemeRole.Agent),
            new((canonical?.Mode ?? mode) + (canonical?.RunState is { } state ? " · " + RunState(state, locale) : ""), ThemeRole.Muted),
        };
        widgets.Add(new DataSidebarWidget("core.session", "SESSION", 100, sessionRows));
        if (canonical?.Plan is { } plan && (plan.Items.Count > 1 || plan.Items.Any(i => i.State is "Blocked" or "Failed")))
        {
            var children = plan.Items.Where(i => i.ParentId is not null).Select(i => i.ParentId!).ToHashSet(StringComparer.Ordinal);
            var leaves = plan.Items.Where(i => !children.Contains(i.Id)).ToArray();
            var completed = leaves.Count(i => i.State is "Completed" or "Skipped" or "Cancelled");
            var rows = new List<WidgetRowModel>();
            var byParent = plan.Items.ToLookup(i => i.ParentId ?? "", StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(SidebarPlanItem item, int depth)
            {
                if (!visited.Add(item.Id)) return;
                rows.Add(new(new string(' ', Math.Min(depth, 4) * 2) + item.Description
                    + (details ? " · " + PlanState(item.State, locale) : ""), PlanRole(item.State)));
                foreach (var child in byParent[item.Id].OrderBy(i => i.Order)) Visit(child, depth + 1);
            }
            foreach (var item in byParent[""].OrderBy(i => i.Order)) Visit(item, 0);
            foreach (var item in plan.Items.OrderBy(i => i.Order)) Visit(item, 0);
            widgets.Add(new PlanSidebarWidget(new($"PLAN {completed}/{leaves.Length} · rev.{plan.Revision}", rows)));
        }
        // Reject observation ownership even when this pure formatter is used without the stateful projection.
        if (canonical is null || observation?.SessionId != canonical.SessionId) observation = null;
        var context = observation?.Context;
        var contextRows = new List<WidgetRowModel>
        {
            new(context is null ? L("Sin medición todavía", "No measurement yet")
                : L("Tokens: ", "Tokens: ") + Count(context.Tokens), ThemeRole.Info),
        };
        if (context is not null)
        {
            contextRows.Add(new(L("Capacidad: ", "Capacity: ") + Count(context.Capacity), ThemeRole.Muted));
            contextRows.Add(new(L("Presupuesto: ", "Budget: ") + Count(context.UsableBudget), ThemeRole.Muted));
            contextRows.Add(new(L("Ocupación: ", "Used: ") + Percent(context.UsedPercent), ThemeRole.Info));
            if (details)
            {
                contextRows.Add(new(L("Solicitud: ", "Request: ") + context.ModelId, ThemeRole.Muted));
                foreach (var part in context.Components) contextRows.Add(new(part.Kind + ": " + Count(part.Tokens), ThemeRole.Muted));
                contextRows.Add(new(L("Razonamiento: ", "Reasoning: ") + Count(context.ReportedReasoningTokens), ThemeRole.Muted));
                contextRows.Add(new(context.AsOf.ToUniversalTime().ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ThemeRole.Muted));
            }
        }
        if (observation?.Updating == true) contextRows.Add(new(L("Actualizando solicitud…", "Updating request…"), ThemeRole.Active));
        widgets.Add(new DataSidebarWidget("core.context", L("CONTEXTO", "CONTEXT"), 65, contextRows));
        var usage = observation?.Consumption;
        var usageRows = new List<WidgetRowModel>
        {
            new(L("Tokens sesión: ", "Session tokens: ") + (usage is null ? "—" : Count(usage.Total)), ThemeRole.Info),
            new(L("Costo sesión: ", "Session cost: ") + (usage is null ? "—" : Cost(usage.Cost)), ThemeRole.Muted),
        };
        if (details && usage is not null)
        {
            usageRows.Add(new(L("Invocaciones: ", "Invocations: ") + usage.ModelInvocations, ThemeRole.Muted));
            if (usage.IncompleteInvocations > 0) usageRows.Add(new(L("Sin uso completo: ", "Incomplete usage: ") + usage.IncompleteInvocations, ThemeRole.Attention));
            if (usage.Breakdown is { } breakdown)
            {
                usageRows.Add(new(L("Entrada: ", "Input: ") + Count(breakdown.Input), ThemeRole.Muted));
                usageRows.Add(new(L("Salida: ", "Output: ") + Count(breakdown.Output), ThemeRole.Muted));
                usageRows.Add(new("Cache read: " + Count(breakdown.CacheRead), ThemeRole.Muted));
                usageRows.Add(new("Cache write: " + Count(breakdown.CacheWrite), ThemeRole.Muted));
            }
        }
        widgets.Add(new DataSidebarWidget("core.consumption", L("CONSUMO", "USAGE"), 60, usageRows));
        var diagnostics = new List<WidgetRowModel>();
        if (canonical is null) diagnostics.Add(new(L("Sin sesión activa", "No active session"), ThemeRole.Muted));
        else
        {
            diagnostics.Add(new(canonical.RecoveryBlocked ? L("Recuperación bloqueada", "Recovery blocked")
                : L("Sin bloqueo de recuperación", "No recovery block"), canonical.RecoveryBlocked ? ThemeRole.Error : ThemeRole.Muted));
            if (canonical.ProjectionUnavailable) diagnostics.Add(new(L("Plan no disponible", "Plan unavailable"), ThemeRole.Error));
            if (details)
            {
                if (canonical.RunId is { } run) diagnostics.Add(new("Run " + run, ThemeRole.Muted));
                diagnostics.Add(new(L("Modelo: ", "Model: ") + model, ThemeRole.Agent));
                diagnostics.Add(new("Journal · seq " + canonical.BasedOnJournalSequence, ThemeRole.Muted));
            }
        }
        widgets.Add(new DataSidebarWidget("core.diagnostics", L("DIAGNÓSTICO", "DIAGNOSTICS"), 40, diagnostics));
        return widgets;
    }

    private static string Mark(MetricAvailability availability) => availability switch
    {
        MetricAvailability.Estimated => "≈", MetricAvailability.Stale => "anterior · ", _ => "",
    };
    private static bool HasValue(MetricAvailability availability) => availability is MetricAvailability.Reported or MetricAvailability.Estimated or MetricAvailability.Stale;
    public static string Count(Metric<long?> metric) => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability) + value.ToString("N0", CultureInfo.InvariantCulture) : "—";
    private static string Percent(Metric<double?> metric) => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability) + value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
    private static string Cost(Metric<Money> metric) => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability) + value.Amount.ToString("0.0000", CultureInfo.InvariantCulture) + " " + value.Currency : "—";
    private static ThemeRole PlanRole(string state) => state switch
    {
        "Completed" => ThemeRole.Success, "InProgress" => ThemeRole.Active, "Blocked" => ThemeRole.Attention,
        "Failed" => ThemeRole.Error, _ => ThemeRole.Muted,
    };
    private static string PlanState(string state, string locale) => locale == "en" ? state : state switch
    {
        "Pending" => "pendiente", "Ready" => "listo", "InProgress" => "en curso", "Blocked" => "bloqueado",
        "Completed" => "completado", "Failed" => "falló", "Skipped" => "omitido", "Cancelled" => "cancelado", _ => state,
    };
    private static string RunState(string state, string locale) => locale == "en" ? state : state switch
    {
        "Created" => "creado", "Running" => "en curso", "AwaitingInput" => "esperando respuesta", "Completed" => "completado",
        "Failed" => "falló", "Cancelled" => "cancelado", _ => state,
    };
}

internal sealed class DataSidebarWidget(string id, string title, int priority, IReadOnlyList<WidgetRowModel> rows) : ISidebarWidget
{
    public string Id => id;
    public string Title => title;
    public ThemeRole Accent => ThemeRole.Info;
    public int DefaultPriority => priority;
    public WidgetRelevance Evaluate(ClientState state) => rows.Any(r => r.Role is ThemeRole.Attention or ThemeRole.Error)
        ? WidgetRelevance.Attention : WidgetRelevance.Normal;
    public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(title, rows);
}
