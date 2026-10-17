using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static string ToolPreferenceFixtureCatalog() => System.Text.Json.JsonSerializer.Serialize(new
    {
        mode = "Plan", tools = new (string Id, string Effect, string Risk, string[] Tags)[]
        {
            (Id: "filesystem.write", Effect: "NonIdempotent", Risk: "High", Tags: new[] { "file", "write" }),
            ("ext.fixture.write", "NonIdempotent", "High", new[] { "write", "file" }),
            ("ext.incompatible.tags", "NonIdempotent", "High", new[] { "process" }),
            ("ext.incompatible.risk", "NonIdempotent", "Critical", new[] { "write", "file" }),
            ("ext.incompatible.effect", "None", "High", new[] { "write", "file" }),
        }.Select(tool => new { visibleName = tool.Id, toolId = tool.Id,
            source = "Extension:User:Trusted:fixture@1", effectClass = tool.Effect, risk = tool.Risk,
            tags = tool.Tags, protection = "Protected", decision = "reject:PLAN mode", visible = false,
            description = "Escribe archivos del proyecto con comprobación de su versión." })
    });

    [Fact]
    public void Tool_catalog_reports_real_descriptor_metadata_for_guided_preferences() => RunTuiTest(fx =>
    {
        var catalog = ToolsInspectionJson.Decode(fx.Server.Query("tools", default)!.Json)!;
        var definitions = OmniHost.CreateExplorerTools().Catalog();
        var tool = definitions.Find(new OmniCore.Abstractions.ToolId("filesystem.read"))!.Descriptor;
        var row = catalog.Tools.Single(entry => entry.ToolId == "filesystem.read");
        Assert.Equal(tool.Risk.ToString(), row.Risk);
        Assert.Equal(tool.Protection.ToString(), row.Protection);
        Assert.Equal(tool.Tags, row.Tags);
    });

    private static void ChooseToolPreference(TuiFixture fx, string sourceId, string targetId)
    {
        fx.Invoke(() =>
        {
            var source = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "mapping-source");
            source.SelectedItem = source.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().IndexOf(sourceId);
            var target = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "mapping-target");
            target.SelectedItem = target.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().IndexOf(targetId);
            Assert.True(source.SelectedItem >= 0); Assert.True(target.SelectedItem >= 0);
            source.SetFocus();
        });
    }

    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 40)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 120, 40)]
    public void Guided_tool_preferences_use_catalog_choices_and_keyboard_without_saving_implicitly(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "choice-data"), Path.Combine(fx.Root, "choice-config"));
        var client = new ToolQueryClient(fx.Server, ToolPreferenceFixtureCatalog());
        fx.StartTui(policyPreferences: host, client: client, initialColumns: width, initialRows: height,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        var sequence = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "tools_security", locale); ClickId(fx, "preferences-tool-mappings-open");
        var revision = host.Read().Revision;
        ChooseToolPreference(fx, "filesystem.write", "ext.fixture.write");
        var target = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "mapping-target");
        KeyWithEffect(fx, KeyCode.Enter, () => target.HasFocus, "Enter proceeds to the alternative");
        KeyWithEffect(fx, KeyCode.Enter, () => Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-save").Enabled,
            "Enter adds an explicit pending preference");
        Assert.Equal(revision, host.Read().Revision);
        fx.Invoke(() => Assert.All(fx.App.Overlay!.SubViews, view =>
        {
            Assert.True(view.Frame.Top >= 0 && view.Frame.Bottom <= fx.App.Overlay.Viewport.Height, view.Id);
            Assert.True(view.Frame.Left >= 0 && view.Frame.Right <= fx.App.Overlay.Viewport.Width, view.Id);
        }));
        ExportPreferenceFrame(fx, width, height, "guided-tools-" + locale, "filesystem.write");
        ClickThroughApplication(fx, "mapping-save");
        Assert.Equal("ext.fixture.write", host.Read().ToolPreferences["filesystem.write"]);
        Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-save").Enabled);
        ClickId(fx, "mapping-remove"); ClickId(fx, "mapping-back");
        Assert.Equal("ext.fixture.write", host.Read().ToolPreferences["filesystem.write"]);
        Assert.Equal(sequence, fx.Server.SubscribeSince(0).Count);
        Assert.Equal(0, client.Commands);
    }, noColor: false);

    [Theory]
    [InlineData(null, "No se pudo consultar")]
    [InlineData("{", "No se pudo consultar")]
    [InlineData("{\"tools\":[]}", "No hay alternativas compatibles")]
    [InlineData("{\"tools\":[{\"visibleName\":\"read\",\"toolId\":\"read\",\"source\":\"core\",\"effectClass\":\"None\",\"visible\":true,\"decision\":\"permission policy\"}]}", "no informa")]
    public void Guided_tool_preferences_explain_empty_and_unavailable_catalogs_and_can_remove_obsolete_rules(string? json, string explanation) => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "empty-data"), Path.Combine(fx.Root, "empty-config"));
        host.SaveToolPreferences(host.Read().Revision, new Dictionary<string, string> { ["filesystem.write"] = "ext.removed.write" });
        var client = new ToolQueryClient(fx.Server, json);
        fx.StartTui(policyPreferences: host, client: client, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "tools_security"); ClickId(fx, "preferences-tool-mappings-open");
        Assert.Contains(explanation, OverlayText(fx.App.Overlay!));
        Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-apply").Enabled);
        Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-source").Visible);
        Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-target").Visible);
        ClickId(fx, "mapping-remove"); ClickId(fx, "mapping-save");
        Assert.Empty(host.Read().ToolPreferences);
        Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "mapping-save").Visible);
        ExportPreferenceFrame(fx, 80, 25, "guided-tools-empty-" + (json is null ? "missing" : json == "{" ? "invalid" : json.Contains("[]") ? "none" : "old-host"), explanation);
        Assert.Equal(0, client.Commands);
    });

    [Fact]
    public void Guided_tool_preferences_report_revision_conflict_without_overwriting_external_changes() => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Root, Path.Combine(fx.Root, "stale-data"), Path.Combine(fx.Root, "stale-config"));
        fx.StartTui(policyPreferences: host, client: new ToolQueryClient(fx.Server, ToolPreferenceFixtureCatalog()), initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "tools_security"); ClickId(fx, "preferences-tool-mappings-open");
        ChooseToolPreference(fx, "filesystem.write", "ext.fixture.write"); ClickId(fx, "mapping-apply");
        host.SaveCommandAliases(host.Read().Revision, new Dictionary<string, string> { ["revisar"] = "plan" });
        ClickId(fx, "mapping-save");
        Assert.Contains("configuración cambió", OverlayText(fx.App.Overlay!));
        Assert.Empty(host.Read().ToolPreferences);
        Assert.Equal("plan", host.Read().CommandAliases["revisar"]);
        Assert.Equal("mapping-preferences", fx.App.Overlay!.Id);
    });
}
