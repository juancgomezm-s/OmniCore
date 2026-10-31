using OmniCore.Cli;
using OmniCore.Domain;
using Task = System.Threading.Tasks.Task;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class CliEndToEndTests
{
    [Fact]
    public async Task Cli_inicializar_creates_updates_and_repeats_without_model_or_session_input()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            Assert.Equal(0, (await Run("trust")).Code);
            File.WriteAllText(Path.Combine(workspace, "MEMORY.md"), "Decision: use local SQLite.");
            File.WriteAllText(Path.Combine(workspace, "package.json"), "{\"scripts\":{\"test\":\"vitest run\",\"build\":\"tsc\"}}");
            var first = await Run("inicializar");
            Assert.Equal(0, first.Code);
            Assert.Contains("MEMORY.md", first.Output, StringComparison.Ordinal);
            var target = Path.Combine(workspace, "agents.md");
            Assert.True(File.Exists(target));
            Assert.Contains("vitest run", File.ReadAllText(target), StringComparison.Ordinal);
            File.AppendAllText(target, "\nManual extra.\n");
            var again = await Run("initialize");
            Assert.Equal(0, again.Code);
            var before = File.ReadAllText(target);
            var repeated = await Run("/inicializar");
            Assert.Equal(0, repeated.Code);
            Assert.Contains("sin cambios", repeated.Output, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(target));
            Assert.EndsWith("\nManual extra.\n", before, StringComparison.Ordinal);
            Assert.Equal(0, provider.RequestCount);
        });
    }

    [Fact]
    public async Task Cli_inicializar_rejects_untrusted_workspace_and_a_durable_active_turn()
    {
        await InIsolatedCli(async (workspace, _, _, provider) =>
        {
            var untrusted = await Run("inicializar");
            Assert.Equal(1, untrusted.Code);
            Assert.False(File.Exists(Path.Combine(workspace, "agents.md")));
            Assert.Equal(0, (await Run("trust")).Code);
            using var runtime = OmniCliRuntime.Create(workspace);
            var server = Assert.IsType<OmniServer>(runtime.Connect(CancellationToken.None));
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"Pending conversation\"}"), CancellationToken.None).Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var lane = Assert.IsType<LaneId>(server.LastLaneId());
            var turn = TurnId.New();
            new OmniCore.Engine.EventStream(server.AcquireStore(), server.AcquireCodecs(), session)
                .Append(new TurnStarted(turn, lane), DurabilityClass.Barrier);
            var error = Assert.Throws<InvalidOperationException>(() => runtime.InitializeProject(TestContext.Current.CancellationToken));
            Assert.Equal("project.initialize.busy", error.Message);
            Assert.False(File.Exists(Path.Combine(workspace, "agents.md")));
            Assert.Equal(0, provider.RequestCount);
        });
    }
}
