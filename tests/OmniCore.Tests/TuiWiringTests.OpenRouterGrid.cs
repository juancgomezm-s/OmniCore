using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static OpenRouterModel GridModel(string id, string name, string? input = "0.0000001", string? output = "0.00001") =>
        new(id, name, null, 1_048_576, 32768, input, output, ["text"], ["text"], [], "{}");

    [Theory]
    [InlineData("0.0000001", "0.10")]
    [InlineData("0.00001", "10.00")]
    [InlineData("1e-7", "0.10")]
    [InlineData("0", "0.00")]
    [InlineData("0.000000000001", "0.000001")]
    [InlineData(null, "—")]
    [InlineData("unknown", "—")]
    [InlineData("-1", "—")]
    [InlineData("79228162514264337593543950335", "—")]
    public void OpenRouter_grid_prices_are_per_million_and_unknown_never_becomes_free(string? raw, string expected) =>
        Assert.Equal(expected, OpenRouterCatalogGrid.PerMillion(raw));

    [Fact]
    public void OpenRouter_grid_groups_the_whole_catalog_before_paging_and_keeps_exact_model_identity()
    {
        var providers = new ProviderConnectionsFixture { CatalogModels = Enumerable.Range(0, 110)
            .Select(index => GridModel("zeta/model-" + index.ToString("D3"), "Zeta: Model"))
            .Append(GridModel("openai/first", "OpenAI: First")).ToArray() };
        var page = OpenRouterCatalogGrid.Browse(providers, "openrouter", null, 0);
        Assert.Equal("openai/first", page.Models[0].Id);
        Assert.Equal(111, page.TotalCount);
        Assert.Equal(30, page.NextOffset);
        Assert.Equal(2, providers.CatalogBrowseCalls);
        var next = OpenRouterCatalogGrid.Browse(providers, "openrouter", null, 30);
        Assert.Empty(page.Models.Select(model => model.Id).Intersect(next.Models.Select(model => model.Id)));
        Assert.Empty(providers.RegisteredModels);
        Assert.Equal(0, providers.CatalogRefreshCalls);
    }

    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 80, 25)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 80, 25)]
    [InlineData("es", 120, 40)]
    [InlineData("en", 120, 40)]
    [InlineData("fr", 120, 40)]
    [InlineData("it", 120, 40)]
    public void OpenRouter_grid_has_localized_headers_visible_cost_and_registers_the_sorted_selection(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var providers = new ProviderConnectionsFixture
        {
            Rows = [new("openrouter", ProviderConnectionMethod.ApiKey, ProviderConnectionState.Connected,
                OmniCore.Domain.BillingMode.MeteredCurrency, null, null, false, true, true, true, null, null) { Backend = "openrouter" }],
            CatalogModels = [GridModel("openai/long-model-name-for-truncation", "OpenAI: Example"),
                GridModel("anthropic/example", "Anthropic: Example", "0", null)],
        };
        fx.StartTui(providerConnections: providers, initialColumns: width, initialRows: height,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-openrouter-open");
        ClickId(fx, "provider-discover");
        fx.Wait(() => fx.App.Overlay?.Id == "openrouter-catalog", "catalog grid");
        var table = Descendants(fx.App.Overlay!).OfType<TableView>().Single();
        Assert.Equal("Anthropic", table.Table![0, 0]);
        Assert.Equal("OpenAI", table.Table[1, 0]);
        Assert.Equal("0.10/10.00 USD", table.Table[1, 3]);
        Assert.Equal("0.00/— USD", table.Table[0, 3]);
        Assert.Equal(new Localization(locale).Resolve("openrouter.catalog.cost"), table.Table.ColumnNames[3]);
        Assert.DoesNotContain("openrouter.catalog.title", OverlayText(fx.App.Overlay!));
        fx.Invoke(() => { table.SetSelection(0, 1, false); table.SetFocus(); });
        ExportPreferenceFrame(fx, width, height, "openrouter-grid-" + locale, "0.10/10.00 USD");
        Assert.Empty(providers.RegisteredModels);
        Assert.Equal(0, providers.CatalogRefreshCalls);
        ClickId(fx, "openrouter-catalog-register");
        fx.Wait(() => providers.RegisteredModels.Count > 0, "register sorted selection");
        Assert.Equal(["openai/long-model-name-for-truncation"], providers.RegisteredModels);
    }, noColor: false);
}
