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

/// <summary>Scripted providers, real tools/journal/CAS. No authenticated provider claim.</summary>
public sealed class ToolCallMultiRunDurableAttributionTests
{
    [Theory]
    [InlineData("allowed", "toolcall.succeeded")]
    [InlineData("denied", "toolcall.permission_denied")]
    [InlineData("missing", "toolcall.failed")]
    public void Subsequent_run_does_not_steal_tool_or_model_step_scope_after_reopen(string scenario,
        string expectedTerminal)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-m55-multirun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "read.txt"), "fixture read body");
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var artifacts = new FileArtifactStore(Path.Combine(root, "data"));
            var first = TestRun.Open(store, session, "first run");
            var firstCall = ToolCallId.New();
            Execute(first, firstCall, PermissionDecision.Allow, "read.txt");
            var firstEvents = store.ReadFrom(session, 1).ToArray();
            var firstTurn = Assert.Single(firstEvents, e => codecs.Decode(e) is TurnStarted).TurnId!;
            Assert.Single(firstEvents, e => codecs.Decode(e) is ToolCallSucceeded succeeded
                && succeeded.ToolCallId == firstCall);

            // ADR-0046: one active Run per Session. Use the real cancellation service,
            // which also closes the first Run's live Task/Lane, before admitting the next Run.
            new RunControlService(store, codecs).CancelRun(session, first.RunId);
            Assert.Equal(RunState.Cancelled,
                RunProjection.Replay(session, first.RunId, codecs, store.ReadFrom(session, 1)).State);
            var second = TestRun.Open(store, session, "second run");
            var secondCall = ToolCallId.New();
            Execute(second, secondCall, scenario == "denied" ? PermissionDecision.Deny : PermissionDecision.Allow,
                scenario == "missing" ? "absent.txt" : "read.txt");
            var all = store.ReadFrom(session, 1).ToArray();
            var secondTurn = Assert.Single(all, e => codecs.Decode(e) is TurnStarted
                && e.RunId == second.RunId).TurnId!;
            Assert.NotEqual(firstTurn, secondTurn);
            Assert.Single(all, e => e.ToolCallId == secondCall && e.Type.ToString() == expectedTerminal);

            // Capture complete durable fields before reopening, not just IDs or latest projection.
            var snapshots = all.Select(Snapshot).ToArray();
            store.Close();
            SqliteConnection.ClearPool((SqliteConnection)store.Connection);
            store = new SqliteEventStore(journal);
            var replay = store.ReadFrom(session, 1).ToArray();
            Assert.Equal(snapshots, replay.Select(Snapshot).ToArray());
            Assert.Equal(firstEvents.Select(Snapshot), replay.Take(firstEvents.Length).Select(Snapshot));
            VerifyScope(first, firstCall, firstTurn);
            VerifyScope(second, secondCall, secondTurn);

            var steps = replay.Where(e => codecs.Decode(e) is ModelStepCompleted).ToArray();
            Assert.Equal(4, steps.Length);
            Assert.Equal(8, steps.Sum(e => ((ModelStepCompleted)codecs.Decode(e)).Usage.Input));
            Assert.Equal(12, steps.Sum(e => ((ModelStepCompleted)codecs.Decode(e)).Usage.Output));
            Assert.Equal(2, replay.Count(e => codecs.Decode(e) is TurnCompleted));
            Assert.All(steps, e =>
            {
                var step = (ModelStepCompleted)codecs.Decode(e);
                var reference = Assert.Single(e.ArtifactRefs);
                Assert.Equal(step.ResponseArtifact, reference);
                Assert.True(artifacts.Verify(reference.Hash, reference.Size));
                Assert.NotNull(artifacts.GetText(reference.Hash));
                Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset);
                var owner = e.RunId == first.RunId ? first : second;
                Assert.Equal(owner.RootTask, e.TaskId);
                Assert.Equal(owner.RootLane, e.LaneId);
                Assert.Equal(owner.RunId, e.CorrelationId);
            });

            void VerifyScope(TestRun.Opened owner, ToolCallId call, TurnId turn)
            {
                var toolEvents = replay.Where(e => e.ToolCallId == call).ToArray();
                Assert.True(toolEvents.Length >= 3);
                Assert.Single(toolEvents, e => codecs.Decode(e) is ToolCallRequested);
                Assert.All(toolEvents, e =>
                {
                    Assert.Equal(session, e.SessionId);
                    Assert.Equal(owner.RunId, e.RunId);
                    Assert.Equal(owner.RunId, e.CorrelationId);
                    Assert.Equal(owner.RootTask, e.TaskId);
                    Assert.Equal(owner.RootLane, e.LaneId);
                    Assert.Equal(turn, e.TurnId);
                    Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset);
                    Assert.Equal("OmniCore.Engine.EventStream", e.Source);
                });
            }

            void Execute(TestRun.Opened owner, ToolCallId call, PermissionDecision permission, string file)
            {
                var catalog = OmniHost.CreateExplorerTools().Catalog();
                var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                        { ["filesystem.read"] = permission }), root);
                var calls = 0;
                ModelResponse Complete(ModelRequest request, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ContentBlock block = calls++ == 0
                        ? new ToolCallBlock(call, "fixture-" + call, "filesystem.read",
                            System.Text.Json.JsonSerializer.Serialize(new { path = file }))
                        : new TextBlock("done");
                    return new ModelResponse([block], block is ToolCallBlock ? StopReason.ToolUse : StopReason.EndTurn,
                        new TokenUsage(2, 3, 0, 0, 0), null, new ProviderMetadata("script", "test", null));
                }
                var explorer = new ExplorerTurn(Complete, executor, catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    new ExecutionFingerprint("script", "h", "t", "c", "o", "M3"),
                    new ModelSelection(new ModelIdValue("script"), 8192, ToolMode.Direct, null), store!, codecs,
                    artifacts, new InMemoryAuditSink(), new RedactionPolicy());
                var outer = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
                using (ExecutionScope.Begin(outer))
                {
                    var result = explorer.Ask("read fixture", "system", session, owner.RunId, owner.RootLane,
                        "", CancellationToken.None);
                    Assert.Equal(StopReason.EndTurn, result.StopReason);
                    Assert.Equal(2, calls);
                    var trace = Assert.Single(result.ToolCalls);
                    Assert.Equal(permission == PermissionDecision.Allow && file == "read.txt", trace.Succeeded);
                    Assert.Same(outer, ExecutionScope.Current);
                }
                Assert.Null(ExecutionScope.Current);
            }
        }
        finally
        {
            if (store is not null)
            {
                store.Close();
                SqliteConnection.ClearPool((SqliteConnection)store.Connection);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Snapshot(DomainEvent e) => System.Text.Json.JsonSerializer.Serialize(new
    {
        e.EventId, e.SessionId, e.Sequence, e.Type, e.SchemaVersion, e.Timestamp,
        e.Causation, e.CorrelationId, e.RunId, e.TaskId, e.LaneId, e.TurnId,
        e.PlanItemId, e.ToolCallId, e.ExecutionId, e.Source, e.ArtifactRefs, e.PayloadJson,
    });
}
