using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class TuiLocalEngineDeletionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "omni-engine-delete-" + Guid.NewGuid().ToString("N"));
    private string Providers => Path.Combine(_directory, "providers.yaml");
    private string Models => Path.Combine(_directory, "models.yaml");

    [Fact]
    public void Delete_removes_only_explicit_provider_and_preserves_models_and_unrelated_yaml()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Providers, """
            providers:
              local:
                family: OpenAiChatCompatible
                host: attach
                baseUrl: http://127.0.0.1:9090/v1
                auth: none
              remote:
                family: AnthropicMessages
                baseUrl: https://api.example.test
                authRef: credentials/remote
                billingMode: MeteredCurrency
            """);
        const string models = "models:\n  unrelated: { provider: remote, context: 12000 }\n";
        File.WriteAllText(Models, models);
        var host = new TuiLocalEngineHost(_directory);
        var snapshot = host.Read(CancellationToken.None);

        var updated = host.Delete("local", snapshot.Revision, CancellationToken.None);

        Assert.DoesNotContain(updated.Engines, row => row.ProviderId == "local");
        Assert.Contains("remote:", File.ReadAllText(Providers));
        Assert.DoesNotContain("host: attach", File.ReadAllText(Providers));
        Assert.Equal(models, File.ReadAllText(Models));
        Assert.NotEqual(snapshot.Revision, updated.Revision);
    }

    [Fact]
    public void Delete_rejects_models_that_reference_the_local_engine_without_mutating_files()
    {
        WriteLocalProvider();
        const string models = "models:\n  selected: { provider: engine, context: 8192, maxOutput: 2048 }\n  other: { provider: cloud, context: 12000, maxOutput: 2048 }\n";
        File.WriteAllText(Models, models);
        var host = new TuiLocalEngineHost(_directory);
        var snapshot = host.Read(CancellationToken.None);
        var original = File.ReadAllText(Providers);

        var error = Assert.Throws<LocalEngineConfigurationException>(() =>
            host.Delete("engine", snapshot.Revision, CancellationToken.None));

        Assert.Equal("localEngines.error.modelsReferencingEngine", error.MessageKey);
        Assert.Equal(original, File.ReadAllText(Providers));
        Assert.Equal(models, File.ReadAllText(Models));
    }

    [Fact]
    public void Delete_rejects_stale_revision_and_managed_process_still_owned_and_running()
    {
        WriteLocalProvider(mode: "managed");
        var supervisor = new FakeSupervisor(new("engine", "managed", true, true, "localEngines.runtime.managedRunning"));
        var host = new TuiLocalEngineHost(_directory, supervisor);
        var snapshot = host.Read(CancellationToken.None);
        Assert.Empty(snapshot.Diagnostics);
        File.AppendAllText(Providers, "\n# concurrent update\n");
        var latest = File.ReadAllText(Providers);
        AssertKey("revisionConflict", () => host.Delete("engine", snapshot.Revision, CancellationToken.None));
        Assert.Equal(latest, File.ReadAllText(Providers));

        var current = host.Read(CancellationToken.None);
        Assert.Empty(current.Diagnostics);
        AssertKey("managedRunning", () => host.Delete("engine", current.Revision, CancellationToken.None));
        Assert.Equal(latest, File.ReadAllText(Providers));
        Assert.Contains("managed:", File.ReadAllText(Providers));
    }

    private void WriteLocalProvider(string mode = "attach")
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Providers, mode == "managed" ? """
            providers:
              engine:
                family: OpenAiChatCompatible
                host: managed
                baseUrl: auto
                auth: none
                billingMode: Local
                managed: { executable: fixture-only.exe, args: [] }
              cloud:
                family: AnthropicMessages
                baseUrl: https://api.example.invalid
                auth: none
            """ : """
            providers:
              engine:
                family: OpenAiChatCompatible
                host: attach
                baseUrl: http://127.0.0.1:9090/v1
                auth: none
                billingMode: Local
              cloud:
                family: AnthropicMessages
                baseUrl: https://api.example.invalid
                auth: none
            """);
    }

    private static void AssertKey(string code, Action action) =>
        Assert.Equal("localEngines.error." + code,
            Assert.Throws<LocalEngineConfigurationException>(action).MessageKey);

    private sealed class FakeSupervisor(LocalEngineRuntimeStatus status) : ITuiLocalEngineSupervisor
    {
        public IReadOnlyList<LocalEngineRuntimeStatus> Read(CancellationToken cancellationToken) => [status];
        public LocalEngineRuntimeActionResult EnsureReady(string providerId, CancellationToken cancellationToken) =>
            new(true, "localEngines.runtime.managedRunning");
        public LocalEngineRuntimeActionResult Stop(string providerId, CancellationToken cancellationToken) =>
            new(true, "localEngines.runtime.managedStopped");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
