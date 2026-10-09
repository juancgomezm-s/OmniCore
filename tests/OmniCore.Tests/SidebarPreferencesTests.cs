using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class SidebarPreferencesTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "omni-sidebar-settings-" + Guid.NewGuid().ToString("N"));
        internal readonly DefaultPlatformPaths Paths;
        internal readonly string Workspace;
        internal readonly SidebarConfiguration Settings;
        internal Fixture()
        {
            Paths = new(Path.Combine(Root, "data")); Workspace = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Paths.ConfigDirectory);
            Settings = new(Paths, Workspace);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact]
    public void Scoped_yaml_preferences_preserve_other_settings_and_survive_reopen()
    {
        using var fx = new Fixture();
        var user = Path.Combine(fx.Paths.ConfigDirectory, "settings.yaml");
        File.WriteAllText(user, "budget: { session: 3, daily: 9 }\nsidebar: { mode: stacked }\nwidgets:\n  core.context: { visible: auto, priority: 70 }\n");
        var original = fx.Settings.Read(); Assert.Empty(original.Diagnostics); Assert.Equal("stacked", original.Preferences.Mode);
        fx.Settings.Set("User", "widgets.core.context.expanded", "true", original.Revision);
        var loaded = new ConfigLoader().Load(null, null, File.ReadAllText(user));
        Assert.Equal(3, loaded.SessionCapUsd); Assert.Equal(9, loaded.DailyCapUsd);
        Assert.True(loaded.Settings!.Widgets!["core.context"].Expanded);
        var project = Path.Combine(fx.Workspace, ".omnicore"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "settings.yaml"), "sidebar: { mode: overlay }\nwidgets:\n  core.context: { visible: false }\n");
        Assert.Equal("stacked", fx.Settings.Read().Preferences.Mode); // untrusted repository ignored
        new WorkspaceTrustStore(fx.Paths).SetTrusted(fx.Workspace, true);
        var inherited = fx.Settings.Read(); Assert.Empty(inherited.Diagnostics); Assert.Equal("overlay", inherited.Preferences.Mode);
        Assert.Equal("false", inherited.Preferences.Widgets!["core.context"].Visible);
        Assert.True(inherited.Preferences.Widgets["core.context"].Expanded); Assert.Equal(70, inherited.Preferences.Widgets["core.context"].Priority);
        Assert.Equal("Project", inherited.Sources!["sidebar.mode"]);
        fx.Settings.Set("Workspace", "sidebar.mode", "tabbed", inherited.Revision);
        var reopened = new SidebarConfiguration(fx.Paths, fx.Workspace).Read();
        Assert.Equal("tabbed", reopened.Preferences.Mode); Assert.Equal("Workspace", reopened.Sources!["sidebar.mode"]);
        Assert.Equal("stacked", new ConfigLoader().Load(null, null, File.ReadAllText(user)).Settings!.Sidebar!.Mode);
        fx.Settings.Set("Workspace", "sidebar.mode", "reset", reopened.Revision);
        Assert.Equal("overlay", fx.Settings.Read().Preferences.Mode);
        new WorkspaceTrustStore(fx.Paths).SetTrusted(fx.Workspace, false);
        Assert.Equal("stacked", fx.Settings.Read().Preferences.Mode);
    }

    [Theory]
    [InlineData("sidebar: { visible: maybe }")]
    [InlineData("sidebar: { mode: banana }")]
    [InlineData("sidebar: { stackedMinWidth: 90, tabbedMinWidth: 120 }")]
    [InlineData("widgets: { core.plan: { priority: 1001 } }")]
    [InlineData("widgets: { core.plan: { expanded: 'true' } }")]
    public void Invalid_presentation_yaml_is_reported_with_location(string yaml)
    {
        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null, yaml));
        Assert.Contains(error.Diagnostics, d => d.Line > 0 && d.Column > 0);
        using var fx = new Fixture(); File.WriteAllText(Path.Combine(fx.Paths.ConfigDirectory, "settings.yaml"), yaml);
        var snapshot = fx.Settings.Read(); Assert.NotEmpty(snapshot.Diagnostics);
        Assert.Throws<InvalidOperationException>(() => fx.Settings.Set("Workspace", "sidebar.mode", "auto", snapshot.Revision));
    }

    [Fact]
    public void Saving_rejects_stale_revision_security_keys_and_session_hiding_without_writes()
    {
        using var fx = new Fixture(); var revision = fx.Settings.Read().Revision;
        fx.Settings.Set("Workspace", "widgets.core.plan.visible", "false", revision);
        var current = fx.Settings.Read();
        Assert.Throws<InvalidOperationException>(() => fx.Settings.Set("Workspace", "sidebar.mode", "tabbed", revision));
        Assert.Throws<ArgumentException>(() => fx.Settings.Set("Project", "sidebar.mode", "auto", current.Revision));
        Assert.Throws<ArgumentException>(() => fx.Settings.Set("User", "budget.session", "0", current.Revision));
        Assert.Throws<ArgumentException>(() => fx.Settings.Set("Workspace", "widgets.core.session.visible", "false", current.Revision));
        Assert.Throws<ArgumentException>(() => fx.Settings.Set("Workspace", "sidebar.tabbedMinWidth", "121", current.Revision));
        Assert.Equal(current.Revision, fx.Settings.Read().Revision);
        var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink());
        server.ConfigureWorkspaceRoot(fx.Workspace); server.ConfigureSidebarPaths(fx.Paths);
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sidebar.configure\",\"scope\":\"User\",\"key\":\"sidebar.mode\",\"value\":\"overlay\"," + JsonObj.Field("revision", current.Revision) + "}");
        Assert.Equal("error", server.Send(command, TestContext.Current.CancellationToken).Status);
        Assert.Equal("ok", server.SendUserAction(command, TestContext.Current.CancellationToken).Status);
        Assert.Equal("overlay", SidebarPreferencesJson.Decode(server.Query("sidebarSettings", TestContext.Current.CancellationToken)!.Json)!.Preferences.Mode);
    }

    [Fact]
    public void Responsive_composition_has_real_tabs_fixed_session_and_hidden_navigation()
    {
        var widgets = new ISidebarWidget[]
        {
            new SessionSidebarWidget(new("title", "act")),
            new FixtureWidget("normal", "NORMAL", 70, Enumerable.Range(0, 8).Select(i => new WidgetRowModel("row " + i)).ToArray()),
            new FixtureWidget("alert", "ALERT", 10, [new("waiting", ThemeRole.Attention), new("failed", ThemeRole.Error), new("detail"), new("detail2")]),
        };
        var settings = new SidebarPreferences(Widgets: new Dictionary<string, WidgetPreferences> { ["normal"] = new("false") });
        var tabbed = SidebarComposition.Build(widgets, ClientState.Empty(), settings, TuiLayoutMode.Tabbed, 20, "normal");
        Assert.Equal("core.session", Assert.Single(tabbed.Pinned).Widget.Id);
        Assert.Equal("normal", Assert.Single(tabbed.Body).Widget.Id); Assert.Contains("alert", tabbed.AttentionIds);
        Assert.Equal(2, tabbed.Navigation.Count);
        var resized = SidebarComposition.Build(widgets, ClientState.Empty(), new(), TuiLayoutMode.Stacked, 6, tabbed.SelectedId);
        Assert.Equal("normal", resized.SelectedId); Assert.True(resized.Collapsed > 0);
        Assert.Equal("alert", resized.Body[0].Widget.Id);
        Assert.Contains(Assert.IsType<ListWidgetModel>(resized.Body[0].Model).Rows, r => r.Role == ThemeRole.Attention);
        Assert.Contains(Assert.IsType<ListWidgetModel>(resized.Body[0].Model).Rows, r => r.Text == "failed" && r.Role == ThemeRole.Error);
        Assert.Equal(TuiLayoutMode.Tabbed, TuiLayoutModel.ForWidth(105).Mode);
        Assert.Equal(TuiLayoutMode.Stacked, TuiLayoutModel.ForWidth(120).Mode);
        Assert.Equal(TuiLayoutMode.Overlay, TuiLayoutModel.ForWidth(80, preferences: new(Mode: "stacked")).Mode);
        Assert.True(TuiLayoutModel.ForWidth(10).SidebarWidth <= 10);
        Assert.Equal(TuiLayoutMode.Tabbed, TuiLayoutModel.ForWidth(140, preferences: new(StackedMinWidth: 150)).Mode);
    }
    private sealed record FixtureWidget(string Id, string Title, int DefaultPriority, IReadOnlyList<WidgetRowModel> Rows) : ISidebarWidget
    {
        public ThemeRole Accent => ThemeRole.Info;
        public WidgetRelevance Evaluate(ClientState state) => Rows.Any(r => r.Role == ThemeRole.Attention) ? WidgetRelevance.Attention : WidgetRelevance.Normal;
        public WidgetModel Build(ClientState state, WidgetSize size) => new ListWidgetModel(Title, Rows);
    }
}
