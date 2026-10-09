namespace OmniCore.Tests;

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
using Xunit;

public sealed class M5RuntimeInconsistentUsageTests
{
    public enum Counter { CacheRead, CacheWrite, Reasoning }
    public enum BudgetMode { RunCap, DefaultCaps, Uncapped }

    [Theory]
    [InlineData(Counter.CacheRead, BudgetMode.RunCap)]
    [InlineData(Counter.CacheRead, BudgetMode.DefaultCaps)]
    [InlineData(Counter.CacheRead, BudgetMode.Uncapped)]
    [InlineData(Counter.CacheWrite, BudgetMode.RunCap)]
    [InlineData(Counter.CacheWrite, BudgetMode.DefaultCaps)]
    [InlineData(Counter.CacheWrite, BudgetMode.Uncapped)]
    [InlineData(Counter.Reasoning, BudgetMode.RunCap)]
    [InlineData(Counter.Reasoning, BudgetMode.DefaultCaps)]
    [InlineData(Counter.Reasoning, BudgetMode.Uncapped)]
    public void Reported_subcounter_above_its_reported_aggregate_is_durable_but_never_authorizes_tools(
        Counter counter, BudgetMode budgetMode)
    {
        var fixture = OpenFixture(budgetMode == BudgetMode.RunCap ? 1m : null, durable: true);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var usage = ContradictoryUsage(counter);
            var calls = 0;
            var turn = MakeTurn(fixture, countedTools,
                enforceDefaultCaps: budgetMode == BudgetMode.DefaultCaps,
                (_, _) =>
                {
                    calls++;
                    return calls == 1 ? ToolUseResponse(usage, TokenUsageFields.All) : EndTurnResponse();
                });

            var expectedStop = budgetMode == BudgetMode.Uncapped ? StopReason.Error : StopReason.Cancelled;
            Assert.Equal(expectedStop, turn.Ask("question", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None).StopReason);
            Assert.Equal(1, calls);
            Assert.Equal(0, countedTools.ExecuteCount);

            var previousSequence = fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence;
            ((SqliteEventStore)fixture.Store).Close();
            fixture = fixture with { Store = new SqliteEventStore(Path.Combine(fixture.TempRoot, "journal.db")) };
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            Assert.Equal(previousSequence, fixture.Store.ReadFrom(fixture.Session, 1).Last().Sequence);
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(0, completed.StepIndex);
            Assert.Equal(usage, completed.Usage);
            Assert.Equal(TokenUsageFields.All, completed.ReportedUsageFields);
            Assert.Null(completed.CostUsd);
            Assert.NotNull(completed.ResponseArtifact);
            var artifacts = new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts"));
            Assert.True(artifacts.Verify(completed.ResponseArtifact.Hash, completed.ResponseArtifact.Size));
            using var raw = JsonDocument.Parse(artifacts.GetText(completed.ResponseArtifact.Hash)!);
            Assert.Equal(usage.Input, raw.RootElement.GetProperty("input").GetInt64());
            Assert.Equal(usage.Output, raw.RootElement.GetProperty("output").GetInt64());
            Assert.Equal(usage.CacheRead, raw.RootElement.GetProperty("cacheRead").GetInt64());
            Assert.Equal(usage.CacheWrite, raw.RootElement.GetProperty("cacheWrite").GetInt64());
            Assert.Equal(usage.Reasoning, raw.RootElement.GetProperty("reasoning").GetInt64());
            Assert.Equal(JsonValueKind.Null, raw.RootElement.GetProperty("costUsd").ValueKind);
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);

