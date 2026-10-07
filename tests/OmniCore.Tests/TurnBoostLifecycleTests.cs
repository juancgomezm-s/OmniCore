using Microsoft.Data.Sqlite;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class TurnBoostLifecycleTests
{
    [Fact]
    public void Act_gate_retry_uses_boost_for_first_durable_turn_only()
    {
        using var fx = new InternalExplorerAskCommandTests.Fixture(RunMode.Act);
        var catalog = new FakeCatalog();
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), fx.Root);
        var artifacts = new FileArtifactStore(Path.Combine(fx.Root, "boost-lifecycle-artifacts"));
        var requested = new ReasoningRequest("high", null);
        var boostId = Guid.NewGuid();
        var boost = new ReasoningResolution(requested, requested, ReasoningSelectionSource.TurnBoost,
            turnBoostId: boostId);
        var baseline = new ReasoningResolution(null, null, ReasoningSelectionSource.None);
        var seenRequests = new List<(ReasoningRequest? Request, ReasoningResolution? Resolution)>();

        ExplorerTurn MakeTurn(ReasoningResolution resolution) => new((request, _) =>
        {
            seenRequests.Add((request.Reasoning, request.Model.ReasoningResolution));
            return new ModelResponse([new TextBlock("scripted completion")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("offline-turn-boost-fixture", "fixture", null));
        }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("fixture", "hash", "tools", "context", "output", "build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct,
                resolution.AppliedRequest, maxOutputTokens: 2048, reasoningResolution: resolution),
            fx.Store, fx.Codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());

        var consumed = new List<Guid>();
        var acceptanceCalls = 0;
        var runtime = OmniCliRuntime.Create(fx.Root);
        var exit = runtime.RunActLoop(MakeTurn(boost), _ => { }, "fixture objective", "fixture instructions",
            fx.Session, fx.Run, fx.Lane, "", new WorkspaceGatesYaml { Acceptance = true }, null,
            fx.Server, artifacts, new InMemoryAuditSink(), null, interactive: true, "en",
            TestContext.Current.CancellationToken,
            acceptanceResponder: _ => Interlocked.Increment(ref acceptanceCalls) == 1 ? "revise" : "accept",
            instructionSnapshot: new TurnInstructionSnapshot(false, "fixture instructions"),
            subsequentTurnFactory: _ => MakeTurn(baseline), turnBoostId: boostId,
            turnBoostConsumed: consumed.Add);

        Assert.Equal(0, exit);
        Assert.Equal(2, acceptanceCalls);
        Assert.Equal(new[] { boostId }, consumed);
        Assert.Equal(new (ReasoningRequest?, ReasoningResolution?)[]
        {
            (requested, boost),
            (null, baseline),
        }, seenRequests);

        var decoded = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).ToArray();
        var turns = decoded.OfType<TurnStarted>().Where(evt => evt.ReasoningResolution is not null).ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal(boostId, turns[0].ReasoningResolution!.TurnBoostId);
        Assert.Equal(ReasoningSelectionSource.TurnBoost, turns[0].ReasoningResolution!.Source);
        Assert.Null(turns[1].ReasoningResolution!.TurnBoostId);
        Assert.Equal(ReasoningSelectionSource.None, turns[1].ReasoningResolution!.Source);
        var steps = decoded.OfType<ModelStepStarted>().ToArray();
        Assert.Equal(2, steps.Length);
        Assert.All(steps.Where(step => step.TurnId == turns[0].TurnId), step =>
            Assert.True(boost.IsEquivalentTo(step.ReasoningResolution)));
        Assert.All(steps.Where(step => step.TurnId == turns[1].TurnId), step =>
            Assert.True(baseline.IsEquivalentTo(step.ReasoningResolution)));
        Assert.Contains(decoded.OfType<RunCompleted>(), item => item.RunId == fx.Run);
    }

    [Fact]
    public void Boost_resolution_is_replayed_on_same_suspended_turn_after_sqlite_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-boost-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
        var calls = 0;
        var boostId = Guid.NewGuid();
        var request = new ReasoningRequest("high", null);
        var resolution = new ReasoningResolution(request, request, ReasoningSelectionSource.TurnBoost,
            turnBoostId: boostId);
        var schema = new QuestionnaireSchema("Resume boost fixture", null,
            [new QuestionField("choice", "Choice", null, QuestionKind.SingleChoice,
                [new QuestionOption("yes", "Yes", null)], null, true, null, null, null)]);
        try
        {
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts);
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act")
                + "," + JsonObj.Field("objective", "durable boost resume fixture")
                + "," + JsonObj.Field("workspace", root) + "}"), TestContext.Current.CancellationToken).Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var lane = Assert.IsType<LaneId>(server.LastLaneId());
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var questions = new QuestionnaireInteractionService(store, codecs, artifacts);
            var snapshot = new TurnInstructionSnapshot(true, "fixed conversational instruction");

            ExplorerTurn MakeTurn() => new((modelRequest, _) =>
            {
                Assert.Equal(request, modelRequest.Reasoning);
                Assert.True(resolution.IsEquivalentTo(modelRequest.Model.ReasoningResolution));
                calls++;
                return calls == 1
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "fixture-ask", "user.ask",
                        QuestionnaireCodec.EncodeSchema(schema))], StopReason.ToolUse,
                        new TokenUsage(2, 1, 0, 0, 0), null,
                        new ProviderMetadata("offline-boost-resume-fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("resumed")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("offline-boost-resume-fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture", "hash", "tools", "context", "output", "build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, request,
                    maxOutputTokens: 2048, reasoningResolution: resolution),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questions, recordEffectiveFingerprint: true);

            var first = MakeTurn().Ask("ask", snapshot.ResolvedInstruction, session, run, lane, "",
                TestContext.Current.CancellationToken, instructionSnapshot: snapshot);
            Assert.Equal(StopReason.InputRequired, first.StopReason);
            var interaction = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>());
            Assert.Equal(1, calls);
            Close(store);

            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            questions = new QuestionnaireInteractionService(store, codecs, artifacts);
            var resolved = questions.Resolve(new EventStream(store, codecs, session), interaction.InteractionId,
                [new QuestionAnswer("choice", ["yes"], null, null)], false, null,
                new UserInputReceived(run, "[\"QuestionnaireResponse\"]", null,
                    "InteractionResponse(Questionnaire)"));
            Assert.True(resolved.Accepted);

            var resumed = MakeTurn().Ask("", snapshot.ResolvedInstruction, session, run, lane, "",
                TestContext.Current.CancellationToken, instructionSnapshot: snapshot);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal(2, calls);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var turn = Assert.Single(events.OfType<TurnStarted>());
            Assert.True(resolution.IsEquivalentTo(turn.ReasoningResolution));
            Assert.Equal(boostId, turn.ReasoningResolution!.TurnBoostId);
            Assert.Equal(2, events.OfType<ModelStepStarted>().Count());
            Assert.All(events.OfType<ModelStepStarted>(), step =>
                Assert.True(resolution.IsEquivalentTo(step.ReasoningResolution)));
            Assert.Equal(2, events.OfType<ModelStepCompleted>().Count());
            Assert.Contains(events.OfType<TurnCompleted>(), completed => completed.TurnId == turn.TurnId);
        }
        finally
        {
            Close(store);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }
}
