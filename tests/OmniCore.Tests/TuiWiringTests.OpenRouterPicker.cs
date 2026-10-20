using System.Net;
using System.Text;
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
    [InlineData(false, 80, 25)]
    [InlineData(true, 120, 40)]
    public void OpenRouter_picker_exposes_cached_models_and_registers_only_the_Enter_selection(bool warmCache,
        int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "picker-data"));
        var credentials = new FileCredentialStore(Path.Combine(fx.Root, "picker-credentials"));
        var http = new PickerOpenRouterHandler();
        var service = new OpenRouterConnectionService(credentials, paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-openrouter-picker-key", false, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        if (warmCache) service.RefreshCatalogAsync("openrouter", null, 0, 100, default).GetAwaiter().GetResult();
        var requests = http.RequestCount;
        var policies = ModelPolicyHost.Create(paths.DataDirectory);
        fx.StartTui(policies: policies, providerConnections: host, initialColumns: width, initialRows: height);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "F3 con OpenRouter conectado");
        Assert.Equal(requests, http.RequestCount); // Opening F3 uses cache only.
        Assert.Empty(OmniHost.LoadUserConfiguration(paths).Registry.Models());
        if (!warmCache)
        {
            Assert.Contains("Actualiza OpenRouter", OverlayText(fx.App.Overlay!));
            Assert.DoesNotContain("Inicia sesión", OverlayText(fx.App.Overlay!));
            ClickId(fx, "omni-palette-openrouter-refresh");
            fx.Wait(() => PickerItems(fx).Any(item => item.Contains("org/second")), "catálogo descargado visible en F3");
            Assert.Equal(1, http.RequestCount);
        }
        Assert.Equal(2, host.CachedOpenRouterPickerModels(default).Count);
        fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "openrouter");
        fx.Wait(() => PickerItems(fx).Length == 2, "búsqueda por proveedor");
        Assert.DoesNotContain(PickerItems(fx), item => item.Contains("org/image-only"));
        ExportPreferenceFrame(fx, width, height, "openrouter-picker", "org/second");
        fx.Invoke(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            list.SelectedItem = Array.FindIndex(PickerItems(fx), item => item.Contains("org/second"));
            list.SetFocus();
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null && fx.App.Header!.Text.Contains("org/second"), "Enter elige el modelo directamente");
        var registered = Assert.Single(OmniHost.LoadUserConfiguration(paths).Registry.Models());
        Assert.Equal("org/second", registered.Id);
        Assert.Equal("openrouter", registered.ProviderId);
        Assert.Equal(1, http.RequestCount); // No inference or network on selection.
        Assert.Equal("org/second", policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.ModelId);
    }, noColor: false);

    [Fact]
    public void OpenRouter_picker_failed_refresh_preserves_cached_models_search_and_selection() => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "stale-picker-data"));
        var credentials = new FileCredentialStore(Path.Combine(fx.Root, "stale-picker-credentials"));
        var http = new PickerOpenRouterHandler();
        var service = new OpenRouterConnectionService(credentials, paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-openrouter-picker-key", false, default).GetAwaiter().GetResult();
        service.RefreshCatalogAsync("openrouter", null, 0, 100, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        var policies = ModelPolicyHost.Create(paths.DataDirectory);
        fx.StartTui(policies: policies, providerConnections: host);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "selector");
        fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "second");
        http.Fail = true;
        ClickId(fx, "omni-palette-openrouter-refresh");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("copia anterior", StringComparison.OrdinalIgnoreCase), "aviso de catálogo conservado");
        Assert.Equal("second", fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text.ToString());
        Assert.Contains("org/second", Assert.Single(PickerItems(fx)));
        Assert.Null(policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default));
        Assert.Empty(OmniHost.LoadUserConfiguration(paths).Registry.Models());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenRouter_picker_refresh_preserves_new_search_or_honors_Escape(bool close) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "waiting-picker-data"));
        var store = new FileCredentialStore(Path.Combine(fx.Root, "waiting-picker-credentials"));
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new PickerOpenRouterHandler { Gate = gate };
        var service = new OpenRouterConnectionService(store, paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-openrouter-picker-key", false, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        fx.StartTui(policies: ModelPolicyHost.Create(paths.DataDirectory), providerConnections: host);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "selector");
        ClickId(fx, "omni-palette-openrouter-refresh");
        fx.Wait(() => http.RequestCount == 1, "consulta en espera");
        if (close)
        {
            fx.InjectKey(new Key(KeyCode.Esc));
            fx.Wait(() => fx.App.Overlay is null && http.Cancelled.Task.IsCompleted, "Esc cancela la consulta");
            gate.TrySetResult(true);
            fx.Invoke(() => fx.App.PollOnce());
            Assert.Null(fx.App.Overlay);
        }
        else
        {
            fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "second");
            gate.TrySetResult(true);
            fx.Wait(() => PickerItems(fx).Length == 1 && PickerItems(fx)[0].Contains("org/second"), "se conserva la búsqueda nueva");
            Assert.Equal("second", fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text.ToString());
        }
        Assert.Empty(OmniHost.LoadUserConfiguration(paths).Registry.Models());
    });

    private sealed class PickerOpenRouterHandler : HttpMessageHandler
    {
        public int RequestCount;
        public bool Fail;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref RequestCount);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer fixture-openrouter-picker-key", request.Headers.Authorization!.ToString());
            if (Gate is not null)
            {
                try { await Gate.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(true); throw; }
            }
            return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Fail ? "{}" : """
                    {"data":[
                      {"id":"org/first","name":"First","context_length":8192,"pricing":{"prompt":"0","completion":"0"},"architecture":{"output_modalities":["text"]}},
                      {"id":"org/second","name":"Second","context_length":16384,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"output_modalities":["text"]}},
                      {"id":"org/image-only","context_length":4096,"architecture":{"output_modalities":["image"]}}
                    ]}
                    """, Encoding.UTF8, "application/json")
            };
        }
    }
}
