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

/// <summary>
/// Regression fixtures for spend reported before user.ask suspends a Turn, before a final
/// ModelCompleted exists. Each first segment stays below its configured caps and is expected to
/// reach the questionnaire. SQLite is reopened before checking spend from another session or
/// resuming the same Run. RED until suspended-segment usage is durably accounted for.
/// </summary>
public sealed class SuspendedSpendAccountingRegressionTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose an approach", null,
        new QuestionField[]
        {
            new("approach", "Which approach?", null, QuestionKind.SingleChoice,
                new[] { new QuestionOption("safe", "Safe", null), new QuestionOption("fast", "Fast", null) },
                null, true, null, null, null),
        });

    [Fact]
    public void Suspended_spend_in_prior_session_blocks_before_provider_under_daily_cap()
    {
        var root = NewRoot("daily");
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var sessionA = SessionId.New();
            var runA = TestRun.Open(store, sessionA, mode: RunMode.Plan);
            var interaction = StartQuestionAndSuspend(store, codecs, artifacts, root,
                sessionA, runA.RunId, runA.RootLane, sessionCapUsd: 20m, dailyCapUsd: 20m);
            Assert.NotNull(interaction);
            Assert.Empty(store.ReadFrom(sessionA, 1).Select(codecs.Decode).OfType<ModelCompleted>());

            // The test measures durable state, not the ExplorerTurn instance's in-memory Usage.
            store.Close();
            store = new SqliteEventStore(journal);
            var sessionB = SessionId.New();
            var runB = TestRun.Open(store, sessionB);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = MakeTurn((_, _) =>
            {
                calls++;
                return FinalResponse();
            }, store, codecs, artifacts, executor, catalog, service, sessionCapUsd: 100m,
                dailyCapUsd: 5m);

            var result = turn.Ask("new request", "system", sessionB, runB.RunId, runB.RootLane,
                "", CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Contains(store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.BudgetExceeded);
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Suspended_spend_below_daily_cap_allows_provider_in_next_session()
    {
        var root = NewRoot("daily-control");
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var sessionA = SessionId.New();
            var runA = TestRun.Open(store, sessionA, mode: RunMode.Plan);
            Assert.NotNull(StartQuestionAndSuspend(store, codecs, artifacts, root,
                sessionA, runA.RunId, runA.RootLane, sessionCapUsd: 20m, dailyCapUsd: 20m));
            Assert.Empty(store.ReadFrom(sessionA, 1).Select(codecs.Decode).OfType<ModelCompleted>());

            store.Close();
            store = new SqliteEventStore(journal);
            var sessionB = SessionId.New();
            var runB = TestRun.Open(store, sessionB);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = MakeTurn((_, _) =>
            {
                calls++;
                return FinalResponse();
            }, store, codecs, artifacts, executor, catalog, service, sessionCapUsd: 100m,
                dailyCapUsd: 15m);

            var result = turn.Ask("new request", "system", sessionB, runB.RunId, runB.RootLane,
                "", CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Resuming_same_run_rejects_before_provider_when_new_session_cap_is_below_suspended_spend()
    {
        var root = NewRoot("resume");
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var interactionId = StartQuestionAndSuspend(store, codecs, artifacts, root,
                session, run.RunId, run.RootLane, sessionCapUsd: 20m, dailyCapUsd: 100m);
            Assert.NotNull(interactionId);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "lastsession.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(interactionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            // RunCreated's MaxCostUsd is immutable through the public API. Recreate the Host with
            // a stricter configured session cap to exercise the same durable-spend preflight on
            // this Run without rewriting canonical history or inventing a budget-change event.
            var turn = MakeTurn((_, _) =>
            {
                calls++;
                return FinalResponse();
            }, store, codecs, artifacts, executor, catalog, service, sessionCapUsd: 5m,
                dailyCapUsd: 100m);

            var result = turn.Ask("continue", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                item => item.Kind == InteractionKind.BudgetExceeded);
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Resuming_same_run_allows_provider_when_new_session_cap_covers_suspended_spend()
    {
        var root = NewRoot("resume-control");
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var interactionId = StartQuestionAndSuspend(store, codecs, artifacts, root,
                session, run.RunId, run.RootLane, sessionCapUsd: 20m, dailyCapUsd: 100m);
            Assert.NotNull(interactionId);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "lastsession.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(interactionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = MakeTurn((_, _) =>
            {
                calls++;
                return FinalResponse();
            }, store, codecs, artifacts, executor, catalog, service, sessionCapUsd: 15m,
                dailyCapUsd: 100m);

            var result = turn.Ask("continue", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
        }
        finally { Cleanup(store, journal, root); }
    }

    private static InteractionId? StartQuestionAndSuspend(SqliteEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, string workspace, SessionId session, RunId run, LaneId lane,
        decimal sessionCapUsd, decimal dailyCapUsd)
    {
        var service = new QuestionnaireInteractionService(store, codecs, artifacts);
        var catalog = new FakeCatalog().Add(new UserAskTool());
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), workspace);
        var result = MakeTurn((_, _) => new ModelResponse(new ContentBlock[]
        {
            new ToolCallBlock(ToolCallId.New(), "provider-call", "user.ask", QuestionnaireCodec.EncodeSchema(Schema)),
        }, StopReason.ToolUse, new TokenUsage(10_000_000, 0, 0, 0, 0), null,
            new ProviderMetadata("scripted", "", null)), store, codecs, artifacts, executor, catalog,
            service, sessionCapUsd, dailyCapUsd)
            .Ask("ask the user", "system", session, run, lane, "", CancellationToken.None);

        Assert.Equal(StopReason.InputRequired, result.StopReason);
        Assert.Equal(10_000_000, result.Usage.Input);
        Assert.NotNull(result.PendingInteractionId);
        return result.PendingInteractionId;
    }

    private static ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete,
        IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts, IToolExecutor executor,
        FakeCatalog catalog, QuestionnaireInteractionService service, decimal sessionCapUsd, decimal dailyCapUsd) =>
        new(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
            sessionCapUsd: sessionCapUsd, dailyCapUsd: dailyCapUsd, questionnaires: service);

    private static ModelResponse FinalResponse() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-suspended-spend-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(SqliteEventStore? store, string journal, string root)
    {
        store?.Close();
        using var connection = new SqliteConnection("DataSource=" + journal);
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
