using System.Globalization;
using System.Text.Json;
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
public sealed class CrossSessionDailyCapRegressionTests
{
    [Fact]
    public void Session_only_reader_does_not_block_a_run_cost_budget_with_complete_local_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-run-only-spend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var sqlite = new SqliteEventStore(journal);
        try
        {
            IEventStore store = new SessionOnlyStore(sqlite);
            var session = SessionId.New();
            var codecs = EventCodecs.Create();
            var run = OpenRunWithCost(store, codecs, session, 10m);
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            AppendCompletedTurnWithCost(store, codecs, artifacts, session, run.RunId, run.RootLane, 2m);

            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: false,
                sessionCapUsd: 100m, dailyCapUsd: 20m);

            var result = turn.Ask("question", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, calls);
        }
        finally
        {
            sqlite.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData(false, StopReason.EndTurn, 1)]
    [InlineData(true, StopReason.Cancelled, 0)]
    public void Stores_without_workspace_reader_preserve_uncapped_behavior_and_fail_closed_when_capped(
        bool enforceCaps, StopReason expectedStop, int expectedCalls)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-only-spend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var sqlite = new SqliteEventStore(journal);
        try
        {
            IEventStore store = new SessionOnlyStore(sqlite);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: enforceCaps,
                sessionCapUsd: 100m, dailyCapUsd: 20m);

            var result = turn.Ask("question", "system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(expectedStop, result.StopReason);
            Assert.Equal(expectedCalls, calls);
        }
        finally
        {
            sqlite.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Prior_session_cost_from_another_utc_day_does_not_count_against_today()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-cross-session-day-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var priorSession = SessionId.New();
            var priorRun = TestRun.Open(store, priorSession);
            AppendUsageCompletion(store, codecs, artifacts, priorSession, priorRun.RunId,
                day: yesterday, costUsd: "21");

            var currentSession = SessionId.New();
            var currentRun = TestRun.Open(store, currentSession);
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1_000_000, 0, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: 5m, dailyCapUsd: 20m);

            var result = turn.Ask("today", "system", currentSession, currentRun.RunId,
                currentRun.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, calls);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Incomplete_workspace_usage_fails_closed_before_provider_call(bool missingArtifact,
        bool malformedDay)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-cross-session-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var priorSession = SessionId.New();
            var priorRun = TestRun.Open(store, priorSession);
            AppendUsageCompletion(store, codecs, artifacts, priorSession, priorRun.RunId,
                day: malformedDay ? "not-a-UTC-day" : "2026-10-04",
                costUsd: missingArtifact || malformedDay ? "1" : null, includeArtifact: !missingArtifact);

            var currentSession = SessionId.New();
            var currentRun = TestRun.Open(store, currentSession);
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: 100m, dailyCapUsd: 20m);

            var result = turn.Ask("today", "system", currentSession, currentRun.RunId,
                currentRun.RootLane, "", CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Contains(store.ReadFrom(currentSession, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                interaction => interaction.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Daily_cap_blocks_new_session_after_prior_session_exceeds_same_day_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-cross-session-spend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, bool enforceCaps) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: enforceCaps,
                sessionCapUsd: 100m, dailyCapUsd: 20m);
            static ModelResponse Response(long input) => new(new ContentBlock[] { new TextBlock("done") },
                StopReason.EndTurn, new TokenUsage(input, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            var sessionA = SessionId.New();
            var runA = TestRun.Open(store, sessionA);
            var callsA = 0;
            var fixtureDay = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var first = MakeTurn((_, _) => { callsA++; return Response(21_000_000); }, false)
                .Ask("first", "system", sessionA, runA.RunId, runA.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            Assert.Equal(1, callsA);
            var completedA = Assert.Single(store.ReadFrom(sessionA, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            Assert.NotNull(completedA.ResponseArtifact);
            var payload = artifacts.GetText(completedA.ResponseArtifact.Hash);
            Assert.NotNull(payload);
            using var record = JsonDocument.Parse(payload);
            Assert.Equal(21m, decimal.Parse(record.RootElement.GetProperty("costUsd").GetString()!, CultureInfo.InvariantCulture));
            var day = record.RootElement.GetProperty("day").GetString();
            Assert.True(day == fixtureDay,
                $"Artifact day '{day}' differs from captured fixture day '{fixtureDay}'; fixture boundary issue, not a product cap result");
            Assert.Equal(runA.RunId.ToString(), record.RootElement.GetProperty("runId").GetString());
            var sessionB = SessionId.New();
            var runB = TestRun.Open(store, sessionB);
            Assert.NotEqual(sessionA, sessionB);
            Assert.NotEqual(runA.RunId, runB.RunId);
            Assert.Empty(store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var callsB = 0;
            var second = MakeTurn((_, _) => { callsB++; return Response(1); }, true)
                .Ask("second", "system", sessionB, runB.RunId, runB.RootLane, "", CancellationToken.None);
            Assert.True(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == fixtureDay,
                "Fixture crossed UTC date boundary; not a product cap result");
            Assert.Equal(0, callsB);
            Assert.Equal(StopReason.Cancelled, second.StopReason);
            Assert.Contains(store.ReadFrom(sessionB, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                interaction => interaction.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Negative_legacy_cost_in_prior_session_fails_closed_under_daily_cap()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-cross-session-negative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var priorSession = SessionId.New();
            var priorRun = TestRun.Open(store, priorSession);
            AppendUsageCompletion(store, codecs, artifacts, priorSession, priorRun.RunId,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "-1");

            var currentSession = SessionId.New();
            var currentRun = TestRun.Open(store, currentSession);
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 0, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: 100m, dailyCapUsd: 20m);

            var result = turn.Ask("today", "system", currentSession, currentRun.RunId,
                currentRun.RootLane, "", CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Contains(store.ReadFrom(currentSession, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                interaction => interaction.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AppendUsageCompletion(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, SessionId sessionId, RunId runId, string day, string? costUsd,
        bool includeArtifact = true)
    {
        var record = "{\"omnicoreUsage\":1,\"response\":\"prior\",\"runId\":\""
            + runId + "\",\"day\":\"" + day + "\",\"input\":1,\"output\":0"
            + ",\"cacheRead\":0,\"cacheWrite\":0,\"reasoning\":0,\"costUsd\":"
            + (costUsd is null ? "null" : "\"" + costUsd + "\"") + "}";
        var artifact = includeArtifact
            ? artifacts.PutText(record, "application/vnd.omnicore.model-usage+json",
                ArtifactKind.ModelResponse, Sensitivity.Sensitive)
            : null;
        var payload = new ModelCompleted(TurnId.New(), artifact);
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        var evt = DomainEvent.Create(sessionId, payload.Type(), payload.SchemaVersion(), null,
            runId, runId, null, null, payload.TurnId, null, null,
            artifact is null ? Array.Empty<ArtifactRef>() : new[] { artifact }, json);
        store.Append(sessionId, evt, DurabilityClass.Standard, CancellationToken.None);
    }

    private static TestRun.Opened OpenRunWithCost(IEventStore store, IEventCodecRegistry codecs,
        SessionId sessionId, decimal maxCostUsd)
    {
        var runId = RunId.New();
        var taskId = TaskId.New();
        var laneId = LaneId.New();
        var budget = new TaskBudget(maxCostUsd, null, null, null);
        var stream = new EventStream(store, codecs, sessionId);
        stream.AppendBatch(new DomainEventPayload[]
        {
            new RunCreated(runId, sessionId, "run budget", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, taskId, DateTimeOffset.UtcNow),
            new RunStarted(runId),
            new TaskCreated(taskId, runId, "run budget", Array.Empty<TaskDependency>(), budget),
            new TaskReady(taskId),
            new LaneCreated(laneId, taskId, ProfileId.New()),
            new LaneStarted(laneId),
            new TaskStarted(taskId, laneId),
        }, DurabilityClass.Standard);
        return new TestRun.Opened(sessionId, runId, taskId, laneId);
    }

    private static void AppendCompletedTurnWithCost(IEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, SessionId sessionId, RunId runId, LaneId laneId, decimal costUsd)
    {
        var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stream = new EventStream(store, codecs, sessionId);
        var turnId = TurnId.New();
        var usagePayload = "{\"omnicoreUsage\":1,\"response\":\"prior\",\"runId\":\""
            + runId + "\",\"day\":\"" + day + "\",\"input\":2,\"output\":0"
            + ",\"cacheRead\":0,\"cacheWrite\":0,\"reasoning\":0,\"costUsd\":\""
            + costUsd.ToString(CultureInfo.InvariantCulture) + "\"}";
        var artifact = artifacts.PutText(usagePayload, "application/vnd.omnicore.model-usage+json",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        stream.Append(new TurnStarted(turnId, laneId));
        stream.Append(new ModelCompleted(turnId, artifact));
        stream.Append(new TurnCompleted(turnId));
    }

    private sealed class SessionOnlyStore(SqliteEventStore inner) : IEventStore
    {
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.Append(sessionId, evt, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.AppendBatch(sessionId, evts, durability, cancellationToken);

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
