using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M2WiringTests
{
    [Fact]
    public async Task PathBoundaryValidator_rejects_escapes_and_accepts_inside()
    {
        var v = new PathBoundaryValidator();

        Assert.True(v.IsWithin("/ws/a.cs", "/ws"));
        Assert.False(v.IsWithin("/ws/../etc", "/ws"));
        Assert.True(v.IsSafeRelative("src/a.cs"));
        Assert.False(v.IsSafeRelative("../a.cs"));
        Assert.False(v.IsSafeRelative("/abs"));
    }

    [Fact]
    public async Task PathBoundaryValidator_windows_absolutes()
    {
        var v = new PathBoundaryValidator();
        var root = "C:\\Users\\juanc\\source\\repos\\OmniCore";

        Assert.True(v.IsWithin(Path.Combine(root, "README.md"), root), "archivo dentro de la raíz");
        Assert.True(v.IsWithin(Path.Combine(root, "src", "OmniCore.Domain", "Ids.cs"), root), "subdirectorio dentro");
        Assert.False(v.IsWithin("C:\\Windows\\System32\\x.exe", root), "absoluto fuera");
        Assert.False(v.IsWithin(Path.Combine(root, "..", "README.md"), root), "traversal fuera");
    }

    [Fact]
    public async Task ConfigLoader_builds_minimal_registry_by_default()
    {
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(null, null);

        Assert.True(registry.Provider("local") is not null, "El registro mínimo tiene un provider local");
        Assert.True(registry.Model("local-worker") is not null, "El registro mínimo tiene un modelo por defecto");
    }

    [Fact]
    public async Task ConfigLoader_reads_providers_yaml()
    {
        var providers = "providers:\n  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n"
            + "  openrouter: { baseUrl: https://openrouter.ai/api/v1, auth: { apiKey: openrouter-key } }\n";
        var models = "models:\n  qwen-27b: { provider: local }\n  sonnet: { provider: openrouter }\n";
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(providers, models);

        Assert.Equal(AuthKind.None, registry.Provider("local")!.Auth.Kind);
        Assert.Equal(AuthKind.ApiKey, registry.Provider("openrouter")!.Auth.Kind);
        Assert.Equal("openrouter-key", registry.Provider("openrouter")!.Auth.SecretRef);
        Assert.True(registry.Model("qwen-27b") is not null);
        Assert.True(registry.Model("sonnet") is not null);
    }

    [Fact]
    public void Provider_factory_uses_chat_completions_for_declared_compatible_family()
    {
        var descriptor = new ProviderDescriptor("p", OmniCore.Domain.ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:8080/v1", OmniCore.Abstractions.AuthConfig.None(), false, false, false);

        var provider = OmniHost.ConnectProvider(descriptor, descriptor.BaseUrl, "key", "");

        Assert.IsType<OpenAiChatCompatibleProvider>(provider);
    }

    [Theory]
    [InlineData(OmniCore.Domain.ProviderFamily.AnthropicMessages)]
    [InlineData(OmniCore.Domain.ProviderFamily.OpenAIResponses)]
    public void Provider_factory_rejects_unimplemented_families_with_typed_error(
        OmniCore.Domain.ProviderFamily family)
    {
        var descriptor = new ProviderDescriptor("p", family, "https://example.test/v1",
            OmniCore.Abstractions.AuthConfig.None(), false, false, false);

        var error = Assert.Throws<ProviderFamilyNotSupportedException>(() =>
            OmniHost.ConnectProvider(descriptor, descriptor.BaseUrl, "key", ""));

        Assert.Equal(family, error.Family);
        Assert.Equal("provider.familyNotSupported", error.UserMessage.Key);
        Assert.Contains("not implemented", Localization.English().Resolve(error.UserMessage.Key,
            error.UserMessage.Args), StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_marks_unimplemented_provider_family()
    {
        var config = Path.Combine(Path.GetTempPath(), "omnicore-doctor-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        var previous = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            File.WriteAllText(Path.Combine(config, "providers.yaml"),
                "providers:\n  anthropic:\n    family: AnthropicMessages\n    baseUrl: https://api.example.test/v1\n    auth: none\n");
            File.WriteAllText(Path.Combine(config, "models.yaml"),
                "models:\n  claude-test:\n    provider: anthropic\n");
            var output = new List<string>();

            var exit = OmniCliRuntime.Doctor("en", output.Add, (key, args) => Localization.English().Resolve(key, args));

            Assert.True(exit == 0, string.Join(Environment.NewLine, output));
            Assert.Contains(output, line => line.Contains("AnthropicMessages", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("not implemented yet", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, previous);
            try { Directory.Delete(config, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Registry_without_models_throws_NoModelConfigured()
    {
        var ex = Assert.Throws<NoModelConfiguredException>(() => new ModelRegistry().ResolveDefault());
        Assert.Equal("models.noneConfigured", ex.UserMessage.Key);
    }

    [Fact]
    public void Registry_with_one_model_resolves_it()
    {
        var registry = new ModelRegistry().AddModel(new ModelDefinition("m1", "p", 8192, 8192, 4096));
        Assert.Equal("m1", registry.ResolveDefault().Id);
    }

    [Fact]
    public async Task PlanMode_run_emits_plan_approval_and_switches_to_act()
    {
        // ADR-0035 §4: PLAN → ACT en el mismo Run via PlanApproval + RunModeChanged.
        var codecs = OmniCore.Infrastructure.EventCodecs.Create();
        var store = new OmniCore.Infrastructure.InMemoryEventStore();
        var engine = new OmniCore.Engine.SimulationEngine(store, codecs, new OmniCore.Infrastructure.InMemoryAuditSink());
        var scenario = OmniCore.Host.Scenarios.MultiItemPlan();
        var planMode = new OmniCore.Engine.SimulationScenario(
            scenario.Name + "-plan", OmniCore.Domain.RunMode.Plan, scenario.Input,
            scenario.Plan, scenario.Tasks, scenario.Turns, scenario.Permissions,
            "Completed", scenario.ExpectedPlan, null) { PlanApproval = "approve_execute" };

        var result = engine.Execute(planMode, TestContext.Current.CancellationToken);

        var types = store.ReadFrom(result.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.Contains("interaction.requested", types);
        Assert.Contains("interaction.resolved", types); // la aprobación la da el usuario, no el runtime
        Assert.Contains("run.mode_changed", types);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ContextMaterializer_orders_and_counts_with_working_state_last()
    {
        // ADR-0042 §2: el WorkingState va al final y el snapshot registra tokens.
        var counter = new FakeTokenCounter();
        var contributors = new OmniCore.Context.IContextContributor[] {
            new OmniCore.Context.SystemPromptContributor("system prompt"),
            new OmniCore.Context.SessionConversationContributor(new[] {
                new OmniCore.Context.ConversationContextEntry("conversation-000000", OmniCore.Domain.ContextItemKind.UserMessage,
                    "pregunta", true),
            }),
            new OmniCore.Context.WorkingStateContributor("Plan rev.1 - Objetivo: x\n→ tarea"),
        };
        var materializer = new OmniCore.Context.ContextMaterializer(counter, contributors);
        var request = new OmniCore.Context.MaterializeRequest(
            OmniCore.Domain.SessionId.New(), OmniCore.Domain.RunId.New(), null, null, null, 1,
            new OmniCore.Domain.ExecutionFingerprint("k", "h", "t", "c", "o", "b"));

        var snapshot = materializer.Materialize(request, TestContext.Current.CancellationToken);

        Assert.True(snapshot.Items.Count >= 1, "Hay al menos el WorkingState");
        Assert.True(snapshot.Fingerprint.ModelKey == "k");
        Assert.True(snapshot.TokenCount > 0, "El conteo registra tokens");
        Assert.Equal("system-prompt", snapshot.Items[0].Id);
        Assert.Equal("working-state", snapshot.Items[^1].Id);
    }

    [Fact]
    public void Execution_fingerprint_is_sha256_and_changes_with_effective_inputs()
    {
        var baseline = new OmniCore.Domain.ExecutionFingerprint("model-a", "harness-a", "tools-a",
            "context-a", "overrides-a", "build-a");

        Assert.Matches("^[0-9a-f]{64}$", baseline.Hash());
        Assert.NotEqual(baseline.Hash(), new OmniCore.Domain.ExecutionFingerprint("model-b", "harness-a",
            "tools-a", "context-a", "overrides-a", "build-a").Hash());
        Assert.NotEqual(baseline.Hash(), new OmniCore.Domain.ExecutionFingerprint("model-a", "harness-b",
            "tools-a", "context-a", "overrides-a", "build-a").Hash());
        Assert.NotEqual(baseline.Hash(), new OmniCore.Domain.ExecutionFingerprint("model-a", "harness-a",
            "tools-b", "context-a", "overrides-a", "build-a").Hash());
    }

    [Fact]
    public async Task SystemProcessRuntime_launch_wait_capture_and_cancel()
    {
        // IProcessRuntime mínimo (ADR-0038 §2): lanzar, esperar, capturar y cancelar.
        var runtime = SystemProcessRuntime.Instance();
        var launch = new ProcessLaunch("cmd.exe", ["/c", "echo hola-proceso"], "", new Dictionary<string, string>(), true);
        var handle = runtime.Launch(launch, CancellationToken.None);

        var result = runtime.Wait(handle, System.TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.True(result.Stdout?.Contains("hola-proceso"), "Captura la salida estandar");
    }

    [Fact]
    public void System_process_runtime_does_not_inherit_canary_environment_and_accepts_explicit_extras()
    {
        const string canary = "OMNICORE_TEST_CANARY_SECRET";
        const string extra = "OMNICORE_TEST_CONFIGURED_EXTRA";
        const string explicitVariable = "OMNICORE_TEST_EXPLICIT_LAUNCH";
        var oldCanary = Environment.GetEnvironmentVariable(canary);
        var oldExtra = Environment.GetEnvironmentVariable(extra);
        try
        {
            Environment.SetEnvironmentVariable(canary, "must-not-cross-boundary");
            Environment.SetEnvironmentVariable(extra, "configured-extra-value");
            var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "env";
            var args = OperatingSystem.IsWindows() ? new[] { "/c", "set" } : Array.Empty<string>();
            var launch = new ProcessLaunch(executable, args, "",
                new Dictionary<string, string> { [explicitVariable] = "explicit-value" }, true);

            var baseline = new SystemProcessRuntime();
            var noExtras = baseline.Launch(launch, CancellationToken.None);
            var baselineResult = baseline.Wait(noExtras, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(baselineResult.TimedOut);
            Assert.DoesNotContain(canary + "=", baselineResult.Stdout ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(extra + "=", baselineResult.Stdout ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains(explicitVariable + "=explicit-value", baselineResult.Stdout ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PATH=", baselineResult.Stdout ?? "", StringComparison.OrdinalIgnoreCase);

            var configured = new SystemProcessRuntime(new[] { extra });
            var withExtra = configured.Launch(launch, CancellationToken.None);
            var configuredResult = configured.Wait(withExtra, TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Contains(extra + "=configured-extra-value", configuredResult.Stdout ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(canary, oldCanary);
            Environment.SetEnvironmentVariable(extra, oldExtra);
        }
    }

    [Fact]
    public async Task LocalModelHost_attach_ready_and_managed_start_stop()
    {
        // ADR-0011 §4: attach verifica salud; managed lanza con IProcessRuntime y se cancela.
        var runtime = SystemProcessRuntime.Instance();
        var host = new LocalModelHost(runtime);

        Assert.Equal(LocalServerStatus.Ready, host.Attach(() => true));
        Assert.Equal(LocalServerStatus.Unreachable, host.Attach(() => false));

        // managed: lanza un proceso largo (nunca listo); el timeout readiness lo cancela.
        var spec = new ManagedServerSpec("cmd.exe", ["/c", "ping -n 30 127.0.0.1 > NUL"], "", 0);
        var ready = host.StartManaged(spec, () => false, System.TimeSpan.FromSeconds(2));
        Assert.Equal(LocalServerStatus.Unreachable, ready);
        Assert.False(host.IsManagedRunning(), "El timeout de readiness cancela el árbol");
    }

    [Fact]
    public async Task ModeDefaultsPolicy_fills_unspecified_tools_on_scripted()
    {
        // ADR-0037 §4: las tools sin regla explícita en el escenario caen al default del modo.
        var policy = OmniCore.Security.ScriptedPermissionPolicy
            .WithTool("fake.write", OmniCore.Domain.PermissionDecision.Allow)
            .WithModeDefaults(OmniCore.Domain.RunMode.Act);
        var intent = new OmniCore.Abstractions.ToolIntent(OmniCore.Domain.ToolCallId.New(),
            new OmniCore.Abstractions.ToolId("filesystem.read"),
            "{}", OmniCore.Domain.EffectClass.None, OmniCore.Domain.ResourceClaims.Empty(),
            OmniCore.Abstractions.ToolRisk.Low, null);

        var record = policy.Evaluate(intent);

        Assert.True(record.Final == OmniCore.Domain.PermissionDecision.Allow, "read es Allow en modo Act");
        Assert.True(record.Layers.Count >= 2, "La segunda capa es ModeDefaultsPolicy");
    }
}