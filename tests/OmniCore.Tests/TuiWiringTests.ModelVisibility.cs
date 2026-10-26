using OmniCore.Host;
using OmniCore.Infrastructure;
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
    public void OpenRouter_visibility_controls_persist_filter_bulk_edits_and_only_show_enabled_models_in_F3(int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "visibility-data"));
        var http = new PickerOpenRouterHandler();
        var service = new OpenRouterConnectionService(new FileCredentialStore(Path.Combine(fx.Root, "visibility-credentials")),
            paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-openrouter-picker-key", false, default).GetAwaiter().GetResult();
        service.RefreshCatalogAsync("openrouter", null, 0, 100, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        var registered = new[] { new ModelRegistryModelDescriptor("active-local", "local", 8192, 8192, 2048) };
        var policies = ModelPolicyHost.Create(paths.DataDirectory, registered);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, new("local", "active-local"), "active-local", true, default);
        fx.StartTui(policies: policies, providerConnections: host, initialColumns: width, initialRows: height);
        fx.Invoke(() => fx.App.Composer!.Text = "conservar borrador");
        var eventsBefore = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "settings");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-openrouter-open"); ClickId(fx, "provider-discover");
        fx.Wait(() => fx.App.Overlay?.Id == "openrouter-catalog", "catalog");
        TableView Grid() => Descendants(fx.App.Overlay!).OfType<TableView>().Single();
        Assert.Equal("[✓]", Grid().Table![0, 0]);
        Assert.Equal("—", Grid().Table![1, 0]);
        fx.Invoke(() => Grid().SetSelection(0, 1, false));
        fx.Wait(() => !Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "openrouter-catalog-visibility").Enabled,
            "image-only models are not offered in the text menu");
        fx.Invoke(() => Grid().SetSelection(0, 0, false));
        ClickThroughApplication(fx, "openrouter-catalog-visibility");
        fx.Wait(() => !policies.IsVisibleInPicker("openrouter", "org/first"), "hide without registering");
        Assert.Equal("[ ]", Grid().Table![0, 0]);
        fx.Invoke(() => Grid().SetFocus());
        fx.InjectKey(new Key(KeyCode.Space));
        fx.Wait(() => policies.IsVisibleInPicker("openrouter", "org/first"), "space restores visibility");
        Assert.Equal("[✓]", Grid().Table![0, 0]);
        ExportPreferenceFrame(fx, width, height, "openrouter-visibility", "Costo (USD/M)");
        ClickThroughApplication(fx, "openrouter-catalog-manage-visibility");
        fx.Wait(() => fx.App.Overlay?.Id == "model-toggles", "maintenance from catalog");
        Assert.Equal("openrouter", Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text.ToString());
        ClickThroughApplication(fx, "model-toggles-hide-filtered");
        Assert.False(policies.IsVisibleInPicker("openrouter", "org/first"));
        Assert.False(policies.IsVisibleInPicker("openrouter", "org/second"));
        Assert.True(policies.IsVisibleInPicker("local", "active-local"));
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single().Text = "org/first");
        ClickThroughApplication(fx, "model-toggles-show-filtered");
        Assert.True(policies.IsVisibleInPicker("openrouter", "org/first"));
        Assert.False(policies.IsVisibleInPicker("openrouter", "org/second"));
        using (var reopened = ModelPolicyHost.Create(paths.DataDirectory, registered))
        {
            Assert.True(reopened.IsVisibleInPicker("openrouter", "org/first"));
            Assert.False(reopened.IsVisibleInPicker("openrouter", "org/second"));
            Assert.True(reopened.IsVisibleInPicker("local", "active-local"));
        }
        ExportPreferenceFrame(fx, width, height, "model-visibility-maintenance", "org/first");
        for (var i = 0; i < 4 && fx.App.Overlay is not null; i++) fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "close maintenance");
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "F3 uses saved visibility");
        Assert.Contains(PickerItems(fx), row => row.Contains("org/first"));
        Assert.DoesNotContain(PickerItems(fx), row => row.Contains("org/second"));
        Assert.Equal("active-local", policies.CurrentSelection(workspace, default)!.ModelId);
        Assert.Equal("conservar borrador", fx.App.Composer!.Text.ToString());
        Assert.Empty(OmniHost.LoadUserConfiguration(paths).Registry.Models());
        Assert.Equal(eventsBefore, fx.Server.SubscribeSince(0).Count);
        Assert.Equal(1, http.RequestCount);
    }, noColor: false);

    [Fact]
    public void Model_visibility_bulk_write_is_atomic_and_scoped_to_the_exact_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-visibility-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences.db");
            using (var preferences = new ModelPickerPreferences(path))
            {
                Assert.Throws<InvalidOperationException>(() => preferences.SetVisibleMany(
                    [("first", "same-id"), (null!, "invalid")], false, TestContext.Current.CancellationToken));
                Assert.True(preferences.IsVisible("first", "same-id"));
                preferences.SetVisibleMany([("first", "same-id"), ("first", "other")], false, TestContext.Current.CancellationToken);
                Assert.True(preferences.IsVisible("second", "same-id"));
            }
            using var reopened = new ModelPickerPreferences(path);
            Assert.False(reopened.IsVisible("first", "same-id"));
            Assert.False(reopened.IsVisible("first", "other"));
        }
        finally { Directory.Delete(root, true); }
    }
}
