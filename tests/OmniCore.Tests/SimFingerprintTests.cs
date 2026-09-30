using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-009 (M1, ADR-0017): cada <c>TurnStarted</c> de un run simulado lleva su
/// <see cref="ExecutionFingerprint"/> —determinista: escenario + model key falso fijo + hash del
/// toolkit del sim— igual que los turnos reales. Determinista por construcción: dos ejecuciones
/// del mismo escenario producen el mismo fingerprint y el replay desde el journal lo reconstruye
/// sin romper la golden rule (ack ok ⇒ sin violaciones).
/// </summary>
public sealed class SimFingerprintTests
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

    /// <summary>Ejecuta el escenario a nivel de engine y devuelve los TurnStarted del journal.</summary>
    private static IReadOnlyList<TurnStarted> RunAndCollectTurnStarts(string scenarioName)
    {
        var scenario = ScenarioLoader.Parse(Scenario(scenarioName));
        var store = new InMemoryEventStore();
        var engine = new SimulationEngine(store, EventCodecs.Create(), new InMemoryAuditSink());
        var result = engine.Execute(scenario, TestContext.Current.CancellationToken);

        var codecs = EventCodecs.Create();
        return store.ReadFrom(result.SessionId, 1)
            .Select(e => codecs.Decode(e))
            .OfType<TurnStarted>()
            .ToList();
    }

    [Fact]
    public void Simulated_turns_carry_the_same_deterministic_fingerprint()
    {
        var first = RunAndCollectTurnStarts("multi-item-plan");
        var second = RunAndCollectTurnStarts("multi-item-plan");

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.All(first.Concat(second), started => Assert.NotNull(started.Fingerprint));

        var a = first[0].Fingerprint!;
        var b = second[0].Fingerprint!;
        Assert.Equal(a.Hash(), b.Hash()); // determinista entre ejecuciones (ids distintos)
        Assert.Equal("sim.scripted", a.ModelKey); // model key falso fijo, nunca un modelo real
        Assert.Equal(64, a.HarnessPolicyHash.Length); // SHA-256 hex del escenario
        Assert.Equal(64, a.ToolkitHash.Length); // SHA-256 hex de la superficie de tools
    }

    [Fact]
    public void The_fingerprint_identifies_the_scenario_and_its_tool_surface()
    {
        var plan = RunAndCollectTurnStarts("multi-item-plan");
        var ask = RunAndCollectTurnStarts("ask-permission");

        Assert.NotEqual(plan[0].Fingerprint!.Hash(), ask[0].Fingerprint!.Hash());
        Assert.NotEqual(plan[0].Fingerprint!.HarnessPolicyHash, ask[0].Fingerprint!.HarnessPolicyHash);
        Assert.NotEqual(plan[0].Fingerprint!.ToolkitHash, ask[0].Fingerprint!.ToolkitHash);
    }

    [Theory]
    [InlineData("multi-item-plan")]
    [InlineData("taskgraph-nm")]
    [InlineData("ask-permission")]
    [InlineData("crash-resume")]
    [InlineData("plan-approval")]
    public void Every_scenario_replays_from_the_journal_with_fingerprinted_turns(string name)
    {
        // El mismo camino que `omni sim` con el escenario completo en el comando. El engine
        // verifica la golden rule al final de cada run: una violación ⇒ exit 3 ⇒ ack error,
        // así que ack ok certifica el replay. Todos los turn.started llevan fingerprint.
        var server = OmniHost.CreateInMemoryServer();
        var payload = "{" + JsonObj.Field("cmd", "sim") + "," + JsonObj.Field("scenarioYaml", Scenario(name)) + "}";
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);

        Assert.True(ack.Status == "ok", name + ": " + ack.Error);

        var codecs = EventCodecs.Create();
        var journal = server.AcquireStore().ReadFrom(server.LastSessionId()!, 1);
        var starts = journal.Select(e => codecs.Decode(e)).OfType<TurnStarted>().ToList();
        Assert.NotEmpty(starts);
        Assert.All(starts, started =>
        {
            Assert.NotNull(started.Fingerprint);
            Assert.Equal("sim.scripted", started.Fingerprint!.ModelKey);
        });
    }
}
