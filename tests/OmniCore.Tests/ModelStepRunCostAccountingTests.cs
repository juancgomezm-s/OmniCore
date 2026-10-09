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

public sealed class ModelStepRunCostAccountingTests
{
    private static readonly QuestionnaireSchema Schema = new("Continue?", null,
        new QuestionField[] { new("choice", "Choice", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("yes", "Yes", null), new QuestionOption("no", "No", null) }, null, true, null, null, null) });

    [Fact]
    public void Run_cap_counts_a_multi_step_segment_once()
    {
        var root = NewRoot();
        var store = new InMemoryEventStore();
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = OpenRun(store, codecs, session, 15m);
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = FakeCatalog.Default();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var callCount = 0;
            var turn = MakeTurn(store, codecs, artifacts, session, run.RunId, catalog, executor,
                new ModelPricing(1m, 1m), (_, _) =>
                {
                    callCount++;
                    return callCount == 1
                        ? ToolUseResponse("fake.read", new TokenUsage(10_000_000, 0, 0, 0, 0))
                        : FinalResponse(new TokenUsage(1_000_000, 0, 0, 0, 0));
                });

            var result = turn.Ask("request", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            var completed = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>().Single();
            using var summary = System.Text.Json.JsonDocument.Parse(artifacts.GetText(completed.ResponseArtifact!.Hash)!);
            Assert.Equal("11", summary.RootElement.GetProperty("costUsd").GetString());
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void Final_summary_uses_confirmed_step_costs_when_pricing_changes_between_suspended_segments()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var first = MakeTurn(store, codecs, artifacts, session, run.RunId, catalog, executor,
                new ModelPricing(1m, 1m), (_, _) => ToolUseResponse("user.ask",
                    new TokenUsage(10_000_000, 0, 0, 0, 0)), service);

            var suspended = first.Ask("ask", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var state = Path.Combine(root, "session.txt");
            File.WriteAllText(state, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), state, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
                new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) }, false).Status);

            catalog = new FakeCatalog().Add(new UserAskTool());
            executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var resumed = MakeTurn(store, codecs, artifacts, session, run.RunId, catalog, executor,
                new ModelPricing(2m, 2m), (_, _) => FinalResponse(new TokenUsage(1_000_000, 0, 0, 0, 0)), service);
            var result = resumed.Ask("continue", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            var summaryEvent = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>().Single();
            using var summary = System.Text.Json.JsonDocument.Parse(artifacts.GetText(summaryEvent.ResponseArtifact!.Hash)!);
            Assert.Equal("12", summary.RootElement.GetProperty("costUsd").GetString());
            var stepCosts = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>()
                .OrderBy(step => step.StepIndex).Select(step => step.CostUsd).ToArray();
            Assert.Equal(new decimal?[] { 10m, 2m }, stepCosts);
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Summary_cost_is_unknown_if_any_invocation_started_without_confirmed_completion()
    {
        var root = NewRoot();
        var store = new InMemoryEventStore();
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var stream = new EventStream(store, codecs, session);
            var turnId = TurnId.New();
            stream.Append(new TurnStarted(turnId, run.RootLane));
            stream.Append(new ModelStepStarted(turnId, 0, "test", 8192, "Direct", null, null, null));
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = FakeCatalog.Default();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = MakeTurn(store, codecs, artifacts, session, run.RunId, catalog, executor,
                new ModelPricing(2m, 2m), (_, _) =>
                {
                    calls++;
                    return FinalResponse(new TokenUsage(1_000_000, 0, 0, 0, 0));
                });

            var result = turn.Ask("resume", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            var summaryEvent = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>().Single();
            using var summary = System.Text.Json.JsonDocument.Parse(artifacts.GetText(summaryEvent.ResponseArtifact!.Hash)!);
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                summary.RootElement.GetProperty("costUsd").ValueKind);
        }
        finally { DeleteRoot(root); }
    }

    private static TestRun.Opened OpenRun(IEventStore store, IEventCodecRegistry codecs,
        SessionId session, decimal maxCost)
    {
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = new TaskBudget(maxCost, null, null, null);
        stream.AppendBatch(new DomainEventPayload[]
        {
            new RunCreated(run, session, "run cost test", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run), new TaskCreated(task, run, "run cost test", Array.Empty<TaskDependency>(), budget),
            new TaskReady(task), new LaneCreated(lane, task, ProfileId.New()), new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Standard);
        return new TestRun.Opened(session, run, task, lane);
    }

    private static ExplorerTurn MakeTurn(IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        SessionId session, RunId run, FakeCatalog catalog, IToolExecutor executor, ModelPricing pricing,
        Func<ModelRequest, CancellationToken, ModelResponse> complete,
        QuestionnaireInteractionService? questionnaires = null) =>
        new(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("test", "h", "t", "c", "o", "M5.5"),
            new ModelSelection(new ModelIdValue("test"), 8192, ToolMode.Direct, null),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            pricing: pricing, questionnaires: questionnaires,
            maximumGenerationRequestAttempts: 1); // each scripted delegate returns one response per request

    private static ModelResponse ToolUseResponse(string name, TokenUsage usage) => new(
        new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "provider-call", name,
            name == "user.ask" ? QuestionnaireCodec.EncodeSchema(Schema) : "{}") },
        StopReason.ToolUse, usage, null, new ProviderMetadata("scripted", "test", null));

    private static ModelResponse FinalResponse(TokenUsage usage) => new(
        new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn, usage, null,
        new ProviderMetadata("scripted", "test", null));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-model-step-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(SqliteEventStore? store, string journal, string root)
    {
        store?.Close();
        using var connection = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(connection);
        DeleteRoot(root);
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
