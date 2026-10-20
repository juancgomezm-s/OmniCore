using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class LiveOpenRouterCatalogTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void Live_OpenRouter_catalog_is_visible_and_selectable_in_F3_without_inference() =>
        TuiWiringTests.RunLiveOpenRouterCatalog();
}

public sealed partial class TuiWiringTests
{
    internal static void RunLiveOpenRouterCatalog()
    {
        if (Environment.GetEnvironmentVariable("OMNI_LIVE_OPENROUTER_CATALOG") != "1")
        {
            Assert.Skip("Requires explicit opt-in, configured OpenRouter credentials and network; no inference.");
            return;
        }
        var previousData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var previousConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var previousDirectory = Environment.CurrentDirectory;
        var previousColor = Environment.GetEnvironmentVariable("NO_COLOR");
        var evidence = Environment.GetEnvironmentVariable("OMNI_LIVE_EVIDENCE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(evidence));
        using var fx = TuiFixture.Create();
        try
        {
            var data = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_DATA_DIR");
            var config = Environment.GetEnvironmentVariable("OMNI_LIVE_USER_CONFIG_DIR");
            Assert.True(Directory.Exists(data) && Directory.Exists(config), "Explicit real user paths required.");
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.CurrentDirectory = fx.Workspace;
            var paths = OmniHost.CreatePlatformPaths();
            var host = new TuiProviderConnectionHost(paths: paths);
            var provider = host.List(default).FirstOrDefault(row => row.Backend == "openrouter" && row.CanDiscoverModels);
            Assert.NotNull(provider);
            var configuredBefore = OmniHost.LoadUserConfiguration(paths).Registry.Models()
                .Count(model => model.ProviderId == provider.ProviderId);
            var cachedBefore = host.CachedOpenRouterPickerModels(default).Count;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var page = host.RefreshOpenRouterCatalogAsync(provider.ProviderId, null, 0, 100, deadline.Token)
                .GetAwaiter().GetResult();
            Assert.False(page.Stale, "Official OpenRouter catalog could not be refreshed.");
            Assert.Null(page.Error);
            var choices = host.CachedOpenRouterPickerModels(default);
            Assert.NotEmpty(choices);
            var registry = OmniHost.LoadUserConfiguration(paths).Registry;
            Assert.Equal(configuredBefore, registry.Models().Count(model => model.ProviderId == provider.ProviderId));
            var selected = choices.FirstOrDefault(model => model.Id.EndsWith(":free", StringComparison.Ordinal)
                    && registry.Model(model.Id) is null)
                ?? choices.First(model => registry.Model(model.Id) is null);
            var policies = ModelPolicyHost.Create();
            fx.StartTui(policies: policies, providerConnections: host, initialColumns: 120, initialRows: 40);
            KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "F3 real con catálogo OpenRouter");
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "openrouter");
            fx.Wait(() => PickerItems(fx).Any(item => item.Contains(selected.Id)), "modelos de OpenRouter visibles sin registrar todo el catálogo");
            var visibleModels = PickerItems(fx).Length;
            fx.Invoke(() =>
            {
                var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
                list.SelectedItem = Array.FindIndex(PickerItems(fx), row => row.Contains(selected.Id));
                list.EnsureSelectedItemVisible(); list.SetFocus();
            });
            ExportPreferenceFrame(fx, 120, 40, "live-openrouter-picker", selected.Id);
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.Contains(selected.Id), "modelo OpenRouter elegido mediante Enter");
            var actual = OmniHost.LoadUserConfiguration(paths).Registry;
            Assert.Equal(selected.ProviderId, actual.Model(selected.Id)!.ProviderId);
            Assert.Equal(configuredBefore + 1, actual.Models().Count(model => model.ProviderId == provider.ProviderId));
            Assert.Empty(fx.Decoded<ModelStepStarted>());
            Assert.Single(fx.Decoded<UserInputReceived>());
            KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "reabrir conserva selección");
            Assert.Contains(PickerItems(fx), row => row.Contains("✓") && row.Contains(selected.Id));
            Directory.CreateDirectory(evidence!);
            File.WriteAllText(Path.Combine(evidence!, "result.json"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, provider = provider.ProviderId, realCatalog = true,
                configuredBefore, cachedBefore, totalCatalogModels = page.TotalCount, selectableTextModels = choices.Count,
                visibleModels, selectedModel = selected.Id, registeredOnlySelection = true,
                configuredAfter = configuredBefore + 1, inferenceRequests = 0,
                tuiDriver = "Terminal.Gui DOTNET", nativeWindowAutomated = false
            }));
        }
        finally
        {
            fx.Application?.RequestStop(); fx.Loop?.Join(5000);
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, previousData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, previousConfig);
            Environment.SetEnvironmentVariable("NO_COLOR", previousColor);
        }
    }
}
