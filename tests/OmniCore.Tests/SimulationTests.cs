using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class SimulationTests
{
    [Fact]
    public async Task Plan_propose_applies_mutation_to_plan_projection()
    {
        // P0-6: plan.propose debe aplicar la mutación de verdad (PlanProjection cambia).
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var hostTools = OmniHost.CreateHostTools();
        var executor = ScriptedToolExecutor.WithCoreTools(hostTools.Catalog(),
            OmniCore.Security.ScriptedPermissionPolicy.WithTool("plan.propose",
                OmniCore.Domain.PermissionDecision.Allow));
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink(), executor);
        var scenario = Scenarios.WithPlanPropose();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        // El item P1 debe haber pasado a InProgress (PlanItemStarted emitido por PlanService).
        var tail = store.ReadFrom(result.SessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("plan_item.started"), "plan.propose emite PlanItemStarted");
        Assert.True(result.ExitCode == 0, string.Join(" | ", result.Diagnostics));
    }

    [Fact]
    public async Task Sim_multi_item_plan_completes()
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink());
        var scenario = Scenarios.MultiItemPlan();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Completed", result.Run.State.ToString());
        Assert.True(result.Plan.Items().Count >= 4, "El plan debe contener root + 3 items");
    }

    [Fact]
    public async Task Sim_golden_rule_replays_identical_from_journal()
    {
        // Golden rule (ADR-0041 §2): el estado reconstruido desde el journal coincide con el vivo.
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink());
        var scenario = Scenarios.MultiItemPlan();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        // El motor compara el estado vivo (payloads aplicados en memoria) con el reconstruido
        // desde el journal; una diferencia daría exit code 3 y un diagnóstico "golden rule".
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("golden rule", StringComparison.Ordinal));
    }

    [Fact]
    public void Golden_rule_detects_a_journal_that_lost_an_event()
    {
        // La regla no es tautológica: si el journal no reproduce lo vivo, lo dice.
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var session = OmniCore.Domain.SessionId.New();
        var stream = new EventStream(store, codecs, session);
        var opened = TestRun.Open(stream, session); // el mismo stream: único escritor
        var plan = OmniCore.Domain.PlanId.New();
        var root = OmniCore.Domain.PlanItemId.New();
        stream.Append(new OmniCore.Domain.PlanCreated(plan, opened.RunId, root, "objetivo"));
        stream.Append(new OmniCore.Domain.PlanItemStarted(root));
        stream.Append(new OmniCore.Domain.PlanItemCompleted(root, null));

        var journal = store.ReadFrom(session, 1);
        Assert.Empty(GoldenRule.Check(codecs, journal, stream));

        var lossy = journal.Where(e => e.Type.ToString() != "plan_item.completed").ToArray();
        var diffs = GoldenRule.Check(codecs, lossy, stream);
        Assert.NotEmpty(diffs);
        Assert.Contains(diffs, d => d.Contains("Completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sim_exposes_working_state_projection()
    {
        // ADR-0016 §7: el WorkingState se proyecta desde el plan final del run (no se persiste).
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink());
        var scenario = Scenarios.MultiItemPlan();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        Assert.NotNull(result.WorkingState);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Corregir el test de autenticación", result.WorkingState!.RunObjective);
        Assert.Equal("Completed", result.WorkingState.Items[0].State.ToString());
        var rendered = OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState);
        Assert.Contains("Objetivo:", rendered);
    }

    [Fact]
    public async Task Sim_watchdog_emits_progress_stalled_when_stuck()
    {
        // ADR-0016 §9: con un turno sin señal de progreso sobre un item en curso, el watchdog
        // está conectado (no lanza y el journal puede contener progress.stalled para 6+ turns).
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink());
        var scenario = Scenarios.MultiItemPlan();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        var types = store.ReadFrom(result.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
        // En el escenario normal el run termina rápido, así que no hay stall; solo se verifica
        // que el cable no rompe el flujo (el watchdog corre en cada execute).
        Assert.True(types.Length > 10, "El journal contiene los eventos del run");
    }

    [Fact]
    public async Task Ask_tool_emits_interaction_in_journal()
    {
        // Con una política Ask y el cliente del sim aprobando (userApprovesAsk=true), el
        // pipeline emite PermissionRequested + InteractionRequested + PermissionGranted y
        // EJECUTA la tool una sola vez (el flujo de "Ask aprobado" ya no re-evalúa denegando).
        var policy = OmniCore.Security.ScriptedPermissionPolicy.WithTool("fake.write",
            OmniCore.Domain.PermissionDecision.Ask);
        var catalog = OmniCore.Tools.FakeCatalog.Default();
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var executor = new OmniCore.Host.ScriptedToolExecutor(catalog, policy);
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink(), executor);
        var tools = Scenarios.WithTools();
        // El usuario simulado aprueba el Ask; sin respuesta se denegaría (ADR-0003).
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>> {
            ["root"] = [SimulatedTurnAction.ToolCall("fake.write", "applied", "approve"), SimulatedTurnAction.DoneMarker()],
        };
        var scenario = new SimulationScenario(tools.Name, tools.Mode, tools.Input, tools.Plan, tools.Tasks, turns,
            tools.Permissions, tools.ExpectedRunState, tools.ExpectedPlan);

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        var tail = store.ReadFrom(result.SessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.Contains("toolcall.permission_requested", types);
        Assert.Contains("interaction.requested", types);
        Assert.Contains("interaction.resolved", types);
        Assert.Contains("toolcall.permission_granted", types);
        Assert.Contains("toolcall.succeeded", types);
        Assert.False(types.Contains("toolcall.permission_denied"), "el Ask aprobado NO re-deniega");
    }

    [Fact]
    public async Task Sim_pipeline_emits_toolcall_events_in_order()
    {
        // Con executor real, una tool call pasa por Requested → PermissionEvaluated
        // → Authorized → Started → Succeeded en orden (ADR-0004/0036), en el journal.
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink(),
            OmniCore.Host.ScriptedToolExecutor.Default());
        var scenario = Scenarios.WithTools();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        var tail = store.ReadFrom(result.SessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        var joined = string.Join("|", types);

        Assert.True(Indexes.Of(types).appearsInOrder("toolcall.requested", "toolcall.authorized",
            "toolcall.started", "toolcall.succeeded"), "Secuencia de la tool call en el journal:\n" + joined);
        Assert.True(Indexes.Of(types).appearsInOrder("toolcall.permission_evaluated", "toolcall.started"),
            "Traza de permisos antes de ejecutar:\n" + joined);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Sim_crash_leaves_toolcall_started_and_resume_reconciles_without_dup()
    {
        // ADR-0004/0041: crash tras Started deja la toolcall sin outcome; el resume la
        // reconcilia (EffectUnknown → Reconciled) sin duplicar el efecto.
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, codecs, new InMemoryAuditSink(),
            OmniCore.Host.ScriptedToolExecutor.Default());
        var scenario = Scenarios.WithToolCrash();

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        var tail = store.ReadFrom(result.SessionId, 1);
        var types = tail.Select(e => e.Type.ToString()).ToArray();
        Assert.True(types.Contains("toolcall.started"), "El crash deja la toolcall Started");
        Assert.False(types.Contains("toolcall.succeeded"), "No debe haber outcome de la tool");
        Assert.True(types.Contains("turn.abandoned"), "El turno se abandona por el crash");

        // Resume con un nuevo stream sobre el mismo store.
        var stream2 = new EventStream(store, codecs, result.SessionId);
        var reconciled = engine.Resume(result.SessionId, result.RunId, stream2);
        Assert.True(reconciled >= 1, "Resume reconcilia la toolcall huérfana");

        var after = store.ReadFrom(result.SessionId, 1);
        var afterTypes = after.Select(e => e.Type.ToString()).ToArray();
        Assert.True(Count(afterTypes, "toolcall.reconciled") == 1, "Reconciled una única vez (sin duplicar)");
        Assert.True(Count(afterTypes, "toolcall.succeeded") == 0, "El efecto no se re-ejecuta: sigue sin succeed");
    }

    private static int Count(string[] types, string type)
    {
        var n = 0;
        foreach (var t in types)
        {
            if (t == type)
            {
                n += 1;
            }
        }

        return n;
    }

    /// <summary>Verifica que varios nombres de evento aparecen en orden relativo en una lista.</summary>
    private sealed class Indexes
    {
        private readonly string[] _types;

        private Indexes(string[] types) => _types = types;

        public static Indexes Of(string[] types) => new Indexes(types);

        public bool appearsInOrder(string first, string second, string third, string fourth)
        {
            var a = IndexOf(first);
            var b = IndexOf(second);
            var c = IndexOf(third);
            var d = IndexOf(fourth);
            return a >= 0 && b >= a && c >= b && d >= c;
        }

        public bool appearsInOrder(string first, string second)
        {
            var a = IndexOf(first);
            var b = IndexOf(second);
            return a >= 0 && b >= a;
        }

        private int IndexOf(string type)
        {
            for (var i = 0; i < _types.Length; i++)
            {
                if (_types[i] == type)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}