using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using BillingMode = OmniCore.Domain.BillingMode;
using System.Drawing;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Mouse_click_reaches_provider_buttons_through_the_application() => RunTuiTest(fx =>
    {
        fx.StartTui(providerConnections: OpenRouterUsabilityFixture(), initialColumns: 120, initialRows: 40);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickThroughApplication(fx, "preferences-openrouter-open");
        fx.Wait(() => fx.App.Overlay?.Id == "preferences-center"
            && Descendants(fx.App.Overlay).Any(view => view.Id == "provider-api-key"), "mouse opens inline API form");
        Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "provider-connection-list");
        ClickThroughApplication(fx, "provider-key-save");
        Assert.Equal("preferences-center", fx.App.Overlay!.Id);
        Assert.Equal("Escribe una clave API.", Descendants(fx.App.Overlay)
            .Single(view => view.Id == "provider-key-status").Text.ToString());
    });

    private static void ClickThroughApplication(TuiFixture fx, string id) => fx.Invoke(() =>
    {
        var view = Descendants(fx.App.Overlay!).Single(view => view.Id == id);
        var point = new Point(Math.Max(0, view.Viewport.Width / 2), Math.Max(0, view.Viewport.Height / 2));
        point = view.ViewportToScreen(in point);
        fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point, Flags = MouseFlags.LeftButtonClicked });
    });

    [Fact]
    public void Inline_provider_keeps_draft_on_resize_and_escape_returns_to_accounts() => RunTuiTest(fx =>
    {
        var providers = OpenRouterUsabilityFixture();
        fx.StartTui(providerConnections: providers, initialColumns: 120, initialRows: 40);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickThroughApplication(fx, "preferences-openrouter-open");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"), "inline key field visible");
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "provider-api-key")
            .Text = "fixture-draft-never-persisted");
        fx.Invoke(() => fx.Application.Screen = new Rectangle(0, 0, 80, 25));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 80
            && Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "provider-api-key")
                .Text.ToString() == "fixture-draft-never-persisted", "resize preserves masked draft");
        Assert.Equal(0, providers.OperationCalls);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center"
            && !Descendants(fx.App.Overlay).Any(view => view.Id == "provider-api-key"), "Esc returns to accounts, not chat");
        Assert.Equal(0, providers.OperationCalls);
    });

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Command_helper_lists_one_effective_language_without_account_shortcuts(string locale) => RunTuiTest(fx =>
    {
        fx.StartTui(localeSelection: new(locale, LocaleSelectionSource.Default));
        Type(fx, "/");
        fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "omni-command-helper"), "command helper visible");
        string[] names = [];
        fx.Invoke(() => names = fx.App.MainWindow!.SubViews.Single(view => view.Id == "omni-command-helper")
            .SubViews.OfType<ListView>().Single().Source!.ToList().Cast<object>()
            .Select(row => row.ToString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToArray());
        Assert.Contains("/" + HumanCommandAliases.DisplayName("plan", locale), names);
        Assert.Contains("/" + HumanCommandAliases.DisplayName("delegate", locale), names);
        Assert.Contains("/" + HumanCommandAliases.DisplayName("cancel", locale), names);
        Assert.DoesNotContain(names, name => OmniCore.Cli.TuiMenuCommands.IsMenuOwned(name[1..]));
        Assert.Equal(names.Length, names.Select(HumanCommandAliases.NormalizeSlashHead).Distinct().Count());
        Assert.DoesNotContain(names, name => HumanCommandAliases.NormalizeSlashHead(name) is "/login" or "/logout" or "/openrouter");
        if (locale == "es")
        {
            Assert.DoesNotContain("/commands", names);
            Assert.DoesNotContain("/context", names);
            Assert.DoesNotContain("/delegate", names);
            Assert.DoesNotContain("/images", names);
        }
    });

    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void OpenRouter_selection_opens_inline_form_and_keeps_validation_inside_preferences(int columns, int rows)
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            RunTuiTest(fx =>
            {
                var providers = OpenRouterUsabilityFixture();
                fx.StartTui(providerConnections: providers, initialColumns: columns, initialRows: rows);
                KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
                ChoosePreferenceCategory(fx, "accounts_connections");
                Assert.Contains(Descendants(fx.App.Overlay!).OfType<Button>(), button => button.Id == "preferences-openrouter-open"
                    && button.Text.Contains("OpenRouter", StringComparison.Ordinal));
                ExportPreferenceFrame(fx, columns, rows, "openrouter-entry", "OpenRouter");
                ClickId(fx, "preferences-openrouter-open");
                fx.Wait(() => fx.App.Overlay?.Id == "preferences-center"
                    && Descendants(fx.App.Overlay).Any(view => view.Id == "provider-api-key"), "provider form inside preferences");
                Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "provider-connection-list");
                Assert.Equal(0, providers.OperationCalls);
                Assert.Equal(0, providers.CreditCalls);
                ExportPreferenceFrame(fx, columns, rows, "openrouter-inline", "OpenRouter");
                var owner = fx.App.Overlay;
                var key = Descendants(owner!).OfType<TextField>().Single(view => view.Id == "provider-api-key");
                Assert.True(key.Secret);
                fx.Invoke(() => key.SetFocus());
                fx.InjectKey(new Key(KeyCode.Enter));
                fx.Wait(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "provider-key-status")
                    .Text.ToString() == "Escribe una clave API.", "empty key validation stays in form");
                Assert.Same(owner, fx.App.Overlay);
                Assert.Equal(0, providers.OperationCalls);
                providers.FailConnect = true;
                fx.Invoke(() => key.Text = "invalid-fixture-key");
                fx.InjectKey(new Key(KeyCode.Enter));
                fx.Wait(() => providers.OperationCalls == 1 && OverlayText(fx.App.Overlay!).Contains("No se pudo", StringComparison.Ordinal), "backend error stays in form");
                Assert.Same(owner, fx.App.Overlay);
                Assert.Equal("", key.Text.ToString());
                Assert.Equal("openrouter-custom", providers.LastProviderId);
                ClickId(fx, "provider-key-cancel");
                fx.Wait(() => fx.App.Overlay?.Id == "preferences-center"
                    && Descendants(fx.App.Overlay).Any(view => view.Id == "preferences-openrouter-open"), "back to accounts without chat");
                ClickId(fx, "preferences-provider-open-anthropic");
                fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"), "other providers have the same inline form");
                Assert.Contains("Anthropic", OverlayText(fx.App.Overlay!));
            });
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    }

    [Fact]
    public void OpenRouter_credit_control_queries_explicitly_preserves_unknown_balance_and_can_retry() => RunTuiTest(fx =>
    {
        var providers = OpenRouterUsabilityFixture();
        fx.StartTui(providerConnections: providers);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-openrouter-open");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"), "inline form visible");
        Assert.Equal(0, providers.CreditCalls);
        ClickId(fx, "provider-credits");
        fx.Wait(() => fx.App.Overlay?.Id == "openrouter-credits" && providers.CreditCalls == 1
            && OverlayText(fx.App.Overlay).Contains("Saldo de cuenta: —", StringComparison.Ordinal), "real credit consumer renders snapshot");
        var text = OverlayText(fx.App.Overlay!);
        Assert.Contains("Uso de la clave: 3", text);
        Assert.Contains("openrouter-custom", text);
        Assert.Contains("no está disponible", text);
        Assert.Equal("openrouter-custom", providers.LastProviderId);
        providers.FailCreditRead = true;
        ClickId(fx, "openrouter-credits-refresh");
        fx.Wait(() => providers.CreditCalls == 2 && OverlayText(fx.App.Overlay!).Contains("No se pudieron consultar", StringComparison.Ordinal), "failure is visible");
        Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "openrouter-credits-refresh").Enabled);
        ClickId(fx, "openrouter-credits-back");
        fx.Wait(() => fx.App.Overlay?.Id == "preferences-center"
            && Descendants(fx.App.Overlay).Any(view => view.Id == "provider-api-key"), "back to chosen provider without a second list");
    });

    private static ProviderConnectionsFixture OpenRouterUsabilityFixture() => new()
    {
        Rows =
        [
            new("anthropic", ProviderConnectionMethod.ApiKey, ProviderConnectionState.NotConfigured,
                BillingMode.MeteredCurrency, null, null, true, false, false, false, null, null),
            new("openrouter-custom", ProviderConnectionMethod.ApiKey, ProviderConnectionState.Connected,
                BillingMode.MeteredCurrency, null, null, true, true, true, true, null, null) { Backend = "openrouter" },
        ],
    };
}
