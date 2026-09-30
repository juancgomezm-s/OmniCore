using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>
/// Criterio de salida de M1 (ADR-0041): los escenarios YAML de <c>docs/sim/</c> (plan de varios
/// items, TaskGraph N:M, Ask, crash + resume, PLAN → ACT) se ejecutan por el mismo camino que
/// <c>omni sim</c> —el escenario completo viaja en el comando— y se reconstruyen desde el journal.
/// El motor verifica en cada ejecución las expectativas del escenario y la golden rule.
/// </summary>
public sealed class ScenarioYamlTests
{
    private static string Scenario(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "sim", name + ".yaml");
            if (File.Exists(Path.Combine(dir.FullName, "OmniCore.slnx")) && File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("escenario no encontrado: " + name);
    }

    private static (OmniServer Server, CommandAck Ack) RunYaml(string name)
    {
        var server = OmniHost.CreateInMemoryServer();
        var payload = "{" + JsonObj.Field("cmd", "sim") + "," + JsonObj.Field("scenarioYaml", Scenario(name)) + "}";
        return (server, server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None));
    }

    private static IReadOnlyList<DomainEvent> Journal(OmniServer server) =>
        server.AcquireStore().ReadFrom(server.LastSessionId()!, 1);

    private static int Count(IReadOnlyList<DomainEvent> journal, string type) =>
        journal.Count(e => e.Type.ToString() == type);

    [Theory]
    [InlineData("multi-item-plan")]
    [InlineData("taskgraph-nm")]
    [InlineData("ask-permission")]
    [InlineData("crash-resume")]
    [InlineData("plan-approval")]
    public void Every_exit_criterion_scenario_runs_and_replays_from_the_journal(string name)
    {
        var (server, ack) = RunYaml(name);

        Assert.True(ack.Status == "ok", name + ": " + ack.Error);
        var journal = Journal(server);
        // El journal completo es válido contra las máquinas de estado (ADR-0036).
        CanonicalStateTracker.Replay(EventCodecs.Create(), journal);
        Assert.Equal(server.LastRunId(), RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!,
            EventCodecs.Create(), journal).Id);
    }

    [Fact]
    public void TaskGraph_nm_links_one_task_to_several_items_and_one_item_to_several_tasks()
    {
        var (server, ack) = RunYaml("taskgraph-nm");
        Assert.Equal("ok", ack.Status);

        var plan = PlanProjection.Replay(EventCodecs.Create(), Journal(server));
        var linksPerItem = plan.Items().Select(item => item.LinkedTasks.Count).ToArray();
        Assert.Contains(3, linksPerItem); // P2: T1 + T2 + T3 (supports)
        var linksPerTask = plan.Items().SelectMany(item => item.LinkedTasks).GroupBy(link => link.TaskId)
            .Select(group => group.Count()).ToArray();
        Assert.Contains(2, linksPerTask); // T1 implementa P1 y P2
    }

    [Fact]
    public void Ask_is_resolved_by_the_user_answer_and_denied_without_one()
    {
        var (server, ack) = RunYaml("ask-permission");
        Assert.Equal("ok", ack.Status);

        var journal = Journal(server);
        Assert.Equal(3, Count(journal, "toolcall.permission_requested"));
        Assert.Equal(3, Count(journal, "interaction.resolved"));   // toda interacción se cierra
        Assert.Equal(1, Count(journal, "toolcall.permission_granted"));
        Assert.Equal(2, Count(journal, "toolcall.permission_denied")); // "deny" y la sin respuesta
        Assert.Equal(1, Count(journal, "toolcall.succeeded"));
    }

    [Fact]
    public void Crash_then_resume_reconciles_the_orphan_toolcall_once_without_reexecuting()
    {
        var (server, ack) = RunYaml("crash-resume");
        Assert.Equal("ok", ack.Status);
        var crashed = Journal(server);
        Assert.Equal(1, Count(crashed, "toolcall.started"));
        Assert.Equal(0, Count(crashed, "toolcall.succeeded"));

        var resume = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim.resume") + "}"),
            CancellationToken.None);
        Assert.True(resume.Status == "ok", resume.Error);

        var after = Journal(server);
        Assert.Equal(1, Count(after, "toolcall.reconciled"));
        Assert.Equal(0, Count(after, "toolcall.succeeded"));
        var tracker = CanonicalStateTracker.Replay(EventCodecs.Create(), after);
        Assert.Contains(tracker.Snapshot(), line => line.StartsWith("toolcall:", StringComparison.Ordinal)
            && line.EndsWith("=Reconciled", StringComparison.Ordinal));

        // Un segundo resume no vuelve a reconciliar (idempotente).
        server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim.resume") + "}"),
            CancellationToken.None);
        Assert.Equal(1, Count(Journal(server), "toolcall.reconciled"));
    }

    [Fact]
    public void Plan_approval_is_given_by_the_user_and_switches_the_same_run_to_act()
    {
        var (server, ack) = RunYaml("plan-approval");
        Assert.Equal("ok", ack.Status);

        var journal = Journal(server);
        Assert.Equal(1, Count(journal, "interaction.resolved"));
        Assert.Equal(1, Count(journal, "run.mode_changed"));
        var run = RunProjection.Replay(server.LastSessionId()!, server.LastRunId()!, EventCodecs.Create(), journal);
        Assert.Equal(RunMode.Act, run.Mode);
        Assert.Equal(RunState.Completed, run.State);
    }

    [Theory]
    [InlineData(null, RunState.AwaitingInput)]
    [InlineData("reject", RunState.AwaitingInput)]
    [InlineData("approve_only", RunState.Completed)]
    public void Without_approval_the_plan_run_waits_and_approve_only_ends_it_as_planned(string? answer,
        RunState expected)
    {
        var yaml = Scenario("plan-approval").Replace("planApproval: approve_execute",
            answer is null ? "" : "planApproval: " + answer);
        var scenario = ScenarioLoader.Parse(yaml);
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, EventCodecs.Create(), new InMemoryAuditSink());

        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Run.State);
        Assert.Equal(RunMode.Plan, result.Run.Mode); // nunca pasa a ACT sin approve_execute
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("golden rule", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scenario_with_an_unknown_field_is_rejected_instead_of_run_halfway()
    {
        var ex = Assert.Throws<ScenarioFormatException>(() =>
            ScenarioLoader.Parse(Scenario("multi-item-plan") + "\nturnz: {}\n"));
        Assert.Contains("turnz", ex.Message);
    }

    [Fact]
    public void Values_with_quotes_and_newlines_survive_the_protocol()
    {
        var text = "línea 1\n\"entre comillas\"\tfin \\ barra";
        var parsed = JsonObj.Parse("{" + JsonObj.Field("k", text) + "}");
        Assert.Equal(text, parsed["k"]);
    }
}
