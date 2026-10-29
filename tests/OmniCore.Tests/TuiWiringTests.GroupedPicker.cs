using System.Net;
using System.Text;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", 80, 25, "Gratuitos")]
    [InlineData("en", 130, 38, "Free models")]
    [InlineData("fr", 80, 25, "Modèles gratuits")]
    [InlineData("it", 130, 38, "Modelli gratuiti")]
    public void Grouped_picker_prioritizes_free_models_preserves_hierarchy_and_selects_only_models(string locale,
        int width, int height, string freeTitle) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "grouped-picker"));
        var http = new GroupedPickerHttp();
        var service = new OpenRouterConnectionService(new FileCredentialStore(Path.Combine(fx.Root, "grouped-credentials")),
            paths, () => new HttpClient(http, false));
        service.ConnectAsync("openrouter", "fixture-grouped-key", false, default).GetAwaiter().GetResult();
        service.RefreshCatalogAsync("openrouter", null, 0, 100, default).GetAwaiter().GetResult();
        var host = new TuiProviderConnectionHost(paths: paths, openRouter: service);
        var policies = ModelPolicyHost.Create(paths.DataDirectory);
        fx.StartTui(policies, providerConnections: host, initialColumns: width, initialRows: height,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.InjectKey(new Key(KeyCode.F3));
        fx.Wait(() => fx.App.Overlay?.Id == "omni-model-palette", "lista de modelos agrupada");
        fx.Read(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            var rows = list.Source!.ToList().Cast<ModelPickerListRow>().ToArray();
            Assert.Equal(freeTitle, rows[0].Text);
            Assert.StartsWith("  OpenRouter", rows[1].Text);
            Assert.StartsWith("    Google", rows[2].Text);
            Assert.StartsWith("      google/free", rows[3].Text);
            var modelRows = rows.Where(row => row.Model is not null).ToArray();
            Assert.Equal(4, modelRows.Length);
            Assert.All(modelRows.Take(2), row => Assert.True(row.IsFree));
            Assert.All(modelRows.Skip(2), row => Assert.False(row.IsFree));
            Assert.Equal("google/free", rows[list.SelectedItem!.Value].Model!.Id);
            Assert.Single(modelRows.Select(row => row.Text.GetColumns(false)).Distinct());
            Assert.EndsWith("0.10/10", modelRows.Single(row => row.Model!.Id == "meta-llama/paid").Text);
            Assert.Contains("1M", modelRows.Single(row => row.Model!.Id == "openai/試験-free").Text);
            Assert.Equal("—", modelRows.Single(row => row.Model!.Id == "anthropic/unknown").Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        });
        Assert.Equal(1, http.Requests); // Showing this view never refreshes or infers.
        ExportPreferenceFrame(fx, width, height, "grouped-picker-" + locale, freeTitle);
        fx.InjectKey(new Key(KeyCode.CursorDown));
        fx.Read(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            var selected = (ModelPickerListRow)list.Source!.ToList()[list.SelectedItem!.Value]!;
            Assert.Equal("openai/試験-free", selected.Model!.Id);
        });
        fx.InjectKey(new Key(KeyCode.End));
        fx.Read(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            Assert.Equal("meta-llama/paid", ((ModelPickerListRow)list.Source!.ToList()[list.SelectedItem!.Value]!).Model!.Id);
        });
        fx.InjectKey(new Key(KeyCode.Home));
        fx.Read(() => Assert.Equal(0, fx.App.Overlay!.SubViews.OfType<ListView>().Single().Viewport.Y));
        fx.InjectKey(new Key(KeyCode.F | KeyCode.CtrlMask));
        fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "meta");
        fx.Wait(() => PickerItems(fx).Length == 1, "buscar fabricante conserva únicamente sus modelos");
        fx.InjectKey(new Key(KeyCode.Enter));
        ConfirmSelectedModelPreferences(fx, "meta-llama/paid");
        fx.Wait(() => fx.App.Overlay is null, "Enter selecciona modelo, nunca encabezados");
        Assert.Equal("meta-llama/paid", policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.ModelId);
        Assert.Equal(1, http.Requests);
    }, noColor: false);

    private sealed class GroupedPickerHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[
                      {"id":"anthropic/unknown","name":"Anthropic: Unknown","context_length":8192,"pricing":{},"architecture":{"output_modalities":["text"]}},
                      {"id":"meta-llama/paid","name":"Meta: Paid","context_length":131072,"pricing":{"prompt":"0.0000001","completion":"0.00001"},"architecture":{"output_modalities":["text"]}},
                      {"id":"openai/試験-free","name":"OpenAI: Free","context_length":1000000,"pricing":{"prompt":"0","completion":"0"},"architecture":{"output_modalities":["text"]}},
                      {"id":"google/free","name":"Google: Free","context_length":32768,"pricing":{"prompt":"0","completion":"0"},"architecture":{"output_modalities":["text"]}}
                    ]}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
