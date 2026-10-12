namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

public sealed class PolicyPreferencesAliasAndRuntimeLeaseTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "omnicore-policy-runtime-" + Guid.NewGuid().ToString("N"));
        internal readonly string Workspace;
        internal readonly DefaultPlatformPaths Paths;

        internal Fixture()
        {
            Workspace = Path.Combine(Root, "workspace");
            Paths = new DefaultPlatformPaths(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Paths.ConfigDirectory);
        }

        internal void ConfigureManagedProvider()
        {
            File.WriteAllText(Path.Combine(Paths.ConfigDirectory, "providers.yaml"), """
                providers:
                  engine:
                    family: OpenAiChatCompatible
                    billingMode: Local
                    host: managed
                    baseUrl: auto
                    managed:
                      executable: fixture-engine
                      args: ["--model", "fixture.gguf"]
                """);
            File.WriteAllText(Path.Combine(Paths.ConfigDirectory, "models.yaml"), """
                models:
                  fixture-model:
                    provider: engine
                    context: 8192
                    maxOutput: 1024
                """);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeProcessRuntime : IProcessRuntime
    {
        private readonly HashSet<int> _alive = [];
        private int _next = 100;
        internal int LaunchCount { get; private set; }

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            LaunchCount++;
            var pid = _next++;
            _alive.Add(pid);
            return new ProcessHandle(pid, this);
        }

        public void CancelTree(ProcessHandle handle) => _alive.Remove(handle.Pid);
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => _alive.Contains(handle.Pid);
    }

    [Theory]
    [InlineData(".omnicore/settings.yaml", "Project")]
    [InlineData("workspace-scope/settings.yaml", "Workspace")]
    public void Policy_preferences_accept_effective_default_model_aliases_from_scoped_settings(string relativeSettings,
        string expectedSource)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "providers.yaml"), """
            providers:
              fixture: { family: OpenAiChatCompatible, baseUrl: https://example.invalid/v1, auth: none }
            """);
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "models.yaml"), """
            models:
              fixture-model:
                provider: fixture
                context: 8192
                maxOutput: 1024
                aliases: [fixture-alias]
            """);

        string settingsPath;
        if (expectedSource == "Project")
        {
            new WorkspaceTrustStore(fixture.Paths).SetTrusted(fixture.Workspace, true);
            settingsPath = Path.Combine(fixture.Workspace, relativeSettings);
        }
        else
        {
            var workspaceData = OmniHost.WorkspaceDataDirectory(fixture.Paths, fixture.Workspace);
            settingsPath = Path.Combine(workspaceData, "settings.yaml");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "defaultModel: fixture-alias\n");

        var snapshot = new PolicyPreferencesHost(fixture.Paths, fixture.Workspace).Read();

        Assert.True(snapshot.Available, string.Join("; ", snapshot.Diagnostics));
        Assert.Equal("fixture-alias", snapshot.EffectiveDefaultModel);
        Assert.Equal(expectedSource, snapshot.DefaultModelSource);
        var diagnostics = new DiagnosticsPreferencesHost(fixture.Paths, fixture.Workspace).Read();
        Assert.Empty(diagnostics.Configuration);
        Assert.Equal("fixture-alias", diagnostics.DefaultModel);
    }

    [Fact]
    public void Managed_start_fails_closed_when_the_shared_lease_is_busy_or_provider_was_deleted()
    {
        using var fixture = new Fixture();
        fixture.ConfigureManagedProvider();
        var selected = OmniHost.LoadUserConfiguration(fixture.Paths).Registry.Provider("engine")!;
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => true);

        using (ProviderConfigurationLease.Acquire(fixture.Paths.ConfigDirectory))
        {
            Assert.Throws<LocalServerConfigurationChangedException>(() =>
                OmniCliRuntime.EnsureConfiguredLocalServer(fixture.Paths, supervisor, selected,
                    selected.BaseUrl, CancellationToken.None));
        }
        Assert.Equal(0, process.LaunchCount);

        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "providers.yaml"), "providers: {}\n");
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "models.yaml"), "models: {}\n");

        var changed = Assert.Throws<LocalServerConfigurationChangedException>(() =>
            OmniCliRuntime.EnsureConfiguredLocalServer(fixture.Paths, supervisor, selected,
                selected.BaseUrl, CancellationToken.None));

        Assert.Equal("engine", changed.ProviderId);
        Assert.Equal(0, process.LaunchCount);
        Assert.False(supervisor.IsManagedOwned("engine"));
    }

    [Fact]
    public void Managed_start_uses_current_configuration_and_retains_process_ownership()
    {
        using var fixture = new Fixture();
        fixture.ConfigureManagedProvider();
        var selected = OmniHost.LoadUserConfiguration(fixture.Paths).Registry.Provider("engine")!;
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => true);
        _ = OmniCliRuntime.EnsureConfiguredLocalServer(fixture.Paths, supervisor, selected,
            selected.BaseUrl, CancellationToken.None);
        Assert.Equal(1, process.LaunchCount);
        Assert.True(supervisor.IsManagedOwned("engine"));
    }
}
