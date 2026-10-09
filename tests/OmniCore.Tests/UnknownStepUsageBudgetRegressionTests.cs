namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class UnknownStepUsageBudgetRegressionTests
{
    [Theory]
    [InlineData(TokenUsageFields.None, true)]
    [InlineData(TokenUsageFields.Input, true)]
    [InlineData(TokenUsageFields.None, false)]
    [InlineData(TokenUsageFields.Input, false)]
    [InlineData(TokenUsageFields.Output, true)]
    [InlineData(TokenUsageFields.Output, false)]
    public void Unknown_or_partial_usage_stops_before_tools_and_next_model_step(
        TokenUsageFields reportedFields, bool runCostCapOnly)
    {
        var fixture = OpenFixture(runCostCapOnly ? 1m : null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: !runCostCapOnly,
                (_, _) =>
                {
                    providerCalls++;
                    return providerCalls == 1 ? ToolUseResponse(reportedFields) : EndTurnResponse();
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.Single(events.OfType<ModelStepCompleted>(), completed =>
                completed.CostUsd is null && completed.ReportedUsageFields == reportedFields);
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            var budgetRequest = Assert.Single(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", budgetRequest.OptionsJson, StringComparison.Ordinal);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Fact]
    public void Same_unknown_usage_tool_response_can_continue_when_no_monetary_caps_are_enabled()
    {
        var fixture = OpenFixture(runCostUsd: null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: false,
                (_, _) =>
                {
                    providerCalls++;
                    return providerCalls == 1
                        ? ToolUseResponse(TokenUsageFields.None)
                        : EndTurnResponse();
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, providerCalls);
            Assert.Equal(1, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.Equal(2, events.OfType<ModelStepCompleted>().Count());
            Assert.Contains(events, payload => payload is ToolCallStarted);
            Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                && request.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Theory]
    [InlineData(TokenUsageFields.All)]
    [InlineData(TokenUsageFields.Input | TokenUsageFields.Output)]
    public void Measured_zero_cost_can_continue_under_caps(TokenUsageFields reportedFields)
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var calls = 0;
            var turn = MakeTurn(fixture, countedTools, true, (_, _) =>
                ++calls == 1 ? ToolUseResponse(reportedFields) : EndTurnResponse());

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, calls);
            Assert.Equal(1, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = events.OfType<ModelStepCompleted>().ToArray();
            Assert.Equal(2, completed.Length);
            Assert.Equal(0m, completed[0].CostUsd);
            Assert.Equal(reportedFields, completed[0].ReportedUsageFields);
            Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                && request.Kind == InteractionKind.BudgetExceeded);
        }
        finally { fixture.DeleteArtifacts(); }
    }

    [Fact]
    public void Unknown_step_cost_remains_unknown_after_sqlite_reopen_and_blocks_daily_spend_in_another_session()
    {
        var fixture = OpenFixture(null, durable: true);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var firstCalls = 0;
            var first = MakeTurn(fixture, countedTools, true, (_, _) =>
            {
                firstCalls++;
                return ToolUseResponse(TokenUsageFields.None);
            });
            Assert.Equal(StopReason.Cancelled, first.Ask("first", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None).StopReason);
            Assert.Equal(1, firstCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var previousSequence = fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence;
            ((SqliteEventStore)fixture.Store).Close();
            fixture = fixture with { Store = new SqliteEventStore(Path.Combine(fixture.TempRoot, "journal.db")) };
            var persisted = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
                .Select(fixture.Codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Null(persisted.CostUsd);
            Assert.Equal(TokenUsageFields.None, persisted.ReportedUsageFields);
            var otherSession = SessionId.New();
            var otherRun = TestRun.Open(fixture.Store, otherSession);
            var secondFixture = fixture with { Session = otherSession, Run = otherRun.RunId, Lane = otherRun.RootLane };
            var secondCalls = 0;
            var second = MakeTurn(secondFixture, countedTools, true, (_, _) =>
            {
                secondCalls++;
                return EndTurnResponse();
            });

            Assert.Equal(StopReason.Cancelled, second.Ask("second", "system", otherSession,
                otherRun.RunId, otherRun.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(0, secondCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            Assert.Equal(previousSequence, fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence);
            var secondEvents = fixture.Store.ReadFrom(otherSession, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.DoesNotContain(secondEvents, payload => payload is ModelStepStarted or ModelStepCompleted);
            var request = Assert.Single(secondEvents.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
        }
        finally { fixture.DeleteArtifacts(); }
    }

    private sealed record Fixture(IEventStore Store, IEventCodecRegistry Codecs, SessionId Session,
        RunId Run, LaneId Lane, string TempRoot, IToolExecutor Executor)
    {
        public void DeleteArtifacts()
        {
            if (Store is SqliteEventStore sqlite)
            {
                sqlite.Close();
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(TempRoot, "journal.db"));
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
            try { Directory.Delete(TempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Fixture OpenFixture(decimal? runCostUsd, bool durable = false)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "omnicore-unknown-step-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        IEventStore store = durable ? new SqliteEventStore(Path.Combine(tempRoot, "journal.db"))
            : new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = new TaskBudget(runCostUsd, null, null, null);
        var stream = new EventStream(store, codecs, session);
        stream.AppendBatch(new DomainEventPayload[]
        {
            new RunCreated(run, session, "usage guard regression", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run),
            new TaskCreated(task, run, "usage guard regression", Array.Empty<TaskDependency>(), budget),
            new TaskReady(task),
            new LaneCreated(lane, task, ProfileId.New()),
            new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Barrier);

        var catalog = new FakeCatalog().Add(FakeTool.Read("fake.read"));
        var inner = ScriptedToolExecutor.WithWorkspace(catalog,
            ScriptedPermissionPolicy.WithTool("fake.read", PermissionDecision.Allow), tempRoot);
        return new Fixture(store, codecs, session, run, lane, tempRoot, inner);
    }

    private static ExplorerTurn MakeTurn(Fixture fixture, IToolExecutor executor, bool enforceDefaultCaps,
        Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(complete, executor,
        new FakeCatalog().Add(FakeTool.Read("fake.read")),
        new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
        new ExecutionFingerprint("budget-test-model", "h", "t", "context", "o", "test-build"),
        new ModelSelection(new ModelIdValue("budget-test-model"), 128, ToolMode.Direct, null),
        fixture.Store, fixture.Codecs,
        new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts")), new InMemoryAuditSink(),
        new RedactionPolicy(), pricing: new ModelPricing(1m, 1m),
        enforceDefaultSpendCaps: enforceDefaultCaps, sessionCapUsd: 5m, dailyCapUsd: 20m,
        maximumGenerationRequestAttempts: 1); // one scripted response per model request

    private static ModelResponse ToolUseResponse(TokenUsageFields reportedFields) => new(
        new ContentBlock[]
        {
            new ToolCallBlock(ToolCallId.New(), "fixture-provider-call", "fake.read", "{}"),
        }, StopReason.ToolUse,
        reportedFields == TokenUsageFields.Input ? new TokenUsage(1, 0, 0, 0, 0)
            : new TokenUsage(0, 0, 0, 0, 0),
        null, new ProviderMetadata("scripted", "budget-test-model", null), reportedFields);

    private static ModelResponse EndTurnResponse() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("scripted", "budget-test-model", null));

    private sealed class CountingExecutor(IToolExecutor inner) : IToolExecutor
    {
        public int ExecuteCount { get; private set; }

        public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
            CancellationToken cancellationToken, EventStream stream)
        {
            ExecuteCount++;
            return inner.ExecuteTool(validated, userApprovesAsk, cancellationToken, stream);
        }
    }
}
