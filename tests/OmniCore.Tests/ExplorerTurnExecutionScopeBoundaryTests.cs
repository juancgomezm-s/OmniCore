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

/// <summary>Execution attribution remains exact across two Runs, suspension, reopen and early exits.</summary>
public sealed class ExplorerTurnExecutionScopeBoundaryTests
{
    private static readonly QuestionnaireSchema Schema = new("Continue?", null, new QuestionField[]
    {
        new("choice", "Choose?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("yes", "Yes", null) }, null, true, null, null, null),
    });

    private static readonly ModelSelection Selection = new(new ModelIdValue("scripted"), 8192,
        ToolMode.Direct, null);
    private static readonly ExecutionFingerprint Fingerprint = new("scripted", "h", "t", "c", "o", "M3");

    [Fact]
    public void Scope_attribution_is_run_lane_turn_isolated_and_restores_parent_across_all_Ask_exits()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-execution-scope-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "blobs");
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "source.txt"), "filesystem tool crossed the scope boundary");
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var runA = TestRun.Open(store, session, "Run A", RunMode.Plan);
            var runB = TestRun.Open(store, session, "Run B", RunMode.Plan);
            Assert.NotEqual(runA.RootTask, runB.RootTask);
            Assert.NotEqual(runA.RootLane, runB.RootLane);

            var artifacts = new FileArtifactStore(blobs);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var explorerCatalog = OmniHost.CreateExplorerTools().Catalog();
            var catalog = new FakeCatalog()
                .Add(explorerCatalog.Find(new ToolId("filesystem.read"))!)
                .Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);

            var outside = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
            var aCall = 0;
            var aScopeSnapshots = new List<ExecutionScopeState?>();
            ModelResponse AskA(ModelRequest _, CancellationToken __)
            {
                aScopeSnapshots.Add(ExecutionScope.Current);
                if (aCall++ == 0)
                    return ToolCall("user.ask", QuestionnaireCodec.EncodeSchema(Schema));
                if (aCall == 2)
                    return ToolCall("filesystem.read", "{\"path\":\"source.txt\"}");
                return Final("A resumed");
            }

            ExplorerTurn.TurnResult pending;
            var firstA = MakeTurn(AskA, executor, catalog, questionnaires, store, codecs, artifacts,
                responder: (_, _) => null);
            using (ExecutionScope.Begin(outside))
            {
                pending = firstA.Ask("ask before reading", "system", session, runA.RunId,
                    runA.RootLane, "", CancellationToken.None);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(StopReason.InputRequired, pending.StopReason);
            var interactionId = Assert.IsType<InteractionId>(pending.PendingInteractionId);
            var suspendedScope = Assert.IsType<ExecutionScopeState>(aScopeSnapshots[0]);
            Assert.Equal(runA.RunId, suspendedScope.RunId);
            Assert.Equal(runA.RootTask, suspendedScope.TaskId);
            Assert.Equal(runA.RootLane, suspendedScope.LaneId);
            var suspendedTurn = Assert.IsType<TurnId>(suspendedScope.TurnId);

            // A second Ask against the same unresolved interaction exits before model execution.
            using (ExecutionScope.Begin(outside))
            {
                var stillPending = firstA.Ask("", "system", session, runA.RunId, runA.RootLane, "",
                    CancellationToken.None);
                Assert.Equal(StopReason.InputRequired, stillPending.StopReason);
                Assert.Equal(interactionId, stillPending.PendingInteractionId);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(1, aCall);

            // Execute the other Run while A is suspended. Its real filesystem read must use B's IDs.
            var bScopeSnapshots = new List<ExecutionScopeState?>();
            var bCall = 0;
            ModelResponse AskB(ModelRequest _, CancellationToken __)
            {
                bScopeSnapshots.Add(ExecutionScope.Current);
                return bCall++ == 0
                    ? ToolCall("filesystem.read", "{\"path\":\"source.txt\"}")
                    : Final("B complete");
            }
            var turnB = MakeTurn(AskB, executor, catalog, questionnaires, store, codecs, artifacts);
            ExplorerTurn.TurnResult completedB;
            using (ExecutionScope.Begin(outside))
            {
                completedB = turnB.Ask("read in B", "system", session, runB.RunId,
                    runB.RootLane, "", CancellationToken.None);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(StopReason.EndTurn, completedB.StopReason);
            Assert.Equal(new ExecutionScopeState(runB.RunId, runB.RootTask, runB.RootLane,
                bScopeSnapshots[0]!.TurnId), bScopeSnapshots[0]);
            Assert.Contains(completedB.ToolCalls, trace => trace.ToolName == "filesystem.read" && trace.Succeeded);

            // Resolve the real durable questionnaire, then close/reopen SQLite before resuming A.
            var resolved = questionnaires.Resolve(new EventStream(store, codecs, session), interactionId,
                new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) }, false, null,
                new UserInputReceived(runA.RunId, "[]", null, "InteractionResponse(Questionnaire)"));
            Assert.True(resolved.Accepted);
            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);

            // A's continuation keeps its original Turn ID and the same Run/Task/Lane scope after reopen.
            var resumedScopes = new List<ExecutionScopeState?>();
            ModelResponse ResumeA(ModelRequest _, CancellationToken __)
            {
                resumedScopes.Add(ExecutionScope.Current);
                return resumedScopes.Count == 1
                    ? ToolCall("filesystem.read", "{\"path\":\"source.txt\"}")
                    : Final("A done");
            }
            var turnAResume = MakeTurn(ResumeA, executor, catalog, questionnaires, store, codecs, artifacts);
            ExplorerTurn.TurnResult completedA;
            using (ExecutionScope.Begin(outside))
            {
                completedA = turnAResume.Ask("resume A", "system", session, runA.RunId,
                    runA.RootLane, "", CancellationToken.None);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(StopReason.EndTurn, completedA.StopReason);
            Assert.Contains(completedA.ToolCalls, trace => trace.ToolName == "filesystem.read" && trace.Succeeded);
            Assert.All(resumedScopes, scope => Assert.Equal(new ExecutionScopeState(runA.RunId,
                runA.RootTask, runA.RootLane, suspendedTurn), scope));

            var persistedTurnA = store.ReadFrom(session, 1).Where(evt => evt.RunId == runA.RunId
                && evt.Type.ToString() == "turn.started").Select(evt => evt.TurnId).ToArray();
            Assert.Equal(new[] { suspendedTurn }, persistedTurnA);

            // Error returns also restore a caller's ambient scope; then terminal Run early return
            // must not invoke the model or disturb the same parent scope.
            var errorTurn = MakeTurn((_, _) => throw new InvalidOperationException("scripted failure"),
                executor, catalog, questionnaires, store, codecs, artifacts);
            ExplorerTurn.TurnResult error;
            using (ExecutionScope.Begin(outside))
            {
                error = errorTurn.Ask("force error", "system", session, runB.RunId,
                    runB.RootLane, "", CancellationToken.None);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(StopReason.Error, error.StopReason);

            var terminalStream = new EventStream(store, codecs, session);
            terminalStream.Append(new LaneCompleted(runB.RootLane, null));
            terminalStream.Append(new TaskCompleted(runB.RootTask, null));
            terminalStream.Append(new RunValidationStarted(runB.RunId));
            terminalStream.Append(new RunCompleted(runB.RunId, RunOutcome.Completed));
            var terminalCalls = 0;
            var terminalTurn = MakeTurn((_, _) =>
            {
                terminalCalls++;
                return Final("must not be called");
            }, executor, catalog, questionnaires, store, codecs, artifacts);
            using (ExecutionScope.Begin(outside))
            {
                var terminal = terminalTurn.Ask("terminal", "system", session, runB.RunId,
                    runB.RootLane, "", CancellationToken.None);
                Assert.Equal(StopReason.Error, terminal.StopReason);
                Assert.Equal(outside, ExecutionScope.Current);
            }
            Assert.Equal(0, terminalCalls);
            Assert.Null(ExecutionScope.Current);

            store.Close();
            store = new SqliteEventStore(journal);
            var persisted = store.ReadFrom(session, 1);
            var turnAId = Assert.IsType<TurnId>(persisted.Single(evt => evt.RunId == runA.RunId
                && evt.Type.ToString() == "turn.started").TurnId);
            var turnBIds = persisted.Where(evt => evt.RunId == runB.RunId
                && evt.Type.ToString() == "turn.started").Select(evt => evt.TurnId).Distinct().ToArray();
            Assert.Equal(2, turnBIds.Length); // B normal completion plus the deliberate error Turn
            var toolEvents = persisted.Where(evt => evt.Type.ToString().StartsWith("toolcall.", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(toolEvents);
            foreach (var evt in toolEvents.Where(evt => evt.RunId == runA.RunId))
            {
                Assert.Equal(runA.RootTask, evt.TaskId);
                Assert.Equal(runA.RootLane, evt.LaneId);
                Assert.Equal(turnAId, evt.TurnId);
            }
            foreach (var evt in toolEvents.Where(evt => evt.RunId == runB.RunId))
            {
                Assert.Equal(runB.RootTask, evt.TaskId);
                Assert.Equal(runB.RootLane, evt.LaneId);
                Assert.Contains(evt.TurnId, turnBIds);
            }
            Assert.Contains(toolEvents, evt => evt.RunId == runA.RunId && evt.TurnId == turnAId);
            Assert.Contains(toolEvents, evt => evt.RunId == runB.RunId && turnBIds.Contains(evt.TurnId));
            Assert.All(bScopeSnapshots, scope => Assert.Equal(new ExecutionScopeState(runB.RunId,
                runB.RootTask, runB.RootLane, turnBIds[0]), scope));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete,
        IToolExecutor executor, FakeCatalog catalog, QuestionnaireInteractionService questionnaires,
        SqliteEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        Func<InteractionId, QuestionnaireSchema, QuestionnaireAskOutcome?>? responder = null) =>
        new(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            Fingerprint, Selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            questionnaires: questionnaires, questionnaireResponder: responder);

    private static ModelResponse ToolCall(string name, string arguments) =>
        new(new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "script-" + name, name, arguments) },
            StopReason.ToolUse, new TokenUsage(2, 1, 0, 0, 0), null,
            new ProviderMetadata("scripted", "test", null));

    private static ModelResponse Final(string text) =>
        new(new ContentBlock[] { new TextBlock(text) }, StopReason.EndTurn,
            new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
}
