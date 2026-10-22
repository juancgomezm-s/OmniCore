using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Client-owned view cache. A gap must be repaired with a fresh snapshot.</summary>
public sealed class ExtensionViewProjection
{
    public static ISidebarWidget IndicatorWidget(ExtensionView view, Localization localization) => new DataSidebarWidget(
        view.Id, view.ExtensionId, 0, (view.Indicators ?? []).Select(indicator => new WidgetRowModel(
            Label(indicator.Label, localization) + "  "
                + (indicator.Value?.ToString(PreferenceInput.Culture(localization.Locale)) ?? "—")
                + (indicator.Unit is null ? "" : " " + Label(indicator.Unit, localization))
                + " · " + localization.Resolve("extension.indicator." + indicator.State),
            indicator.State switch { "error" => ThemeRole.Error, "warning" => ThemeRole.Attention,
                "active" => ThemeRole.Active, "ready" => ThemeRole.Success, _ => ThemeRole.Muted })).ToArray());

    private static string Label(string text, Localization localization) => SessionPreferencesPresentation.SafeText(
        text.StartsWith("extension.", StringComparison.Ordinal) ? localization.Resolve(text) : text);

    public static string Describe(ExtensionViewItem? item, ExtensionView view, Localization localization)
    {
        if (item is null) return "";
        var lines = new List<string>();
        if (item.Detail is { } detail) lines.Add(Label(detail, localization));
        if (item.Cells is { } cells && view.Columns is { } columns)
            foreach (var column in columns)
                lines.Add(Label(column.Label, localization) + ": " + Label(cells.TryGetValue(column.Id, out var value)
                    && value.ValueKind != System.Text.Json.JsonValueKind.Null ? value.ToString() : "—", localization));
        else if (item.Data is { } data) lines.Add(SessionPreferencesPresentation.SafeText(data.ToString()));
        return string.Join("\n", lines);
    }

    /// <summary>Adapter to the existing ADR-0032 widget presentation model, not another UI registry.</summary>
    public static ListWidgetModel Present(ExtensionView view, Localization localization) => new(view.ExtensionId,
        view.Items.Select(item => new WidgetRowModel(
            (item.HasChildren ? "▸ " : "  ") + SessionPreferencesPresentation.SafeText(
                item.Label.StartsWith("extension.", StringComparison.Ordinal) ? localization.Resolve(item.Label) : item.Label),
            item.HasChildren ? ThemeRole.Info : ThemeRole.Primary, "extension.view.select", item.Id)).ToArray());
    private readonly Dictionary<string, ExtensionView> _views = new(StringComparer.Ordinal);
    public ExtensionView? Get(string id) => _views.TryGetValue(id, out var view) ? view : null;
    public bool Apply(ExtensionView update)
    {
        if (update.Items.Count > ExtensionProtocol.MaximumItems || update.Revision < 0
            || update.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != update.Items.Count)
            throw new ArgumentException("extension_view_invalid", nameof(update));
        var current = Get(update.Id);
        if (!update.Reset && current is not null && update.Revision == current.Revision)
            return update.Items.Count == 0 && update.RemovedIds is not { Count: > 0 }
                && (update.ItemOrder is null || update.ItemOrder.SequenceEqual(current.Items.Select(item => item.Id), StringComparer.Ordinal));
        if (!update.Reset && (current is null || update.Revision != current.Revision + 1)) return false;
        var merged = update.Reset ? update : update with
        {
            Items = current!.Items.Where(item => update.RemovedIds?.Contains(item.Id, StringComparer.Ordinal) != true
                && !update.Items.Any(next => next.Id == item.Id)).Concat(update.Items).ToArray(),
        };
        if (merged.Items.Count > ExtensionProtocol.MaximumItems) return false;
        if (update.ItemOrder is { } order)
        {
            var items = merged.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            if (order.Count != items.Count || order.Any(id => !items.ContainsKey(id))
                || order.Distinct(StringComparer.Ordinal).Count() != order.Count) return false;
            merged = merged with { Items = order.Select(id => items[id]).ToArray() };
        }
        _views[update.Id] = merged;
        return true;
    }
}
