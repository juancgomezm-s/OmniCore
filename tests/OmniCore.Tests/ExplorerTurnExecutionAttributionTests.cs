using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerTurnExecutionAttributionTests
{
    [Fact]
    public void Filesystem_tool_events_receive_run_task_lane_turn_envelopes_and_survive_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-exec-attribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "doc.txt"), "attribution fixture");
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var tools = OmniHost.CreateExplorerTools();
            var catalog = tools.Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);
            var index = 0;
            ModelResponse Complete(ModelRequest request, CancellationToken cancellationToken) => index++ == 0
                ? new ModelResponse(new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "script-read",
                    "filesystem.read", "{\"path\":\"doc.txt\"}") }, StopReason.ToolUse,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("script", "test", null))
                : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("script", "test", null));
            var turn = new ExplorerTurn(Complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("script", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("script"), 8192, ToolMode.Direct, null), store, codecs,
                new FileArtifactStore(Path.Combine(root, "blobs")), new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("read doc.txt", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Contains(result.ToolCalls, call => call.ToolName == "filesystem.read" && call.Succeeded);

            store.Close();
            store = new SqliteEventStore(journal);
            var events = store.ReadFrom(session, 1);
            var toolEvents = events.Where(evt => evt.Type.ToString().StartsWith("toolcall.", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(toolEvents);
            var turnId = events.Single(evt => evt.Type.ToString() == "turn.started").TurnId;
            Assert.NotNull(turnId);
            foreach (var evt in toolEvents)
            {
                Assert.Equal(run.RunId, evt.RunId);
                Assert.Equal(run.RootTask, evt.TaskId);
                Assert.Equal(run.RootLane, evt.LaneId);
                Assert.Equal(turnId, evt.TurnId);
            }
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
