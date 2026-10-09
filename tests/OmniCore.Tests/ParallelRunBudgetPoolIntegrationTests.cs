namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

/// <summary>Real Host budget admission with independent read lanes, a durable SQLite ledger,
/// blocked fake providers, and journal/ledger replay after closing and reopening SQLite.</summary>
public sealed class ParallelRunBudgetPoolIntegrationTests
{
    [Fact]
    public async Task Two_live_provider_waits_share_finite_run_and_child_token_ceilings_then_replay_after_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-parallel-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journalPath = Path.Combine(root, "journal.db");
        var reservationPath = Path.Combine(root, "reservations.db");
        SqliteEventStore store = new(journalPath);
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(new EventStream(store, codecs, session), session,
            taskBudget: new TaskBudget(null, 18_432, null, null));
        var reservations = new SqliteSpendReservationStore(reservationPath);
        var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
        var runStream = new EventStream(store, codecs, session);
        var rootProfile = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<LaneCreated>()
            .Single(lane => lane.LaneId == run.RootLane).AgentProfile;
        var rootExecution = ExecutionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane,
            ExecutionId: rootExecution)))
            runStream.Append(new AgentExecutionStarted(rootExecution, run.RootLane, rootProfile, null,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed), DurabilityClass.Barrier);

        var lanes = new List<(TaskId Task, LaneId Lane, ProfileId Profile, ExecutionId Execution)>();
        for (var i = 0; i < 2; i++)
        {
            var childTask = TaskId.New();
            var childLane = LaneId.New();
            var profile = ProfileId.New();
            var execution = ExecutionId.New();
            var scope = new ExecutionScopeState(run.RunId, childTask, childLane, ExecutionId: execution);
            using (ExecutionScope.Begin(scope))
                runStream.AppendBatch(new DomainEventPayload[]
                {
                    new TaskCreated(childTask, run.RunId, "bounded read lane " + i, Array.Empty<TaskDependency>(),
                        new TaskBudget(null, 9_216, null, null), run.RootTask),
                    new TaskReady(childTask),
                    new LaneCreated(childLane, childTask, profile),
                    new LaneStarted(childLane),
                    new TaskStarted(childTask, childLane),
                    new AgentExecutionStarted(execution, childLane, profile, rootExecution,
                        ExecutionRelation.Awaited, ExecutionSupervision.Managed),
                }, DurabilityClass.Barrier);
            lanes.Add((childTask, childLane, profile, execution));
        }

        using var arrivals = new CountdownEvent(2);
        using var releaseProviders = new ManualResetEventSlim();
        var providerCalls = 0;
        ExplorerTurn MakeTurn() => new((_, cancellationToken) =>
        {
            Interlocked.Increment(ref providerCalls);
            arrivals.Signal();
            if (!releaseProviders.Wait(TimeSpan.FromSeconds(15), cancellationToken))
                throw new TimeoutException("Parallel provider fixture release expired.");
            return new ModelResponse([new TextBlock("bounded fixture result")], StopReason.EndTurn,
                new TokenUsage(90, 10, 0, 0, 0), null,
                new ProviderMetadata("offline", "fixture", null), TokenUsageFields.All);
        }, ScriptedToolExecutor.WithWorkspace(new FakeCatalog(), new ScriptedPermissionPolicy([]), root),
            new FakeCatalog(), new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
            new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null, maxOutputTokens: 1024),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            modelContextCapacity: 8192, spendReservations: reservations, maximumGenerationRequestAttempts: 1);

        var workers = lanes.Select(lane => Task.Run(() =>
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, lane.Task, lane.Lane,
                ExecutionId: lane.Execution));
            return MakeTurn().Ask("read", "read-only", session, run.RunId, lane.Lane, "",
                TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken)).ToArray();

        try
        {
            Assert.True(await Task.Run(() => arrivals.Wait(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken),
                "Both independent Lane-owned providers must enter before either is released.");
            Assert.Equal(2, providerCalls);
            var pendingJournal = store.ReadFrom(session, 1);
            Assert.Equal(2, pendingJournal.Select(codecs.Decode).OfType<ModelStepStarted>().Count());
            var pool = RunBudgetPool.For(store);
            var pendingTokens = RunTokenBudgetReader.Read(pendingJournal, codecs, run.RunId, 18_432,
                (evt, step) => pool.OwnsPendingPrimary(evt, step, reservations));
            Assert.Equal(18_432, pendingTokens.Remaining);
            Assert.Equal(18_432m, reservations.ReadPendingAmount(RunBudgetPool.TokenReservationIdentity(
                RunBudgetPool.PrimaryIdentity(session, run.RunId, lanes[0].Lane,
                    pendingJournal.Select(codecs.Decode).OfType<TurnStarted>().Single(turn => turn.LaneId == lanes[0].Lane).TurnId, 0))!)
                + reservations.ReadPendingAmount(RunBudgetPool.TokenReservationIdentity(
                    RunBudgetPool.PrimaryIdentity(session, run.RunId, lanes[1].Lane,
                        pendingJournal.Select(codecs.Decode).OfType<TurnStarted>().Single(turn => turn.LaneId == lanes[1].Lane).TurnId, 0))!));

            var recoveryStore = new SqliteEventStore(journalPath);
            var recovered = recoveryStore.ReadFrom(session, 1);
            Assert.Null(RunTokenBudgetReader.Read(recovered, codecs, run.RunId, 18_432).Remaining);
            recoveryStore.Close();
            SqliteConnection.ClearPool((SqliteConnection)recoveryStore.Connection);
            recoveryStore.Connection.Dispose();
            var beforeExtraDispatch = providerCalls;
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane,
                ExecutionId: rootExecution)))
            {
                _ = MakeTurn().Ask("extra", "read-only", session, run.RunId, run.RootLane, "",
                    TestContext.Current.CancellationToken);
            }
            Assert.Equal(beforeExtraDispatch, providerCalls);
            Assert.Equal(2, store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>().Count());
        }
        finally { releaseProviders.Set(); }

        var results = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.All(results, result => Assert.Equal(StopReason.EndTurn, result.StopReason));
        Assert.Equal(2, providerCalls);
        var settled = store.ReadFrom(session, 1);
        Assert.Equal(2, settled.Select(codecs.Decode).OfType<ModelStepCompleted>().Count());
        Assert.Equal(200, RunTokenBudgetReader.Read(settled, codecs, run.RunId, 18_432).ObservedSettledTokens);
        Assert.Equal(0m, settled.Select(codecs.Decode).OfType<ModelStepStarted>().Sum(step =>
            reservations.ReadPendingAmount(RunBudgetPool.TokenReservationIdentity(
                RunBudgetPool.PrimaryIdentity(session, run.RunId,
                    settled.Select(codecs.Decode).OfType<TurnStarted>().Single(turn => turn.TurnId == step.TurnId).LaneId,
                    step.TurnId, step.StepIndex))!)));

        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
        store = new SqliteEventStore(journalPath);
        var reopened = store.ReadFrom(session, 1);
        var replay = RunTokenBudgetReader.Read(reopened, codecs, run.RunId, 18_432);
        Assert.Equal(18_232, replay.Remaining);
        Assert.Equal(200, replay.ObservedSettledTokens);

        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
        Directory.Delete(root, recursive: true);
    }
}
