using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerCommandTests
{
    [Fact]
    public void Slash_parser_returns_structured_command_invocation()
    {
        Assert.True(CommandLineParser.TryParse("/explain src/OmniCore", out var invocation));
        Assert.Equal("explain", invocation!.Name);
        Assert.Equal(new[] { "src/OmniCore" }, invocation.Arguments);
        Assert.Equal("Typed", invocation.InvocationOrigin);
    }

    [Fact]
    public void Prompt_command_is_expanded_by_host_and_engine_input_contains_no_slash_command()
    {
        var server = OmniHost.CreateInMemoryServer();
        var invocation = new CommandInvocation("explain", new[] { "src/OmniCore" }, "Typed");
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)),
            CancellationToken.None);

        Assert.Equal("ok", ack.Status);
        var json = server.Query("commandOutcome", CancellationToken.None)!.Json;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var outcome = document.RootElement.GetProperty("outcome");
        var expanded = outcome.GetProperty("text").GetString()!;
        Assert.Contains("src/OmniCore", expanded);
        Assert.DoesNotContain("/explain", expanded);
        Assert.Equal("PromptCommand(core:explain@1)", outcome.GetProperty("origin").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Context_query_reads_snapshot_artifact_and_returns_metadata_not_content()
    {
        var data = Path.Combine(Path.GetTempPath(), "omnicore-context-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(data);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts);
            var objective = "explain fixture";
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{"
                + JsonObj.Field("cmd", "explore.start") + ","
                + JsonObj.Field("objective", objective) + "}"), CancellationToken.None).Status);
            var session = server.LastSessionId()!;
            var run = server.LastRunId()!;
            var inputStream = new EventStream(store, codecs, session);
            inputStream.Append(new UserInputReceived(run, "\"kept initial context\"", null));
            var longHistory = string.Join(" ", Enumerable.Repeat("prior-context-material-that-must-be-trimmed", 600));
            for (var i = 0; i < 5; i++)
                inputStream.Append(new UserInputReceived(run, "\"" + JsonObj.Escape(longHistory) + "\"", null));

            var tools = OmniHost.CreateExplorerTools();
            var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(),
                ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow)
                    .WithModeDefaults(RunMode.Plan));
            var fingerprint = new ExecutionFingerprint("context-test", "h", "t", "c", "o", "M2");
            var turn = new ExplorerTurn((request, token) => FakeResponses.PlanThenEnd(request), executor,
                tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, new ModelSelection(new ModelIdValue("context-test"), 256, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            turn.Ask(objective, "Explain. {context}", session, run, server.LastLaneId()!,
                "runtime-owned", CancellationToken.None);

            var result = server.Query("context", CancellationToken.None)!.Json;
            using var snapshot = System.Text.Json.JsonDocument.Parse(result);
            var root = snapshot.RootElement.GetProperty("snapshot");
            Assert.Equal(fingerprint.Hash(), root.GetProperty("fingerprint").GetString());
            Assert.True(root.GetProperty("tokenCount").GetInt32() > 0);
            Assert.True(root.GetProperty("tokenBudget").GetInt64() > 0);
            var item = root.GetProperty("items").EnumerateArray().First();
            Assert.True(item.TryGetProperty("contributor", out _));
            Assert.Equal("estimated", item.GetProperty("tokenAccuracy").GetString());
            Assert.False(item.TryGetProperty("content", out _));
            Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
                diagnostic.GetProperty("decision").GetString() == "OmittedByBudget"
                && diagnostic.GetProperty("reason").GetString() == "omitted by context budget");
        }
        finally
        {
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Tools_query_reports_plan_mode_and_hides_mutations()
    {
        var server = OmniHost.CreateInMemoryServer();
        var effective = EffectiveModelPolicy.Resolve(ModelPolicyKey.For("p", "m"), null,
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 3,
                PlanControl.RuntimeDriven, 8));
        server.ConfigureToolDiagnostics(OmniHost.CreateActTools().Catalog(),
            new ModelCapabilityBoundary(effective), RunMode.Plan);

        var json = server.Query("tools", CancellationToken.None)!.Json;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Plan", document.RootElement.GetProperty("mode").GetString());
        var tools = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("decision").GetString() == "reject:PLAN mode");
        Assert.DoesNotContain(tools, tool => tool.GetProperty("visible").GetBoolean()
            && tool.GetProperty("effectClass").GetString() != "None");

        server.ConfigureToolDiagnostics(OmniHost.CreateActTools().Catalog(),
            new ModelCapabilityBoundary(effective), RunMode.Act);
        using var actDocument = System.Text.Json.JsonDocument.Parse(
            server.Query("tools", CancellationToken.None)!.Json);
        Assert.Contains(actDocument.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("decision").GetString() == "reject:ModelCapabilityBoundary");
    }
}
