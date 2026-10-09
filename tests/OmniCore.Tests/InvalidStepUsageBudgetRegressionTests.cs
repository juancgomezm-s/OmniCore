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
using Xunit;

public sealed class InvalidStepUsageBudgetRegressionTests
{
    public enum UsageCounter { Input, Output, CacheRead, CacheWrite, Reasoning }

    [Theory]
    [InlineData(UsageCounter.Input, true)]
    [InlineData(UsageCounter.Output, true)]
    [InlineData(UsageCounter.CacheRead, true)]
    [InlineData(UsageCounter.CacheWrite, true)]
    [InlineData(UsageCounter.Reasoning, true)]
    [InlineData(UsageCounter.Input, false)]
    [InlineData(UsageCounter.Output, false)]
    [InlineData(UsageCounter.CacheRead, false)]
    [InlineData(UsageCounter.CacheWrite, false)]
    [InlineData(UsageCounter.Reasoning, false)]
    public void Negative_reported_counter_is_durable_but_blocks_tools_and_next_step(
        UsageCounter counter, bool runCostCapOnly)
    {
        var fixture = OpenFixture(runCostCapOnly ? 1m : null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var originalUsage = NegativeUsage(counter);
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: !runCostCapOnly,
                (_, _) =>
                {
                    providerCalls++;
                    return providerCalls == 1
                        ? ToolUseResponse(originalUsage, TokenUsageFields.All)
                        : EndTurnResponse();
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(originalUsage, completed.Usage);
            Assert.Null(completed.CostUsd);
            Assert.Equal(TokenUsageFields.All, completed.ReportedUsageFields);
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            AssertInvalidSummary(fixture, events);
            var budgetRequest = Assert.Single(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", budgetRequest.OptionsJson, StringComparison.Ordinal);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Theory]
    [InlineData(UsageCounter.Input)]
    [InlineData(UsageCounter.Output)]
    [InlineData(UsageCounter.CacheRead)]
    [InlineData(UsageCounter.CacheWrite)]
    [InlineData(UsageCounter.Reasoning)]
    public void Negative_reported_counter_without_caps_abandons_turn_without_budget_interaction(
        UsageCounter counter)
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var originalUsage = NegativeUsage(counter);
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: false,
                (_, _) =>
                {
                    providerCalls++;
                    return ToolUseResponse(originalUsage, TokenUsageFields.All);
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(originalUsage, completed.Usage);
            Assert.Null(completed.CostUsd);
            Assert.Equal(TokenUsageFields.All, completed.ReportedUsageFields);
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            Assert.Single(events.OfType<TurnAbandoned>());
            Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                && request.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Theory]
    [InlineData(UsageCounter.CacheRead)]
    [InlineData(UsageCounter.CacheWrite)]
    [InlineData(UsageCounter.Reasoning)]
    public void Negative_unreported_auxiliary_counter_does_not_authorize_zero_cost(UsageCounter counter)
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var originalUsage = NegativeUsage(counter);
            const TokenUsageFields reportedFields = TokenUsageFields.Input | TokenUsageFields.Output;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: true,
                (_, _) =>
                {
                    providerCalls++;
                    return ToolUseResponse(originalUsage, reportedFields);
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(originalUsage, completed.Usage);
            Assert.Null(completed.CostUsd);
            Assert.Equal(reportedFields, completed.ReportedUsageFields);
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            AssertInvalidSummary(fixture, events);
            var budgetRequest = Assert.Single(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", budgetRequest.OptionsJson, StringComparison.Ordinal);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Theory]
    [InlineData(UsageCounter.Input)]
    [InlineData(UsageCounter.Output)]
    [InlineData(UsageCounter.CacheRead)]
    [InlineData(UsageCounter.CacheWrite)]
    [InlineData(UsageCounter.Reasoning)]
    public void Usage_counter_overflow_across_steps_preserves_both_steps_then_abandons(
        UsageCounter counter)
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var firstUsage = UsageWith(counter, long.MaxValue);
            var secondUsage = UsageWith(counter, 1);
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: false,
                (_, _) =>
                {
                    providerCalls++;
                    return providerCalls == 1
                        ? ToolUseResponse(firstUsage, TokenUsageFields.All)
                        : EndTurnResponse(secondUsage);
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(2, providerCalls);
            Assert.Equal(1, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = events.OfType<ModelStepCompleted>().OrderBy(step => step.StepIndex).ToArray();
            Assert.Equal(2, completed.Length);
            Assert.Equal(firstUsage, completed[0].Usage);
            Assert.Equal(secondUsage, completed[1].Usage);
            Assert.Equal(ExpectedCost(firstUsage), completed[0].CostUsd);
            Assert.Equal(ExpectedCost(secondUsage), completed[1].CostUsd);
            Assert.Single(events.OfType<TurnAbandoned>());
            Assert.DoesNotContain(events, payload => payload is ModelCompleted or TurnCompleted);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Fact]
    public void Input_plus_output_overflow_in_one_step_keeps_completed_evidence_then_abandons()
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var originalUsage = new TokenUsage(long.MaxValue, 1, 0, 0, 0);
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: false,
                (_, _) =>
                {
                    providerCalls++;
                    return ToolUseResponse(originalUsage, TokenUsageFields.All);
                });

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(originalUsage, completed.Usage);
            Assert.Equal(ExpectedCost(originalUsage), completed.CostUsd);
            Assert.Single(events.OfType<TurnAbandoned>());
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted or ModelCompleted or TurnCompleted);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Fact]
    public void Explicit_zero_input_and_output_usage_remains_measured_and_can_continue()
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var providerCalls = 0;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: true,
                (_, _) => ++providerCalls == 1
                    ? ToolUseResponse(new TokenUsage(0, 0, 0, 0, 0),
                        TokenUsageFields.Input | TokenUsageFields.Output)
                    : EndTurnResponse());

            var result = turn.Ask("question", "system", fixture.Session, fixture.Run,
                fixture.Lane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, providerCalls);
            Assert.Equal(1, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.Equal(2, events.OfType<ModelStepCompleted>().Count());
            var completed = Assert.Single(events.OfType<ModelStepCompleted>(), step => step.StepIndex == 0);
            Assert.Equal(new TokenUsage(0, 0, 0, 0, 0), completed.Usage);
            Assert.Equal(0m, completed.CostUsd);
            Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, completed.ReportedUsageFields);
            Assert.Single(events.OfType<ModelCompleted>());
            Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                && request.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            fixture.DeleteArtifacts();
        }
    }

    [Fact]
    public void Invalid_usage_survives_sqlite_and_cas_reopen_and_blocks_new_session_spending()
    {
        var fixture = OpenFixture(null, durable: true);
        try
        {
            var originalUsage = NegativeUsage(UsageCounter.Output);
            var countedTools = new CountingExecutor(fixture.Executor);
            var calls = 0;
            var first = MakeTurn(fixture, countedTools, true, (_, _) =>
            {
                calls++;
                return ToolUseResponse(originalUsage, TokenUsageFields.All);
            });
            Assert.Equal(StopReason.Cancelled, first.Ask("first", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None).StopReason);
            Assert.Equal(1, calls);
            Assert.Equal(0, countedTools.ExecuteCount);
            var previousSequence = fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence;
            ((SqliteEventStore)fixture.Store).Close();
            fixture = fixture with { Store = new SqliteEventStore(Path.Combine(fixture.TempRoot, "journal.db")) };
            var completed = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
                .Select(fixture.Codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Equal(originalUsage, completed.Usage);
            Assert.Equal(TokenUsageFields.All, completed.ReportedUsageFields);
            Assert.Null(completed.CostUsd);
            var artifacts = new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts"));
            Assert.NotNull(completed.ResponseArtifact);
            Assert.True(artifacts.Verify(completed.ResponseArtifact.Hash, completed.ResponseArtifact.Size));
            var otherSession = SessionId.New();
            var otherRun = TestRun.Open(fixture.Store, otherSession);
            var otherFixture = fixture with { Session = otherSession, Run = otherRun.RunId, Lane = otherRun.RootLane };
            var secondCalls = 0;
            var second = MakeTurn(otherFixture, countedTools, true, (_, _) =>
            {
                secondCalls++;
                return EndTurnResponse();
            });
            Assert.Equal(StopReason.Cancelled, second.Ask("second", "system", otherSession,
                otherRun.RunId, otherRun.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(0, secondCalls);
            Assert.Equal(0, countedTools.ExecuteCount);
            Assert.Equal(previousSequence, fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence);
            var events = fixture.Store.ReadFrom(otherSession, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.DoesNotContain(events, payload => payload is ModelStepStarted or ModelStepCompleted);
            var request = Assert.Single(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.BudgetExceeded);
            Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
        }
        finally { fixture.DeleteArtifacts(); }
    }

    private static void AssertInvalidSummary(Fixture fixture, DomainEventPayload[] events)
    {
        Assert.DoesNotContain(events, payload => payload is ModelCompleted);
        Assert.Single(events.OfType<AssistantMessageRecorded>());
        var audit = Assert.Single(fixture.Audit.Records(), record => record.EventName == "turn.spend");
        Assert.Equal("invalid", audit.Details["usageStatus"]);
        Assert.False(audit.Details.ContainsKey("inputTokens"));
        Assert.False(audit.Details.ContainsKey("outputTokens"));
        Assert.False(audit.Details.ContainsKey("costUsd"));
    }

    private sealed record Fixture(IEventStore Store, IEventCodecRegistry Codecs, SessionId Session,
        RunId Run, LaneId Lane, string TempRoot, IToolExecutor Executor, InMemoryAuditSink Audit)
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
        var tempRoot = Path.Combine(Path.GetTempPath(), "omnicore-invalid-step-usage-" + Guid.NewGuid().ToString("N"));
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
            new RunCreated(run, session, "invalid usage budget regression", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run),
            new TaskCreated(task, run, "invalid usage budget regression", Array.Empty<TaskDependency>(), budget),
            new TaskReady(task),
            new LaneCreated(lane, task, ProfileId.New()),
            new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Barrier);

        var catalog = new FakeCatalog().Add(FakeTool.Read("fake.read"));
        var inner = ScriptedToolExecutor.WithWorkspace(catalog,
            ScriptedPermissionPolicy.WithTool("fake.read", PermissionDecision.Allow), tempRoot);
        return new Fixture(store, codecs, session, run, lane, tempRoot, inner, new InMemoryAuditSink());
    }

    private static ExplorerTurn MakeTurn(Fixture fixture, IToolExecutor executor, bool enforceDefaultCaps,
        Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(complete, executor,
        new FakeCatalog().Add(FakeTool.Read("fake.read")),
        new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
        new ExecutionFingerprint("budget-test-model", "h", "t", "context", "o", "test-build"),
        new ModelSelection(new ModelIdValue("budget-test-model"), 128, ToolMode.Direct, null),
        fixture.Store, fixture.Codecs,
        new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts")), fixture.Audit,
        new RedactionPolicy(), pricing: new ModelPricing(1m, 1m),
        enforceDefaultSpendCaps: enforceDefaultCaps, sessionCapUsd: 5m, dailyCapUsd: 20m,
        maximumGenerationRequestAttempts: 1); // this delegate returns one response per model request

    private static TokenUsage NegativeUsage(UsageCounter counter) => counter switch
    {
        UsageCounter.Input => new TokenUsage(-1, 0, 0, 0, 0),
        UsageCounter.Output => new TokenUsage(0, -1, 0, 0, 0),
        UsageCounter.CacheRead => new TokenUsage(0, 0, -1, 0, 0),
        UsageCounter.CacheWrite => new TokenUsage(0, 0, 0, -1, 0),
        UsageCounter.Reasoning => new TokenUsage(0, 0, 0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static ModelResponse ToolUseResponse(TokenUsage usage, TokenUsageFields reportedFields) => new(
        new ContentBlock[]
        {
            new ToolCallBlock(ToolCallId.New(), "fixture-provider-call", "fake.read", "{}"),
        }, StopReason.ToolUse, usage, null,
        new ProviderMetadata("scripted", "budget-test-model", null), reportedFields);

    private static ModelResponse EndTurnResponse(TokenUsage? usage = null) => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, usage ?? new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("scripted", "budget-test-model", null));

    private static TokenUsage UsageWith(UsageCounter counter, long value) => counter switch
    {
        UsageCounter.Input => new TokenUsage(value, 0, 0, 0, 0),
        UsageCounter.Output => new TokenUsage(0, value, 0, 0, 0),
        // Auxiliaries are included in their aggregate; these fixtures must reach arithmetic,
        // not fail the consistency guard before the second invocation.
        UsageCounter.CacheRead => new TokenUsage(value, 0, value, 0, 0),
        UsageCounter.CacheWrite => new TokenUsage(value, 0, 0, value, 0),
        UsageCounter.Reasoning => new TokenUsage(0, value, 0, 0, value),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static decimal ExpectedCost(TokenUsage usage) =>
        usage.Input / 1_000_000m + usage.Output / 1_000_000m;

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
