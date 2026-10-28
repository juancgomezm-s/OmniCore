namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

public sealed class PreferencesOperationsTests
{
    [Fact]
    public void New_policy_security_and_diagnostics_copy_is_complete_in_every_supported_locale()
    {
        foreach (var locale in UiLocaleCodes.Supported)
        {
            Check(PolicyPreferencesResources.Keys, locale);
            Check(SecurityPreferencesResources.Keys, locale);
            Check(DiagnosticsPreferencesResources.Keys, locale);
        }
    }

    [Fact]
    public void Shell_ceiling_refusal_is_localized_and_survives_redaction()
    {
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var message = new Localization(locale)
                .ResolveWire("session_security.shell_ceiling_refused(category=ObserveOnly)");
            Assert.Contains("ObserveOnly", message, StringComparison.Ordinal);
            Assert.DoesNotContain("session_security.shell_ceiling_refused", message, StringComparison.Ordinal);
            Assert.DoesNotContain("[REDACTED]", OmniCliRuntime.RedactSensitive(message), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Policy_preferences_preserve_existing_user_settings_and_use_revision_checks()
    {
        using var fx = new Fixture();
        var settings = Path.Combine(fx.Paths.ConfigDirectory, "settings.yaml");
        File.WriteAllText(settings, "ui:\n  locale: fr\nsidebar:\n  visible: true\n");
        var host = new PolicyPreferencesHost(fx.Paths, fx.Workspace);
        var initial = host.Read();
        Assert.True(initial.Available);

        var saved = host.SaveUserSettings(initial.Revision, null, 2.5m, 20m);
        Assert.True(saved.Available);
        var yaml = File.ReadAllText(settings);
        Assert.Contains("locale: fr", yaml, StringComparison.Ordinal);
        Assert.Contains("sidebar:", yaml, StringComparison.Ordinal);
        Assert.Contains("session: 2.5", yaml, StringComparison.Ordinal);
        Assert.Contains("daily: 20", yaml, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => host.SaveUserSettings(initial.Revision, null, null, null));

        var beforeRouting = host.Read();
        var routing = host.SaveRouting(beforeRouting.Revision,
            new ModelRoutingPreferences([], [], [], [], [], true, "ask", []));
        Assert.True(routing.Available);
        Assert.True(routing.Routing.PreferLocal);
        Assert.Equal("ask", routing.Routing.EscalationMode);
        var modelsYaml = File.ReadAllText(Path.Combine(fx.Paths.ConfigDirectory, "models.yaml"));
        Assert.Contains("models:", modelsYaml, StringComparison.Ordinal);
        Assert.Contains("routing:", modelsYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Security_preferences_edit_exact_user_rules_and_workspace_trust_with_stale_guards()
    {
        using var fx = new Fixture();
        var host = new SecurityPreferencesHost(fx.Paths, fx.Workspace);
        var initial = host.Read();
        var saved = host.SaveUserPermissions(initial.Revision, "Balanced",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["file.read"] = "Deny" });
        Assert.Equal("Balanced", saved.Profile);
        Assert.Contains(saved.Rules, rule => rule is { ToolId: "file.read", Decision: "Deny" });
        Assert.Throws<InvalidOperationException>(() => host.SaveUserPermissions(initial.Revision, "Conservative", new Dictionary<string, string>()));
        Assert.False(saved.WorkspaceTrusted);

        var trusted = host.SetWorkspaceTrust(expectedCurrent: false, trusted: true);
        Assert.True(trusted.WorkspaceTrusted);
        Assert.Throws<InvalidOperationException>(() => host.SetWorkspaceTrust(expectedCurrent: false, trusted: false));
        Assert.False(host.SetWorkspaceTrust(expectedCurrent: true, trusted: false).WorkspaceTrusted);
    }

    [Fact]
    public void Diagnostics_queries_are_offline_and_do_not_create_workspace_data()
    {
        using var fx = new Fixture();
        var workspaceId = OmniCore.Domain.WorkspaceId.Of(
            OmniCore.Host.ProjectIdentity.CanonicalWorkspacePath(fx.Workspace)).ToString();
        var workspaceData = Path.Combine(fx.Paths.DataDirectory, "workspaces", workspaceId);
        var snapshot = new DiagnosticsPreferencesHost(fx.Paths, fx.Workspace).Read();
        Assert.Equal(fx.Workspace, snapshot.WorkspaceRoot);
        Assert.False(snapshot.JournalPresent);
        Assert.False(Directory.Exists(workspaceData));
        var host = new DiagnosticsPreferencesHost(fx.Paths, fx.Workspace);
        var report = host.VerifyJournal(TestContext.Current.CancellationToken);
        Assert.False(report.JournalOpened);
        Assert.False(Directory.Exists(workspaceData));
        var preview = host.PreviewWorkspaceGarbageCollection(TestContext.Current.CancellationToken);
        Assert.True(preview.DryRun);
        Assert.False(Directory.Exists(workspaceData));
        var collection = host.CollectWorkspaceGarbage(fx.Workspace, TestContext.Current.CancellationToken);
        Assert.False(collection.DryRun);
        Assert.False(Directory.Exists(workspaceData));
        Assert.False(Directory.Exists(Path.Combine(fx.Paths.ConfigDirectory, "credentials")));
    }

    private static void Check(IReadOnlyCollection<string> keys, string locale)
    {
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in keys)
        {
            var value = new Localization(locale).ResolveDetailed(key);
            Assert.False(value.UsedFallback, $"Missing {locale} resource {key}");
            Assert.Equal(locale, value.ResourceLocale);
            Assert.False(string.IsNullOrWhiteSpace(value.Text));
            Assert.NotEqual(key, value.Text);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omnicore-preference-ops-" + Guid.NewGuid().ToString("N"));
        internal DefaultPlatformPaths Paths { get; }
        internal string Workspace { get; }
        internal Fixture()
        {
            Paths = new DefaultPlatformPaths(Path.Combine(_root, "data"));
            Workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Paths.ConfigDirectory);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
