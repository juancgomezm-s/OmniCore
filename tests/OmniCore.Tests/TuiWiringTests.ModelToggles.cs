using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 40)]
    public void Model_toggles_include_unregistered_OpenRouter_models_and_persist_without_changing_conversation(int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "toggle-data"));
        var http = new PickerOpenRouterHandler();
        var service = new OpenRouterConnectionService(new FileCredentialStore(Path.Combine(fx.Root, "toggle-credentials")),
            paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-openrouter-picker-key", false, default).GetAwaiter().GetResult();
        service.RefreshCatalogAsync("openrouter", null, 0, 100, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        var registered = Enumerable.Range(0, 30).Select(i => new ModelRegistryModelDescriptor($"local{i:00}", "local", 8192, 8192, 2048)).ToArray();
        var policies = ModelPolicyHost.Create(paths.DataDirectory, registered);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, new("local", "local00"), "local00", true, default);
        fx.StartTui(policies: policies, providerConnections: host, initialColumns: width, initialRows: height);
        fx.Invoke(() => fx.App.Composer!.Text = "conservar este borrador");
        var sequence = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias");
        ChoosePreferenceCategory(fx, "models_execution");
        Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "preferences-model-select");
        ClickThroughApplication(fx, "preferences-models-enabled");
        fx.Wait(() => fx.App.Overlay?.Id == "model-toggles", "lista completa");
        Assert.Equal(new Terminal.Gui.Drawing.Color("#13222E"), fx.App.Overlay!.GetScheme().Normal.Background);
        Button Toggle(string id) => Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "model-toggle-" + id);
        Assert.StartsWith("[   █]", Toggle("local/local00").Text.ToString());
        ClickThroughApplication(fx, "model-toggle-local/local00");
        fx.Wait(() => !policies.IsVisibleInPicker("local", "local00"), "clic apaga una sola vez");
        KeyWithEffect(fx, KeyCode.CursorDown, () => Toggle("local/local01").HasFocus, "recorrer no activa");
        Assert.True(policies.IsVisibleInPicker("local", "local01"));
        fx.InjectKey(new Key(KeyCode.Space));
        fx.Wait(() => !policies.IsVisibleInPicker("local", "local01"), "espacio apaga una sola vez");
        for (var i = 2; i < registered.Length; i++)
        {
            var id = $"local/local{i:00}";
            KeyWithEffect(fx, KeyCode.CursorDown, () => Toggle(id).HasFocus, "lista con desplazamiento");
        }
        KeyWithEffect(fx, KeyCode.CursorDown, () => Toggle("openrouter/org/first").HasFocus, "modelo de catálogo sin registrar");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => !policies.IsVisibleInPicker("openrouter", "org/first"), "Enter apaga catálogo una sola vez");
        Assert.StartsWith("[█   ]", Toggle("openrouter/org/first").Text.ToString());
        Assert.Contains("Visibles en F3: 29/32", OverlayText(fx.App.Overlay!));
        Assert.Equal("local00", policies.CurrentSelection(workspace, default)!.ModelId);
        Assert.Contains("local00", fx.App.Header!.Text);
        Assert.Equal("conservar este borrador", fx.App.Composer!.Text.ToString());
        Assert.Equal(sequence, fx.Server.SubscribeSince(0).Count);
        Assert.Empty(OmniHost.LoadUserConfiguration(paths).Registry.Models());
        Assert.Equal(1, http.RequestCount);
        using (var reopened = ModelPolicyHost.Create(paths.DataDirectory, registered))
            Assert.False(reopened.IsVisibleInPicker("openrouter", "org/first"));
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text = "openrouter");
        ExportPreferenceFrame(fx, width, height, "model-toggles", "org/first");
        Assert.StartsWith("[█   ]", Toggle("openrouter/org/first").Text.ToString());
        Assert.StartsWith("[   █]", Toggle("openrouter/org/second").Text.ToString());
        ClickThroughApplication(fx, "model-toggle-openrouter/org/first");
        fx.Wait(() => policies.IsVisibleInPicker("openrouter", "org/first"), "reactivar también conserva la fila");
        ClickThroughApplication(fx, "model-toggle-openrouter/org/first");
        fx.Wait(() => !policies.IsVisibleInPicker("openrouter", "org/first"), "desactivar para el selector");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center", "volver a preferencias");
        for (var i = 0; i < 3 && fx.App.Overlay is not null; i++) fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "cerrar preferencias");
        Type(fx, "/modelo");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "omni-model-palette", "/modelo elige activos");
        var items = PickerItems(fx);
        Assert.DoesNotContain(items, row => row.Contains("local00") || row.Contains("local01") || row.Contains("org/first"));
        Assert.Contains(items, row => row.Contains("org/second"));
        ChoosePickerItem(fx, "local02");
        fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.Contains("local02"), "elegir activo");
        Assert.Equal(sequence, fx.Server.SubscribeSince(0).Count);
        Assert.Equal(1, http.RequestCount);
    }, noColor: false);

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Model_toggles_and_empty_picker_remain_localized_and_separate(string locale) => RunTuiTest(fx =>
    {
        var models = new[] { new ModelRegistryModelDescriptor("only-model", "local", 8192, 8192, 2048) };
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "toggle-empty"), models);
        fx.StartTui(policies: policies, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "settings");
        ChoosePreferenceCategory(fx, "models_execution", locale);
        ClickId(fx, "preferences-models-enabled");
        Assert.DoesNotContain("shell.models.", OverlayText(fx.App.Overlay!));
        ClickThroughApplication(fx, "model-toggle-local/only-model");
        fx.Wait(() => !policies.IsVisibleInPicker("local", "only-model"), "off");
        KeyWithEffect(fx, KeyCode.CursorDown, () => Descendants(fx.App.Overlay!).Any(view => view.Id == "model-toggle-local/only-model" && view.HasFocus), "fin vuelve al inicio");
        Assert.False(policies.IsVisibleInPicker("local", "only-model"));
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "preferences-center", "back");
        for (var i = 0; i < 3 && fx.App.Overlay is not null; i++) fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "close");
        Type(fx, "/" + HumanCommandAliases.DisplayName("model", locale));
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "omni-model-palette", "localized selector");
        Assert.Empty(PickerItems(fx));
        Assert.Contains(new Localization(locale).Resolve("shell.no_visible_models_check_f4_models"), OverlayText(fx.App.Overlay!));
        Assert.Null(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default));
    });
}
