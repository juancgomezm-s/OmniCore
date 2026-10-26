using System.Globalization;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Read-only catalog presentation. Prices are USD per million tokens, input/output.</summary>
internal sealed class OpenRouterCatalogGrid(IReadOnlyList<OpenRouterModel> models, Localization text,
    Func<OpenRouterModel, bool>? isVisible = null) : ITableSource
{
    public int Rows => models.Count;
    public int Columns => 5;
    public string[] ColumnNames => ["F3", text.Resolve("openrouter.catalog.company"), text.Resolve("openrouter.catalog.model"),
        text.Resolve("openrouter.catalog.context"), text.Resolve("openrouter.catalog.cost")];

    public object this[int row, int column] => column switch
    {
        0 => !CanShowInPicker(models[row]) ? "—" : (isVisible?.Invoke(models[row]) ?? true) ? "[✓]" : "[ ]",
        1 => Company(models[row]),
        2 => Safe(models[row].Id.Split('/', 2).Last()),
        3 => models[row].ContextLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "—",
        4 => Cost(models[row]),
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    internal static string Company(OpenRouterModel model)
    {
        var company = model.Id.Split('/', 2)[0];
        var colon = model.Name?.IndexOf(':') ?? -1;
        return Safe(colon > 0 ? model.Name![..colon].Trim() : company);
    }

    internal static string Safe(string value) => new(value.Where(c => !char.IsControl(c)).Take(160).ToArray());
    internal static bool CanShowInPicker(OpenRouterModel model) => model.OutputModalities.Count == 0
        || model.OutputModalities.Contains("text", StringComparer.Ordinal);
    internal static string Cost(OpenRouterModel model) => PerMillion(model.PromptPricePerToken) + "/"
        + PerMillion(model.CompletionPricePerToken) + " USD";

    internal static string PerMillion(string? value)
    {
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < 0)
            return "—";
        try { return (price * 1_000_000m).ToString("0.00##########################", CultureInfo.InvariantCulture); }
        catch (OverflowException) { return "—"; }
    }

    // Sort the complete bounded cache before paging so companies stay together across pages.
    internal static OpenRouterCatalogPage Browse(ITuiOpenRouterCatalogHost host, string providerId, string? query, int offset)
    {
        var first = host.BrowseOpenRouterCatalog(providerId, query, 0, 100, CancellationToken.None);
        var all = first.Models.ToList();
        var next = first.NextOffset;
        while (next is { } start && start >= all.Count && all.Count < 1000)
        {
            var page = host.BrowseOpenRouterCatalog(providerId, query, start, 100, CancellationToken.None);
            all.AddRange(page.Models);
            if (page.Models.Count == 0 || page.NextOffset <= start) break;
            next = page.NextOffset;
        }
        var ordered = all.Take(1000).OrderBy(Company, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = ordered.Skip(offset).Take(30).ToArray();
        return first with { Models = rows, NextOffset = offset + rows.Length < ordered.Length ? offset + rows.Length : null };
    }
}
