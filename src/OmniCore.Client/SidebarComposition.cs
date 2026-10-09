using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed record SidebarComposition(IReadOnlyList<(ISidebarWidget Widget, WidgetModel Model)> Pinned,
    IReadOnlyList<(ISidebarWidget Widget, WidgetModel Model)> Body,
    IReadOnlyList<ISidebarWidget> Navigation, string? SelectedId, int Collapsed,
    IReadOnlyList<string> AttentionIds)
{
    public static SidebarComposition Build(IEnumerable<ISidebarWidget> widgets, ClientState state,
        SidebarPreferences preferences, TuiLayoutMode mode, int height, string? selectedId, bool expandAll = false)
    {
        WidgetPreferences Setting(ISidebarWidget widget) => preferences.Widgets?.GetValueOrDefault(widget.Id) ?? new();
        var all = widgets.Select((widget, index) => (widget, index, relevance: widget.Evaluate(state))).ToArray();
        var navigation = all.Where(i => i.widget.Id != "core.session")
            .OrderByDescending(i => i.relevance == WidgetRelevance.Attention)
            .ThenByDescending(i => Setting(i.widget).Priority ?? i.widget.DefaultPriority).ThenBy(i => i.index).Select(i => i.widget).ToArray();
        var visible = all.Where(i => i.widget.Id == "core.session" || Setting(i.widget).Visible == "true"
            || Setting(i.widget).Visible == "auto" && i.relevance >= WidgetRelevance.Low).ToArray();
        selectedId = navigation.Any(w => w.Id == selectedId) ? selectedId
            : navigation.FirstOrDefault(w => visible.Any(i => i.widget.Id == w.Id))?.Id;
        var pinned = visible.Where(i => i.widget.Id == "core.session")
            .Select(i => (i.widget, i.widget.Build(state, WidgetSize.Compact))).ToArray();
        var candidates = mode == TuiLayoutMode.Tabbed
            ? all.Where(i => i.widget.Id == selectedId).ToArray()
            : all.Where(i => i.widget.Id != "core.session" && (visible.Contains(i) || i.widget.Id == selectedId))
                .OrderByDescending(i => i.relevance == WidgetRelevance.Attention)
                .ThenByDescending(i => Setting(i.widget).Priority ?? i.widget.DefaultPriority).ThenBy(i => i.index).ToArray();
        var body = new List<(ISidebarWidget Widget, WidgetModel Model)>(); var collapsed = 0;
        // Explicit expansion is inspectable via scroll. Automatic layout collapses low-priority detail first.
        foreach (var item in candidates)
        {
            var expanded = expandAll || Setting(item.widget).Expanded;
            var model = item.widget.Build(state, expanded ? WidgetSize.Expanded : WidgetSize.Normal);
            body.Add((item.widget, model));
        }
        var total = body.Sum(b => b.Model is ListWidgetModel list ? list.Rows.Count + 2 : 2);
        for (var index = body.Count - 1; index >= 0 && mode != TuiLayoutMode.Tabbed && total > height; index--)
        {
            var item = body[index];
            if (expandAll || Setting(item.Widget).Expanded) continue;
            if (item.Model is ListWidgetModel list && list.Rows.Count > 1)
            {
                var limit = Math.Max(Math.Max(1, list.Rows.Count(r => r.Role is ThemeRole.Attention or ThemeRole.Error)), list.Rows.Count - (total - height));
                var keep = list.Rows.Where(r => r.Role is ThemeRole.Attention or ThemeRole.Error).Take(limit).ToList();
                foreach (var row in list.Rows) { if (keep.Count >= limit) break; if (!keep.Contains(row)) keep.Add(row); }
                keep = list.Rows.Where(keep.Contains).ToList();
                body[index] = (item.Widget, list with { Title = list.Title + " · +" + (list.Rows.Count - keep.Count), Rows = keep });
                total -= list.Rows.Count - keep.Count;
                collapsed++;
            }
        }
        return new(pinned, body, navigation, selectedId, collapsed,
            all.Where(i => i.relevance == WidgetRelevance.Attention).Select(i => i.widget.Id).ToArray());
    }
}
