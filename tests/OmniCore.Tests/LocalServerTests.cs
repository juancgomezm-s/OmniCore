using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>ADR-0011 §4: <c>LocalModelHost</c> en modo attach y managed, cableado al runtime de producción.</summary>
public sealed class LocalServerTests
{
    private sealed class FakeProcessRuntime : IProcessRuntime
    {
        private readonly HashSet<int> _alive = [];
        private int _next = 100;
        internal List<ProcessLaunch> Launches { get; } = [];

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            var pid = _next++;
            _alive.Add(pid);
            return new ProcessHandle(pid, this);
        }

        public void CancelTree(ProcessHandle handle) => _alive.Remove(handle.Pid);
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, null, null, false);
        public bool IsAlive(ProcessHandle handle) => _alive.Contains(handle.Pid);
        internal void Crash(int pid) => _alive.Remove(pid);
        internal int AliveCount => _alive.Count;
        internal int LastPid => _next - 1;
    }

    private static ProviderDescriptor Provider(LocalHostConfig? host, string baseUrl = "http://127.0.0.1:8080/v1",
        string id = "local") =>
        new(id, ProviderFamily.OpenAiChatCompatible, baseUrl, AuthConfig.None(), true, true, true)
        { BillingMode = BillingMode.Local, LocalHost = host };

    private static LocalHostConfig Managed(int fixedPort = 0, TimeSpan? readiness = null) =>
        new(LocalHostMode.Managed, "llama-server", ["--port", "{port}", "--api-key", "{apiKey}", "-m", "model.gguf"],
            "", fixedPort, readiness);

    [Fact]
    public void Attach_checks_the_already_running_server_and_fails_before_the_turn_when_it_is_down()
    {
        var probed = new List<string>();
        using var down = new LocalServerSupervisor(reachable: url => { probed.Add(url); return false; });
        var error = Assert.Throws<LocalServerUnavailableException>(() =>
            down.Ensure(Provider(new LocalHostConfig(LocalHostMode.Attach)), "http://127.0.0.1:8080/v1"));
        Assert.Equal("unreachable", error.Reason);
        Assert.Equal("localServer.unreachable", error.UserMessage.Key);
        Assert.Equal(new[] { "http://127.0.0.1:8080/v1" }, probed);

        using var up = new LocalServerSupervisor(reachable: _ => true);
        var endpoint = up.Ensure(Provider(new LocalHostConfig(LocalHostMode.Attach)), "http://127.0.0.1:8080/v1");
        Assert.Equal(new LocalServerEndpoint("http://127.0.0.1:8080/v1", null, false), endpoint);
    }

    [Fact]
    public void Implicit_attach_is_not_probed_before_each_turn()
    {
        using var supervisor = new LocalServerSupervisor(reachable: _ => throw new InvalidOperationException("probe"));
        var endpoint = supervisor.Ensure(Provider(new LocalHostConfig(LocalHostMode.Attach, Declared: false)),
            "http://127.0.0.1:8080/v1");
        Assert.Equal("http://127.0.0.1:8080/v1", endpoint.BaseUrl);
    }

    [Fact]
    public void Providers_without_a_local_server_are_not_probed_or_launched()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, _ => throw new InvalidOperationException("probe"),
            (_, _) => throw new InvalidOperationException("ready"));
        var endpoint = supervisor.Ensure(Provider(null, "https://api.example.test/v1"), "https://api.example.test/v1");
        Assert.Equal("https://api.example.test/v1", endpoint.BaseUrl);
        Assert.Empty(process.Launches);
    }

    [Fact]
    public void Managed_starts_once_with_ephemeral_port_and_key_and_reuses_the_server_between_turns()
    {
        var process = new FakeProcessRuntime();
        var readyCalls = new List<(string Url, string? Key)>();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (url, key) =>
        {
            readyCalls.Add((url, key));
            return true;
        });
        var provider = Provider(Managed(), LocalHostConfig.ManagedLogicalBaseUrl);

        var first = supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl);
        var second = supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl);

        Assert.Single(process.Launches);
        Assert.Equal(first, second);
        Assert.True(first.Managed);
        Assert.NotEqual(LocalHostConfig.ManagedLogicalBaseUrl, first.BaseUrl);
        var port = new Uri(first.BaseUrl).Port;
        Assert.InRange(port, 1, 65535);
        Assert.Contains(port.ToString(), process.Launches[0].Args);
        Assert.Equal(48, first.ApiKey!.Length);
        Assert.Equal(first.ApiKey, process.Launches[0].Environment["LLAMA_API_KEY"]);
        // La API key viaja solo por el entorno del proceso (ADR-0011 §4), nunca por argv.
        Assert.DoesNotContain(process.Launches[0].Args, argument => argument.Contains(first.ApiKey, StringComparison.Ordinal));
        Assert.Equal((first.BaseUrl, first.ApiKey), readyCalls[0]);
        Assert.True(process.Launches[0].DiscardOutput);
        Assert.True(supervisor.IsManagedRunning("local"));
    }

    [Fact]
    public void An_explicit_port_in_the_provider_endpoint_pins_the_managed_server_port()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => true);
        var provider = Provider(Managed(fixedPort: 4242), "http://127.0.0.1:4242/v1");

        var endpoint = supervisor.Ensure(provider, "http://127.0.0.1:4242/v1");

        Assert.Equal("http://127.0.0.1:4242/v1", endpoint.BaseUrl);
        Assert.Contains("4242", process.Launches[0].Args);
    }

    [Fact]
    public void A_crashed_managed_server_is_restarted_with_backoff_until_the_start_limit()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => true,
            restartBackoff: TimeSpan.FromMilliseconds(150));
        var provider = Provider(Managed(), LocalHostConfig.ManagedLogicalBaseUrl);

        var first = supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl);
        process.Crash(process.LastPid);
        Assert.False(supervisor.IsManagedRunning("local"));
        var clock = Stopwatch.StartNew();
        var second = supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(140), "el reinicio espera el backoff configurado");
        Assert.Equal(2, process.Launches.Count);
        Assert.NotEqual(first.ApiKey, second.ApiKey);

        process.Crash(process.LastPid);
        supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl);
        Assert.Equal(LocalServerSupervisor.MaxManagedStarts, process.Launches.Count);

        process.Crash(process.LastPid);
        var error = Assert.Throws<LocalServerUnavailableException>(() =>
            supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl));
        Assert.Equal("restartLimit", error.Reason);
        Assert.Equal(LocalServerSupervisor.MaxManagedStarts, process.Launches.Count);
    }

    [Fact]
    public void A_server_that_never_becomes_ready_is_stopped_after_the_configured_timeout()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => false);
        var provider = Provider(Managed(readiness: TimeSpan.FromSeconds(1)), LocalHostConfig.ManagedLogicalBaseUrl);

        var clock = Stopwatch.StartNew();
        var error = Assert.Throws<LocalServerUnavailableException>(() =>
            supervisor.Ensure(provider, LocalHostConfig.ManagedLogicalBaseUrl));

        Assert.Equal("startFailed", error.Reason);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.AliveCount); // no deja un servidor huérfano
    }

    [Fact]
    public void A_process_that_dies_while_starting_is_reported_as_crashed()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) =>
        {
            process.Crash(process.LastPid);
            return false;
        });
        var error = Assert.Throws<LocalServerUnavailableException>(() =>
            supervisor.Ensure(Provider(Managed(), LocalHostConfig.ManagedLogicalBaseUrl),
                LocalHostConfig.ManagedLogicalBaseUrl));
        Assert.Equal("crashed", error.Reason);
    }

    [Fact]
    public void Disposing_the_supervisor_stops_the_managed_server()
    {
        var process = new FakeProcessRuntime();
        var supervisor = new LocalServerSupervisor(() => process, ready: (_, _) => true);
        supervisor.Ensure(Provider(Managed(), LocalHostConfig.ManagedLogicalBaseUrl), LocalHostConfig.ManagedLogicalBaseUrl);
        Assert.Equal(1, process.AliveCount);

        supervisor.Dispose();

        Assert.Equal(0, process.AliveCount);
    }

    [Fact]
    public void An_explicit_endpoint_override_is_attach_not_a_second_managed_server()
    {
        var process = new FakeProcessRuntime();
        using var supervisor = new LocalServerSupervisor(() => process, _ => true, (_, _) => true);
        var endpoint = supervisor.Ensure(Provider(Managed(), LocalHostConfig.ManagedLogicalBaseUrl),
            "http://127.0.0.1:9999/v1");
        Assert.Equal(new LocalServerEndpoint("http://127.0.0.1:9999/v1", null, false), endpoint);
        Assert.Empty(process.Launches);
    }

    [Fact]
    public void Providers_yaml_declares_managed_attach_and_default_hosts()
    {
        var registry = new ConfigLoader().BuildRegistry("""
            providers:
              managed-auto:
                family: OpenAiChatCompatible
                billingMode: Local
                host: managed
                baseUrl: auto
                managed:
                  executable: llama-server
                  args: ["--port", "{port}"]
                  workingDirectory: models
                  readinessTimeoutSeconds: 45
              managed-fixed:
                family: OpenAiChatCompatible
                billingMode: Local
                host: managed
                baseUrl: http://127.0.0.1:8123/v1
                managed: { executable: llama-server }
              declared-attach:
                family: OpenAiChatCompatible
                host: attach
                baseUrl: http://192.168.1.5:8080/v1
              implicit-attach:
                family: OpenAiChatCompatible
                billingMode: Local
                baseUrl: http://127.0.0.1:8081/v1
              remote:
                family: OpenAiChatCompatible
                billingMode: MeteredCurrency
                baseUrl: https://api.example.test/v1
                authRef: remote
            """, null);

        var auto = registry.Provider("managed-auto")!;
        Assert.Equal(LocalHostConfig.ManagedLogicalBaseUrl, auto.BaseUrl);
        Assert.Equal(new[] { "--port", "{port}" }, auto.LocalHost!.Args);
        Assert.Equal((LocalHostMode.Managed, "llama-server", "models", 0, TimeSpan.FromSeconds(45)),
            (auto.LocalHost.Mode, auto.LocalHost.Executable, auto.LocalHost.WorkingDirectory, auto.LocalHost.FixedPort,
                auto.LocalHost.EffectiveReadinessTimeout));
        var pinned = registry.Provider("managed-fixed")!;
        Assert.Equal("http://127.0.0.1:8123/v1", pinned.BaseUrl);
        Assert.Equal(8123, pinned.LocalHost!.FixedPort);
        Assert.Equal(LocalHostConfig.DefaultReadinessTimeout, pinned.LocalHost.EffectiveReadinessTimeout);
        Assert.True(registry.Provider("declared-attach")!.LocalHost is { Mode: LocalHostMode.Attach, Declared: true });
        // Sin `host`, un provider local es attach implícito: doctor lo diagnostica, el Turn no lo sondea.
        Assert.True(registry.Provider("implicit-attach")!.LocalHost is { Mode: LocalHostMode.Attach, Declared: false });
        Assert.Null(registry.Provider("remote")!.LocalHost);
    }

    [Theory]
    [InlineData("host: sideways\n    baseUrl: http://127.0.0.1:8080/v1", "providers.p.host")]
    [InlineData("host: managed\n    baseUrl: auto", "providers.p.managed")]
    [InlineData("host: managed\n    baseUrl: auto\n    managed: { args: [x] }", "providers.p.managed.executable")]
    [InlineData("host: managed\n    baseUrl: auto\n    managed: { executable: s, args: oops }", "providers.p.managed.args")]
    [InlineData("host: managed\n    baseUrl: auto\n    managed: { executable: s, readinessTimeoutSeconds: 0 }",
        "providers.p.managed.readinessTimeoutSeconds")]
    [InlineData("host: managed\n    baseUrl: http://example.com:8080/v1\n    managed: { executable: s }", "providers.p.baseUrl")]
    [InlineData("host: attach\n    baseUrl: http://127.0.0.1:8080/v1\n    managed: { executable: s }", "providers.p.managed")]
    [InlineData("host: managed\n    family: AnthropicMessages\n    baseUrl: auto\n    managed: { executable: s }",
        "providers.p.host")]
    public void Invalid_host_configuration_is_reported_with_its_key_path(string provider, string keyPath)
    {
        var yaml = "providers:\n  p:\n    " + (provider.Contains("family:", StringComparison.Ordinal) ? "" : "family: OpenAiChatCompatible\n    ")
            + provider + "\n";
        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().BuildRegistry(yaml, null));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.KeyPath == keyPath);
    }

    [Fact]
    public void Launching_with_discarded_output_does_not_block_a_server_that_logs_heavily()
    {
        if (!OperatingSystem.IsWindows()) return;
        var flag = Path.Combine(Path.GetTempPath(), "omni-flood-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runtime = OmniCore.Execution.SystemProcessRuntime.Instance();
            var handle = runtime.Launch(new ProcessLaunch("powershell.exe",
                ["-NoProfile", "-Command", "[Console]::Out.Write('x' * 4000000); [Console]::Error.Write('y' * 4000000); "
                    + "New-Item -ItemType File -Path '" + flag + "' | Out-Null; Start-Sleep -Seconds 30"],
                "", new Dictionary<string, string>(), false) { DiscardOutput = true }, CancellationToken.None);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!File.Exists(flag) && DateTime.UtcNow < deadline) Thread.Sleep(100);
                Assert.True(File.Exists(flag), "el proceso se bloqueó escribiendo en una tubería que nadie drena");
            }
            finally { runtime.CancelTree(handle); }
        }
        finally { if (File.Exists(flag)) File.Delete(flag); }
    }
}
