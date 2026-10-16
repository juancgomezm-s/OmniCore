using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", "OpenRouter", "accounts_connections")]
    [InlineData("en", "OpenRouter", "accounts_connections")]
    [InlineData("fr", "OpenRouter", "accounts_connections")]
    [InlineData("it", "OpenRouter", "accounts_connections")]
    [InlineData("es", "habilidades", "extensions_skills")]
    [InlineData("es", "automatizaciones", "extensions_skills")]
    [InlineData("es", "alias", "extensions_skills")]
    [InlineData("es", "MCP", "extensions_skills")]
    public void Menu_search_finds_the_category_that_owns_the_requested_option(string locale, string query, string category)
    {
        var results = PreferencesPresentation.Search(PreferenceCategoryCatalog.Resolve(new Localization(locale)), query, locale);
        Assert.Contains(results, result => result.Id == category);
        if (query == "alias") Assert.DoesNotContain(results, result => result.Id == "models_execution");
    }

    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("es", 120, 40)]
    [InlineData("en", 80, 25)]
    [InlineData("en", 120, 40)]
    [InlineData("fr", 80, 25)]
    [InlineData("fr", 120, 40)]
    [InlineData("it", 80, 25)]
    [InlineData("it", 120, 40)]
    public void Every_preference_category_has_distinct_actions_and_returns_from_its_pages(string locale, int columns, int height) => RunTuiTest(fx =>
    {
        var providers = OpenRouterUsabilityFixture();
        fx.StartTui(providerConnections: providers, initialColumns: columns, initialRows: height,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.Composer!.Text = "keep my draft");
        var sequence = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        var pages = new Dictionary<string, (string Action, string Marker)[]>
        {
            ["general"] = [("preferences-sidebar-open", "sidebar-settings-back"), ("preferences-animations-open", "animation-back")],
            ["accounts_connections"] = [("preferences-account-open", "account-browser-login"), ("preferences-claude-open", "claude-account-list"),
                ("preferences-openrouter-open", "provider-api-key"), ("preferences-provider-open-anthropic", "provider-api-key")],
            ["local_engines"] = [("local-engine-add", "local-engine-id")],
            ["models_execution"] = [("preferences-model-select", "omni-model-list"), ("preferences-models-open", "policy-default-model"), ("preferences-model-catalog-open", "model-catalog-back")],
            ["tools_security"] = [("preferences-tools-open", "tools-list"), ("preferences-tool-mappings-open", "mapping-source"), ("preferences-permissions-open", "security-rules")],
            ["context_sessions"] = [("preferences-context-open", "session-preferences-list"), ("preferences-memory-open", "session-preferences-list")],
            ["extensions_skills"] = [("preferences-extensions-open", "session-preferences-list"), ("preferences-skills-open", "session-preferences-list"),
                ("preferences-hooks-open", "preferences-inspection"), ("preferences-mcp-open", "preferences-inspection"),
                ("preferences-aliases-open", "mapping-source"), ("preferences-commands-open", "commands-list")],
            ["diagnostics_data"] = [("preferences-diagnostics-open", "diagnostics-summary"), ("preferences-help-open", "preferences-inspection")],
        };
        Assert.Equal(PreferenceCategoryCatalog.All.Select(category => category.Id).Order(), pages.Keys.Order());
        foreach (var category in PreferenceCategoryCatalog.All)
        {
            ChoosePreferenceCategory(fx, category.Id, locale);
            var buttons = Descendants(fx.App.Overlay!).OfType<Button>()
                .Where(view => view.Id?.StartsWith("preferences-", StringComparison.Ordinal) == true && view.Id.EndsWith("-open", StringComparison.Ordinal)).ToArray();
            Assert.Equal(buttons.Length, buttons.Select(view => view.Id).Distinct().Count());
            Assert.Equal(buttons.Length, buttons.Select(view => view.Text.ToString()).Distinct().Count());
            Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "preferences-api-open");
            foreach (var page in pages[category.Id])
            {
                var center = fx.App.Overlay;
                // Focus scrolls the actual menu action into view before routing a screen-coordinate click.
                fx.Invoke(() => Descendants(fx.App.Overlay!).Single(view => view.Id == page.Action).SetFocus());
                ClickThroughApplication(fx, page.Action);
                fx.Wait(() => !ReferenceEquals(center, fx.App.Overlay) && fx.App.Overlay is not null
                    && Descendants(fx.App.Overlay).Any(view => view.Id == page.Marker), page.Action + " opens its named destination");
                Assert.DoesNotContain("preferences.ui.", OverlayText(fx.App.Overlay!));
                Assert.DoesNotContain("policyPreferences.", OverlayText(fx.App.Overlay!));
                if (page.Marker == "mapping-source")
                {
                    Assert.Equal(new Terminal.Gui.Drawing.Color("#332B40"), fx.App.Overlay!.GetScheme().Normal.Background);
                    ExportPreferenceFrame(fx, columns, height, "audit-" + page.Action + "-" + locale, fx.App.Overlay.Title!.Trim());
                }
                if (page.Marker == "animation-back")
                {
                    fx.Invoke(() => Assert.All(fx.App.Overlay!.SubViews,
                        view => Assert.True(view.Frame.Bottom <= fx.App.Overlay.Viewport.Height, view.Id)));
                    ExportPreferenceFrame(fx, columns, height, "audit-animations-" + locale, fx.App.Overlay!.Title!.Trim());
                }
                if (category.Id == "models_execution" && page.Action == "preferences-models-open")
                {
                    Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id is "policy-command-aliases" or "policy-tool-preferences");
                }
                KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center"
                    && Descendants(fx.App.Overlay).Any(view => view.Id == page.Action), "Esc returns to " + category.Id);
                Assert.Equal("keep my draft", fx.App.Composer!.Text.ToString());
            }
            ExportPreferenceFrame(fx, columns, height, "audit-" + category.Id + "-" + locale,
                PreferenceCategoryCatalog.Resolve(new Localization(locale)).Single(item => item.Id == category.Id).Title);
            if (columns < 100) ClickId(fx, "preferences-back");
        }
        Assert.Equal(0, providers.OperationCalls);
        Assert.Equal(0, providers.CatalogRefreshCalls);
        Assert.Equal(0, providers.CreditCalls);
        Assert.Equal(sequence, fx.Server.SubscribeSince(0).Count);
    }, noColor: false);

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Menu_owned_commands_and_their_aliases_are_absent_from_completion_catalog_and_help(string locale) => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "audit-data"), Path.Combine(fx.Root, "audit-config"));
        host.SaveCommandAliases(host.Read().Revision, new Dictionary<string, string> { ["cuentasrapidas"] = "openrouter", ["misopciones"] = "preferences" });
        fx.StartTui(policyPreferences: host, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "/");
        fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "omni-command-helper"), "completion visible");
        var names = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-command-helper").SubViews.OfType<ListView>().Single()
            .Source!.ToList().Cast<object>().Select(row => row.ToString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0][1..]).ToArray();
        Assert.DoesNotContain(names, name => TuiMenuCommands.IsMenuOwned(name));
        Assert.DoesNotContain("cuentasrapidas", names); Assert.DoesNotContain("misopciones", names);
        fx.Invoke(() => fx.App.Composer!.Text = "");
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "extensions_skills", locale);
        ClickId(fx, "preferences-commands-open");
        var catalog = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "commands-list").Source!.ToList()
            .Cast<object>().Select(row => row.ToString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0][1..]).ToArray();
        Assert.DoesNotContain(catalog, name => TuiMenuCommands.IsMenuOwned(name));
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center", "return to extensions");
        if (fx.App.MainWindow!.Frame.Width < 100) ClickId(fx, "preferences-back");
        ChoosePreferenceCategory(fx, "diagnostics_data", locale);
        ClickId(fx, "preferences-help-open");
        var help = OverlayText(fx.App.Overlay!);
        foreach (var name in TuiMenuCommands.Categories.Keys)
        {
            Assert.DoesNotContain("/" + name + " —", help);
            Assert.DoesNotContain("/" + HumanCommandAliases.DisplayName(name, locale) + " —", help);
        }
        Assert.Contains("F4", help);
    });

    [Fact]
    public void Alias_editor_keeps_invalid_input_open_saves_exact_rows_and_cancel_discards_changes() => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "audit-data"), Path.Combine(fx.Root, "audit-config"));
        fx.StartTui(policyPreferences: host, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "extensions_skills"); ClickId(fx, "preferences-aliases-open");
        void Fill(string alias, string command) => fx.Invoke(() =>
        {
            Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "mapping-source").Text = alias;
            Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "mapping-target").Text = command;
        });
        var revision = host.Read().Revision;
        Fill("help", "plan"); ClickId(fx, "mapping-save");
        Assert.Equal("mapping-preferences", fx.App.Overlay!.Id); Assert.Equal(revision, host.Read().Revision);
        Fill("revisar", "plan"); ClickId(fx, "mapping-save");
        Assert.Equal("plan", host.Read().CommandAliases["revisar"]);
        Fill("temporal", "plan"); ClickId(fx, "mapping-apply"); ClickId(fx, "mapping-back");
        Assert.Equal("preferences-center", fx.App.Overlay!.Id);
        Assert.False(host.Read().CommandAliases.ContainsKey("temporal"));
        Assert.Equal("plan", host.Read().CommandAliases["revisar"]);
    });

    [Fact]
    public void Tool_mapping_editor_saves_only_its_map_and_rejects_invalid_pairs_inside_the_form() => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "audit-data"), Path.Combine(fx.Root, "audit-config"));
        host.SaveCommandAliases(host.Read().Revision, new Dictionary<string, string> { ["breve"] = "plan" });
        fx.StartTui(policyPreferences: host, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "tools_security"); ClickId(fx, "preferences-tool-mappings-open");
        fx.Invoke(() =>
        {
            Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "mapping-source").Text = "filesystem.write";
            Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "mapping-target").Text = "filesystem.write";
        });
        var revision = host.Read().Revision;
        ClickId(fx, "mapping-save");
        Assert.Equal("mapping-preferences", fx.App.Overlay!.Id); Assert.Equal(revision, host.Read().Revision);
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "mapping-target").Text = "ext.fixture.write");
        ClickId(fx, "mapping-save");
        Assert.Equal("ext.fixture.write", host.Read().ToolPreferences["filesystem.write"]);
        Assert.Equal("plan", host.Read().CommandAliases["breve"]);
        ClickId(fx, "mapping-back");
        Assert.Contains(Descendants(fx.App.Overlay!), view => view.Id == "preferences-tool-mappings-open");
    });

    [Theory]
    [InlineData("tools_security", "preferences-tools-open", "tools")]
    [InlineData("context_sessions", "preferences-context-open", "context")]
    [InlineData("extensions_skills", "preferences-hooks-open", "hooks")]
    [InlineData("extensions_skills", "preferences-mcp-open", "mcp")]
    public void A_failed_menu_read_keeps_an_error_page_and_returns_to_its_category(string category, string action, string query) => RunTuiTest(fx =>
    {
        var client = new MenuFaultClient(fx.Server, query);
        fx.StartTui(client: client);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, category); ClickId(fx, action);
        Assert.NotNull(fx.App.Overlay);
        Assert.DoesNotContain("IOException", OverlayText(fx.App.Overlay));
        Assert.Equal(1, client.Failures);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center"
            && Descendants(fx.App.Overlay).Any(view => view.Id == action), "failed read returns to its category");
        Assert.Equal(0, client.Sends);
    });

    private sealed class MenuFaultClient(IOmniClient inner, string query) : IOmniClient
    {
        internal int Failures, Sends;
        public CommandAck Send(WireEnvelope command, CancellationToken token)
        { Sends++; return inner.Send(command, token); }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken token)
        {
            if (name != query) return inner.Query(name, token);
            Failures++; throw new IOException("Menu read fixture failure");
        }
    }
}
