using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>ADR-0022 §4 / ADR-0039 §5: la configuración se resuelve por scope y las claves <c>locked</c> mandan.</summary>
public sealed class ScopedConfigurationTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "omni-scoped-config-" + Guid.NewGuid().ToString("N"));
        internal readonly DefaultPlatformPaths Paths;
        internal readonly string Workspace;

        internal Fixture()
        {
            Paths = new(Path.Combine(Root, "data"));
            Workspace = Path.Combine(Root, "workspace");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Paths.ConfigDirectory);
            Directory.CreateDirectory(Path.Combine(Workspace, ".omnicore"));
            Directory.CreateDirectory(OmniHost.WorkspaceDataDirectory(Paths, Workspace));
        }

        internal void User(string yaml) => File.WriteAllText(Path.Combine(Paths.ConfigDirectory, "settings.yaml"), yaml);
        internal void Project(string yaml) => File.WriteAllText(Path.Combine(Workspace, ".omnicore", "settings.yaml"), yaml);
        internal void Local(string yaml) =>
            File.WriteAllText(Path.Combine(OmniHost.WorkspaceDataDirectory(Paths, Workspace), "settings.yaml"), yaml);
        internal void Trust() => new WorkspaceTrustStore(Paths).SetTrusted(Workspace, true);

        internal WorkspaceConfigurationResult Load(bool trusted = true) =>
            ScopedSettingsLoader.Load(Paths, Workspace, trusted, _ => true);

        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public void A_lock_covers_the_key_and_everything_below_it_and_reports_what_it_blocked()
    {
        var resolved = ConfigurationScopeResolver.Resolve(new[]
        {
            new ConfigScopeLayer<string>(ConfigScope.User,
                new Dictionary<string, string> { ["sidebar.mode"] = "user", ["sidebar.visible"] = "user" },
                new HashSet<string> { "sidebar" }),
            new ConfigScopeLayer<string>(ConfigScope.Workspace,
                new Dictionary<string, string> { ["sidebar.mode"] = "workspace", ["theme"] = "workspace" }),
        });

        Assert.Equal("user", resolved.Values["sidebar.mode"]);
        Assert.Equal("workspace", resolved.Values["theme"]);
        var blocked = Assert.Single(resolved.Blocked!);
        Assert.Equal(("sidebar.mode", ConfigScope.Workspace, ConfigScope.User, "sidebar"),
            (blocked.Key, blocked.Scope, blocked.LockedBy, blocked.Lock));
        Assert.Equal(ConfigScope.Workspace, resolved.SourceOf("theme"));
        Assert.False(ConfigurationScopeResolver.Covers("sidebar", "sidebarX.mode"));
    }

    [Fact]
    public void User_lock_wins_over_project_and_workspace_and_without_it_the_most_specific_wins()
    {
        using var fx = new Fixture();
        fx.Trust();
        fx.User("defaultModel: user-model\nlocked: [defaultModel]\n");
        fx.Project("defaultModel: project-model\n");
        fx.Local("defaultModel: workspace-model\n");

        var locked = fx.Load();
        Assert.Equal("user-model", locked.Settings!.DefaultModel);
        Assert.Equal(ConfigScope.User, locked.Scopes!.SourceOf("defaultModel"));
        Assert.Equal(new[] { ConfigScope.Project, ConfigScope.Workspace },
            locked.Scopes.Blocked!.Select(item => item.Scope));

        fx.User("defaultModel: user-model\n");
        var open = fx.Load();
        Assert.Equal("workspace-model", open.Settings!.DefaultModel);
        Assert.Equal(ConfigScope.Workspace, open.Scopes!.SourceOf("defaultModel"));
        Assert.Empty(open.Scopes.Blocked!);
    }

    [Fact]
    public void Leaves_resolve_independently_and_an_untrusted_project_contributes_nothing()
    {
        using var fx = new Fixture();
        fx.User("gates:\n  build: [dotnet, build]\n  test: [dotnet, test, --user]\n");
        fx.Project("gates:\n  test: [dotnet, test, --project]\n");

        var untrusted = fx.Load(trusted: false);
        Assert.Equal(new[] { "dotnet", "test", "--user" }, untrusted.Settings!.Gates!.Test);

        fx.Trust();
        var trusted = fx.Load();
        Assert.Equal(new[] { "dotnet", "build" }, trusted.Settings!.Gates!.Build);
        Assert.Equal(new[] { "dotnet", "test", "--project" }, trusted.Settings.Gates.Test);
        Assert.Equal(ConfigScope.User, trusted.Scopes!.SourceOf("gates.build"));
        Assert.Equal(ConfigScope.Project, trusted.Scopes.SourceOf("gates.test"));

        fx.User("gates:\n  build: [dotnet, build]\n  test: [dotnet, test, --user]\nlocked: [gates.test]\n");
        Assert.Equal(new[] { "dotnet", "test", "--user" }, fx.Load().Settings!.Gates!.Test);
    }

    [Fact]
    public void Locks_are_validated_and_only_the_user_scope_can_declare_them()
    {
        using var fx = new Fixture();
        fx.User("locked: [providers]\n");
        var invalid = Assert.Throws<ConfigValidationException>(() => fx.Load());
        Assert.Contains(invalid.Diagnostics, d => d.KeyPath == "locked.providers");

        fx.User("locked: [defaultModel]\n");
        fx.Trust();
        fx.Project("locked: [defaultModel]\n");
        var repo = Assert.Throws<ConfigValidationException>(() => fx.Load());
        Assert.Contains(repo.Diagnostics, d => d.KeyPath == "locked");

        fx.Project("defaultModel: project-model\n");
        fx.Local("locked: [defaultModel]\n");
        var local = Assert.Throws<ConfigValidationException>(() => fx.Load());
        Assert.Contains(local.Diagnostics, d => d.KeyPath == "locked");
    }

    [Fact]
    public void An_effective_default_model_must_be_a_known_alias_whichever_scope_set_it()
    {
        using var fx = new Fixture();
        fx.User("defaultModel: ghost\n");
        var error = Assert.Throws<ConfigValidationException>(() =>
            ScopedSettingsLoader.Load(fx.Paths, fx.Workspace, false, alias => alias != "ghost"));
        Assert.Contains(error.Diagnostics, d => d.KeyPath == "defaultModel");
    }

    [Fact]
    public void Sidebar_preferences_honour_user_locks_when_reading_and_saving()
    {
        using var fx = new Fixture();
        fx.Trust();
        fx.User("sidebar: { mode: stacked }\nlocked: [sidebar.mode]\n");
        fx.Project("sidebar: { mode: overlay, visible: false }\n");
        var settings = new SidebarConfiguration(fx.Paths, fx.Workspace);

        var snapshot = settings.Read();
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal("stacked", snapshot.Preferences.Mode);
        Assert.Equal("User", snapshot.Sources!["sidebar.mode"]);
        Assert.False(snapshot.Preferences.Visible); // la hoja sin lock sigue su resolución normal
        Assert.Equal("Project", snapshot.Sources["sidebar.visible"]);

        var locked = Assert.Throws<InvalidOperationException>(() =>
            settings.Set("Workspace", "sidebar.mode", "tabbed", snapshot.Revision));
        Assert.Contains("locked", locked.Message, StringComparison.Ordinal);
        Assert.Equal("stacked", settings.Read().Preferences.Mode);

        settings.Set("Workspace", "sidebar.visible", "true", snapshot.Revision);
        Assert.True(settings.Read().Preferences.Visible);
    }
}
