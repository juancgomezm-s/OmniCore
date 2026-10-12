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
        var text = new Localization(locale);
        var widgets = new List<ISidebarWidget>();
        var sessionRows = new List<WidgetRowModel>
        {
            new(string.IsNullOrWhiteSpace(canonical?.Title) ? text.Resolve("sidebar.new_session") : canonical.Title),
            new(text.Resolve("sidebar.model") + model, ThemeRole.Agent),
            new(StatusLinePresentation.ModeLabel(canonical?.Mode ?? mode, locale)
                + (canonical?.RunState is { } state ? " · " + RunState(state, locale) : ""), ThemeRole.Muted),
        };
        if (canonical?.Activity is { } activity)
            sessionRows.Add(new(Activity(activity, locale), ActivityRole(activity)));
        widgets.Add(new DataSidebarWidget("core.session", text.Resolve("sidebar.session_title"), 100, sessionRows));
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
            new(context is null ? text.Resolve("sidebar.no_measurement_yet")
                : text.Resolve("sidebar.tokens") + Count(context.Tokens, locale), ThemeRole.Info),
        };
        if (context is not null)
        {
            contextRows.Add(new(text.Resolve("sidebar.capacity") + Count(context.Capacity, locale), ThemeRole.Muted));
            contextRows.Add(new(text.Resolve("sidebar.budget_label") + Count(context.UsableBudget, locale), ThemeRole.Muted));
            contextRows.Add(new(text.Resolve("sidebar.used") + Percent(context.UsedPercent, locale), ThemeRole.Info));
            if (details)
            {
                contextRows.Add(new(text.Resolve("sidebar.request") + context.ModelId, ThemeRole.Muted));
                foreach (var part in context.Components) contextRows.Add(new(part.Kind + ": " + Count(part.Tokens, locale), ThemeRole.Muted));
                contextRows.Add(new(text.Resolve("sidebar.reasoning") + Count(context.ReportedReasoningTokens, locale), ThemeRole.Muted));
                contextRows.Add(new(context.AsOf.ToUniversalTime().ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ThemeRole.Muted));
            }
        }
        if (observation?.Updating == true) contextRows.Add(new(text.Resolve("sidebar.updating_request"), ThemeRole.Active));
        widgets.Add(new DataSidebarWidget("core.context", text.Resolve("sidebar.context"), 65, contextRows));
        var usage = observation?.Consumption;
        var usageRows = new List<WidgetRowModel>
        {
            new(text.Resolve("sidebar.session_tokens") + (usage is null ? "—" : Count(usage.Total, locale)), ThemeRole.Info),
            new(text.Resolve("sidebar.session_cost") + (usage is null ? "—" : Cost(usage.Cost, locale)), ThemeRole.Muted),
        };
        if (details && usage is not null)
        {
            usageRows.Add(new(text.Resolve("sidebar.invocations") + usage.ModelInvocations, ThemeRole.Muted));
            if (usage.IncompleteInvocations > 0) usageRows.Add(new(text.Resolve("sidebar.incomplete_usage") + usage.IncompleteInvocations, ThemeRole.Attention));
            if (usage.Breakdown is { } breakdown)
            {
                usageRows.Add(new(text.Resolve("sidebar.input") + Count(breakdown.Input, locale), ThemeRole.Muted));
                usageRows.Add(new(text.Resolve("sidebar.output") + Count(breakdown.Output, locale), ThemeRole.Muted));
                usageRows.Add(new(text.Resolve("sidebar.cache_read") + Count(breakdown.CacheRead, locale), ThemeRole.Muted));
                usageRows.Add(new(text.Resolve("sidebar.cache_write") + Count(breakdown.CacheWrite, locale), ThemeRole.Muted));
            }
        }
        widgets.Add(new DataSidebarWidget("core.consumption", text.Resolve("sidebar.usage"), 60, usageRows));
        var diagnostics = new List<WidgetRowModel>();
        if (canonical is null) diagnostics.Add(new(text.Resolve("sidebar.no_active_session"), ThemeRole.Muted));
        else
        {
            diagnostics.Add(new(canonical.RecoveryBlocked ? text.Resolve("sidebar.recovery_blocked")
                : text.Resolve("sidebar.no_recovery_block"), canonical.RecoveryBlocked ? ThemeRole.Error : ThemeRole.Muted));
            if (canonical.ProjectionUnavailable) diagnostics.Add(new(text.Resolve("sidebar.plan_unavailable"), ThemeRole.Error));
            if (details)
            {
                if (canonical.RunId is { } run) diagnostics.Add(new("Run " + run, ThemeRole.Muted));
                diagnostics.Add(new(text.Resolve("sidebar.model") + model, ThemeRole.Agent));
                diagnostics.Add(new("Journal · seq " + canonical.BasedOnJournalSequence, ThemeRole.Muted));
            }
        }
        widgets.Add(new DataSidebarWidget("core.diagnostics", text.Resolve("sidebar.diagnostics"), 40, diagnostics));
        return widgets;
    }

    private static CultureInfo Culture(string locale) => CultureInfo.GetCultureInfo(locale == "es" ? "es-MX" : locale);
    private static string Mark(MetricAvailability availability, string locale) => availability switch
    {
        MetricAvailability.Estimated => "≈", MetricAvailability.Stale => new Localization(locale).Resolve("sidebar.stale"), _ => "",
    };
    private static bool HasValue(MetricAvailability availability) => availability is MetricAvailability.Reported or MetricAvailability.Estimated or MetricAvailability.Stale;
    public static string Count(Metric<long?> metric, string locale = "es") => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability, locale) + value.ToString("N0", Culture(locale)) : "—";
    private static string Percent(Metric<double?> metric, string locale) => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability, locale) + value.ToString("0.0", Culture(locale)) + "%" : "—";
    private static string Cost(Metric<Money> metric, string locale) => HasValue(metric.Availability) && metric.Value is { } value
        ? Mark(metric.Availability, locale) + value.Amount.ToString("0.0000", Culture(locale)) + " " + value.Currency : "—";
    private static ThemeRole PlanRole(string state) => state switch
    {
        "Completed" => ThemeRole.Success, "InProgress" => ThemeRole.Active, "Blocked" => ThemeRole.Attention,
        "Failed" => ThemeRole.Error, _ => ThemeRole.Muted,
    };
    private static string PlanState(string state, string locale) => StateLabel(state, locale);
    private static string Activity(string activity, string locale) => StateLabel(activity, locale);
    private static string StateLabel(string state, string locale) => ShellUiResources.ContainsKey("state." + state)
        ? new Localization(locale).Resolve("state." + state) : state;
    private static ThemeRole ActivityRole(string activity) => activity switch
    {
        "WaitingForPermission" or "WaitingForInput" or "Stalled" => ThemeRole.Attention,
        _ => ThemeRole.Active,
    };
    private static string RunState(string state, string locale) => StateLabel(state, locale);
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