            if (budgetMode == BudgetMode.Uncapped)
            {
                Assert.Single(events.OfType<TurnAbandoned>());
                Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                    && request.Kind == InteractionKind.BudgetExceeded);
            }
            else
            {
                Assert.DoesNotContain(events, payload => payload is ModelCompleted);
                Assert.Single(events.OfType<TurnCompleted>());
                var request = Assert.Single(events.OfType<InteractionRequested>(), item =>
                    item.Kind == InteractionKind.BudgetExceeded);
                Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
            }
        }
        finally { fixture.DeleteArtifacts(); }
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public void Reported_subcounter_equal_to_its_reported_aggregate_is_not_a_contradiction(Counter counter)
    {
        var fixture = OpenFixture(null);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var usage = BoundaryUsage(counter);
            var calls = 0;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: true,
                (_, _) => ++calls == 1
                    ? ToolUseResponse(usage, TokenUsageFields.All)
                    : EndTurnResponse());

            Assert.Equal(StopReason.EndTurn, turn.Ask("question", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None).StopReason);
            Assert.Equal(2, calls);
            Assert.Equal(1, countedTools.ExecuteCount);
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var first = Assert.Single(events.OfType<ModelStepCompleted>(), step => step.StepIndex == 0);
            Assert.Equal(usage, first.Usage);
            Assert.Equal(TokenUsageFields.All, first.ReportedUsageFields);
            Assert.Equal(0.000015m, first.CostUsd);
            Assert.Single(events.OfType<ModelCompleted>());
            Assert.DoesNotContain(events, payload => payload is InteractionRequested request
                && request.Kind == InteractionKind.BudgetExceeded);
        }
        finally { fixture.DeleteArtifacts(); }
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public void Subcounter_is_not_compared_to_placeholder_when_aggregate_bit_is_absent_and_cap_fails_closed(
        Counter counter)
    {
        var fixture = OpenFixture(1m, durable: true);
        try
        {
            var countedTools = new CountingExecutor(fixture.Executor);
            var (usage, fields, absentAggregate) = UnreportedAggregateUsage(counter);
            var calls = 0;
            var turn = MakeTurn(fixture, countedTools, enforceDefaultCaps: false,
                (_, _) =>
                {
                    calls++;
                    return ToolUseResponse(usage, fields);
                });

            Assert.Equal(StopReason.Cancelled, turn.Ask("question", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None).StopReason);
            Assert.Equal(1, calls);
            Assert.Equal(0, countedTools.ExecuteCount);
            ((SqliteEventStore)fixture.Store).Close();
            fixture = fixture with { Store = new SqliteEventStore(Path.Combine(fixture.TempRoot, "journal.db")) };
            var events = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).ToArray();
            var completed = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(usage, completed.Usage); // preserve the provider's raw placeholder and detail
            Assert.Equal(fields, completed.ReportedUsageFields);
            Assert.Null(completed.CostUsd); // missing aggregate evidence is unknown, not zero
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            Assert.Single(events.OfType<TurnCompleted>());
            var summary = Assert.Single(events.OfType<ModelCompleted>());
            using var summaryJson = JsonDocument.Parse(new FileArtifactStore(Path.Combine(fixture.TempRoot, "artifacts"))
                .GetText(summary.ResponseArtifact!.Hash)!);
            Assert.Equal(JsonValueKind.Null, summaryJson.RootElement.GetProperty("costUsd").ValueKind);
            var request = Assert.Single(events.OfType<InteractionRequested>(), item =>
                item.Kind == InteractionKind.BudgetExceeded);
            using var context = JsonDocument.Parse(request.SubjectJson);
            Assert.Contains("incompleto", context.RootElement.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain("inválido", context.RootElement.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain("allow_plus", request.OptionsJson, StringComparison.Ordinal);
            Assert.False(fields.HasFlag(absentAggregate));
        }
        finally { fixture.DeleteArtifacts(); }
    }

    private static Fixture OpenFixture(decimal? runCostUsd, bool durable = false)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "omnicore-m5-usage-consistency-" + Guid.NewGuid().ToString("N"));
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
            new RunCreated(run, session, "M5 reported usage consistency", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run),
            new TaskCreated(task, run, "M5 reported usage consistency", Array.Empty<TaskDependency>(), budget),
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
        maximumGenerationRequestAttempts: 1); // one scripted response per model request

    private static ModelResponse ToolUseResponse(TokenUsage usage, TokenUsageFields fields) => new(
        new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "fixture-provider-call", "fake.read", "{}") },
        StopReason.ToolUse, usage, null, new ProviderMetadata("scripted", "budget-test-model", null), fields);

    private static ModelResponse EndTurnResponse() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("scripted", "budget-test-model", null), TokenUsageFields.Input | TokenUsageFields.Output);

    private static TokenUsage ContradictoryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new TokenUsage(10, 5, 11, 0, 0),
        Counter.CacheWrite => new TokenUsage(10, 5, 0, 11, 0),
        Counter.Reasoning => new TokenUsage(10, 5, 0, 0, 6),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static TokenUsage BoundaryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new TokenUsage(10, 5, 10, 0, 0),
        Counter.CacheWrite => new TokenUsage(10, 5, 0, 10, 0),
        Counter.Reasoning => new TokenUsage(10, 5, 0, 0, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static (TokenUsage Usage, TokenUsageFields Fields, TokenUsageFields MissingAggregate) UnreportedAggregateUsage(
        Counter counter) => counter switch
    {
        Counter.CacheRead => (new TokenUsage(0, 5, 11, 0, 0),
            TokenUsageFields.Output | TokenUsageFields.CacheRead, TokenUsageFields.Input),
        Counter.CacheWrite => (new TokenUsage(0, 5, 0, 11, 0),
            TokenUsageFields.Output | TokenUsageFields.CacheWrite, TokenUsageFields.Input),
        Counter.Reasoning => (new TokenUsage(10, 0, 0, 0, 6),
            TokenUsageFields.Input | TokenUsageFields.Reasoning, TokenUsageFields.Output),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private sealed record Fixture(IEventStore Store, IEventCodecRegistry Codecs, SessionId Session,
        RunId Run, LaneId Lane, string TempRoot, IToolExecutor Executor, InMemoryAuditSink Audit)
    {
        public void DeleteArtifacts()
        {
            if (Store is SqliteEventStore sqlite)
            {
                var connection = (SqliteConnection)sqlite.Connection;
                sqlite.Close();
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(TempRoot, recursive: true);
        }
    }

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
