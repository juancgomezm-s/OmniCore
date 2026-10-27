using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Text;

namespace OmniCore.Cli;

internal enum ModelPickerRowKind { PriceGroup, Provider, Manufacturer, Model }

internal sealed record ModelPickerListRow(ModelPickerRowKind Kind, string Text,
    ModelRegistryModelDescriptor? Model = null, bool IsFree = false)
{
    public override string ToString() => Text;
}

/// <summary>Grouped list, not a table/browser. Fixed trailing columns use terminal cell widths.</summary>
internal static class ModelPickerList
{
    internal sealed record Entry(ModelRegistryModelDescriptor Model, string Provider, string Manufacturer, string Name,
        long? Context, string? InputPrice, string? OutputPrice)
    {
        private static string Price(string? value)
        {
            var result = OpenRouterCatalogGrid.PerMillion(value);
            return result.EndsWith(".00", StringComparison.Ordinal) ? result[..^3] : result;
        }
        internal string Cost => InputPrice is null && OutputPrice is null ? "—" : Price(InputPrice) + "/" + Price(OutputPrice);
        internal bool Free => OpenRouterCatalogGrid.PerMillion(InputPrice) == "0.00"
            && OpenRouterCatalogGrid.PerMillion(OutputPrice) == "0.00";
    }

    internal static string Manufacturer(string id, string? name, string fallback)
    {
        var colon = name?.IndexOf(':') ?? -1;
        var company = colon > 0 ? name![..colon].Trim() : id.Contains('/') ? id.Split('/', 2)[0] : fallback;
        return OpenRouterCatalogGrid.Safe(company);
    }

    internal static Entry[] Order(IEnumerable<Entry> entries) => entries
        .OrderByDescending(entry => entry.Free)
        .ThenBy(entry => entry.Provider, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Manufacturer, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Model.Id, StringComparer.Ordinal).ToArray();

    internal static ModelPickerListRow[] Rows(Entry[] entries, int width, string? selected, Localization text)
    {
        var rows = new List<ModelPickerListRow>();
        string? provider = null, manufacturer = null;
        bool? free = null;
        var columns = Widths(width, entries);
        foreach (var entry in entries)
        {
            if (free != entry.Free)
            {
                rows.Add(new(ModelPickerRowKind.PriceGroup, text.Resolve(entry.Free ? "modelPicker.free" : "modelPicker.more"), IsFree: entry.Free));
                free = entry.Free; provider = null; manufacturer = null;
            }
            if (provider != entry.Provider)
            {
                rows.Add(new(ModelPickerRowKind.Provider, "  " + Fit(entry.Provider, Math.Max(1, width - 2))));
                provider = entry.Provider; manufacturer = null;
            }
            if (manufacturer != entry.Manufacturer)
            {
                rows.Add(new(ModelPickerRowKind.Manufacturer, "    " + Fit(entry.Manufacturer, Math.Max(1, width - 4))));
                manufacturer = entry.Manufacturer;
            }
            var name = entry.Name + (entry.Model.Id == selected ? " ✓" : "");
            rows.Add(new(ModelPickerRowKind.Model, "      " + Pad(name, columns.Name) + "  "
                + Pad(OpenRouterCatalogGrid.CompactContext(entry.Context), columns.Context, right: true) + "  "
                + Pad(entry.Cost, columns.Cost, right: true), entry.Model, entry.Free));
        }
        return rows.ToArray();
    }

    internal static string Heading(Entry[] entries, int width, Localization text)
    {
        var columns = Widths(width, entries);
        return "      " + Pad(text.Resolve("openrouter.catalog.model"), columns.Name) + "  "
            + Pad(text.Resolve("openrouter.catalog.context"), columns.Context, right: true) + "  "
            + Pad(text.Resolve("openrouter.catalog.cost"), columns.Cost, right: true);
    }

    private static (int Name, int Context, int Cost) Widths(int width, Entry[] entries)
    {
        var cost = Math.Clamp(entries.Length == 0 ? 10 : entries.Max(entry => entry.Cost.Length), 10, 17);
        return (Math.Max(1, width - 6 - 4 - 6 - cost), 6, cost);
    }

    private static string Pad(string value, int width, bool right = false)
    {
        var result = Fit(value, width);
        var spaces = new string(' ', Math.Max(0, width - result.GetColumns(false)));
        return right ? spaces + result : result + spaces;
    }

    private static string Fit(string value, int width)
    {
        value = OpenRouterCatalogGrid.Safe(value);
        if (value.GetColumns(false) <= width) return value;
        var result = new System.Text.StringBuilder(); var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var cells = rune.ToString().GetColumns(false);
            if (length + cells > width - 1) break;
            result.Append(rune.ToString()); length += cells;
        }
        return result + "…";
    }
}
