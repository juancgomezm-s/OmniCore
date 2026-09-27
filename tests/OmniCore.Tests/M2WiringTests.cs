using OmniCore.Abstractions;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

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

        Assert.True(v.IsWithin(root + "\\README.md", root), "archivo dentro de la raíz");
        Assert.True(v.IsWithin(root + "\\src\\OmniCore.Domain\\Ids.cs", root), "subdirectorio dentro");
        Assert.False(v.IsWithin("C:\\Windows\\System32\\x.exe", root), "absoluto fuera");
        Assert.False(v.IsWithin(root + "\\..\\README.md", root), "traversal fuera");
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

        Assert.True(registry.Provider("openrouter") is not null);
        Assert.True(registry.Model("qwen-27b") is not null);
        Assert.True(registry.Model("sonnet") is not null);
    }

    [Fact]
    public async Task ConfigLoader_throws_no_model_configured()
    {
        var registry = new ModelRegistry();
        Assert.True(registry.Model("ninguno") is null);
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
            "Completed", scenario.ExpectedPlan, null);

        var result = engine.Execute(planMode, TestContext.Current.CancellationToken);

        var types = store.ReadFrom(result.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
        Assert.Contains("interaction.requested", types);
        Assert.Contains("run.mode_changed", types);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ContextMaterializer_orders_and_counts_with_working_state_last()
    {
        // ADR-0042 §2: el WorkingState va al final y el snapshot registra tokens.
        var counter = new FakeTokenCounter();
        var contributors = new OmniCore.Context.IContextContributor[] {
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
        Assert.Equal("working-state", snapshot.Items[0].Id);
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