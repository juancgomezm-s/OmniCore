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

/// <summary>Offline integration fixtures for direct Run-scoped UltraCode ceilings.</summary>
public sealed class UltraCodeExecutionBudgetIntegrationTests
{
    private static readonly ExecutionFingerprint Baseline = new("fixture", "h", "t", "c", "o", "build");

    [Fact]
    public void Max_turns_accumulate_across_asks_and_sqlite_reopen_and_completed_turn_returns_its_id()
    {
        WithDirectory(root =>
        {
            var journal = Path.Combine(root, "journal.db");
            var store = new SqliteEventStore(journal);
            try
            {
                var codecs = EventCodecs.Create();
                var session = SessionId.New();
                var stream = new EventStream(store, codecs, session);
                var run = TestRun.Open(stream, session, "UltraCode max turns fixture");
                using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane)))
                    stream.Append(new RunModeAuthoritySelected(Authority(run.RunId, "UltraCode max turns fixture",
                        maxTurns: 1, maxToolCalls: 5, maxElapsedSeconds: 300, maxSpendUsd: null),
                        "fixture-authority", "RunCreated"));
                var resumedTurnId = TurnId.New();
                using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane,
                    resumedTurnId)))
                    stream.Append(new TurnStarted(resumedTurnId, run.RootLane, Baseline));

                var providerCalls = 0;
                var first = CreateTurn(root, store, codecs, new CountingExecutor(), (_, _) =>
                {
                    providerCalls++;
                    return EndTurn();
                }).Ask("first", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

                Assert.Equal(StopReason.EndTurn, first.StopReason);
                Assert.Equal(1, providerCalls);
                Assert.Equal(resumedTurnId, first.TurnId);
                Assert.Equal(first.TurnId, Assert.Single(store.ReadFrom(session, 1)
                    .Select(codecs.Decode).OfType<TurnStarted>()).TurnId);

                store.Close();
                store = new SqliteEventStore(journal);
                var second = CreateTurn(root, store, codecs, new CountingExecutor(), (_, _) =>
                {
                    providerCalls++;
                    return EndTurn();
                }).Ask("second", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

                Assert.Equal(StopReason.Cancelled, second.StopReason);
                Assert.Null(second.TurnId);
                Assert.Equal(1, providerCalls);
                Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
                Assert.Equal(first.TurnId, Assert.Single(store.ReadFrom(session, 1)
                    .Select(codecs.Decode).OfType<TurnCompleted>()).TurnId);
            }
            finally
            {
                store.Close();
                using var connection = new SqliteConnection("DataSource=" + journal);
                SqliteConnection.ClearPool(connection);
            }
        });
    }

    [Fact]
    public void Max_tool_calls_blocks_the_next_prepare_execute_boundary_in_the_same_turn()
    {
        WithDirectory(root =>
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session, "UltraCode max tools fixture");
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane)))
                stream.Append(new RunModeAuthoritySelected(Authority(run.RunId, "UltraCode max tools fixture",
                    maxTurns: 1, maxToolCalls: 1, maxElapsedSeconds: 300, maxSpendUsd: null),
                    "fixture-authority", "RunCreated"));

            var catalog = FakeCatalog.Default();
            var toolName = catalog.Definitions().First().Name;
            var executor = new CountingExecutor();
            var providerCalls = 0;
            var result = CreateTurn(root, store, codecs, executor, (request, _) =>
            {
                providerCalls++;
                Assert.Contains(request.Tools, tool => tool.Name == toolName);
                return ToolCall(toolName);
            }, catalog).Ask("read twice", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.NotNull(result.TurnId);
            Assert.Equal(2, providerCalls);
            Assert.Equal(1, executor.Calls);
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ToolCallRequested>());
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ToolCallRejected>());
        });
    }

    [Fact]
    public void Expired_authority_blocks_before_context_turn_start_or_provider_dispatch()
    {
        WithDirectory(root =>
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session, "expired UltraCode fixture");
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane)))
                stream.Append(new RunModeAuthoritySelected(Authority(run.RunId, "expired UltraCode fixture",
                    maxTurns: 2, maxToolCalls: 2, maxElapsedSeconds: 1, maxSpendUsd: null,
                    grantedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)), "fixture-authority", "RunCreated"));
            var providerCalls = 0;

            var result = CreateTurn(root, store, codecs, new CountingExecutor(), (_, _) =>
            {
                providerCalls++;
                return EndTurn();
            }).Ask("blocked", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(0, providerCalls);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>());
        });
    }

    [Fact]
    public void Max_spend_requires_a_known_quote_but_an_explicit_zero_quote_is_not_fabricated()
    {
        WithDirectory(root =>
        {
            var unknownStore = new InMemoryEventStore();
            var unknownCodecs = EventCodecs.Create();
            var unknownSession = SessionId.New();
            var unknownStream = new EventStream(unknownStore, unknownCodecs, unknownSession);
            var unknownRun = TestRun.Open(unknownStream, unknownSession, "unknown price fixture");
            using (ExecutionScope.Begin(new ExecutionScopeState(unknownRun.RunId, unknownRun.RootTask, unknownRun.RootLane)))
                unknownStream.Append(new RunModeAuthoritySelected(Authority(unknownRun.RunId, "unknown price fixture",
                    maxTurns: 2, maxToolCalls: 2, maxElapsedSeconds: 300, maxSpendUsd: 1m),
                    "fixture-authority", "RunCreated"));
            var unknownCalls = 0;
            var unknown = CreateTurn(root, unknownStore, unknownCodecs, new CountingExecutor(), (_, _) =>
            {
                unknownCalls++;
                return EndTurn();
            }, pricing: null).Ask("blocked", "system", unknownSession, unknownRun.RunId,
                unknownRun.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, unknown.StopReason);
            Assert.Equal(0, unknownCalls);
            Assert.Empty(unknownStore.ReadFrom(unknownSession, 1).Select(unknownCodecs.Decode)
                .OfType<ModelStepStarted>());

            var boundedStore = new InMemoryEventStore();
            var boundedCodecs = EventCodecs.Create();
            var boundedSession = SessionId.New();
            var boundedStream = new EventStream(boundedStore, boundedCodecs, boundedSession);
            var boundedRun = TestRun.Open(boundedStream, boundedSession, "bound price fixture");
            using (ExecutionScope.Begin(new ExecutionScopeState(boundedRun.RunId, boundedRun.RootTask, boundedRun.RootLane)))
                boundedStream.Append(new RunModeAuthoritySelected(Authority(boundedRun.RunId, "bound price fixture",
                    maxTurns: 2, maxToolCalls: 2, maxElapsedSeconds: 300, maxSpendUsd: 0.001m),
                    "fixture-authority", "RunCreated"));
            var boundedCalls = 0;
            var bounded = CreateTurn(root, boundedStore, boundedCodecs, new CountingExecutor(), (_, _) =>
            {
                boundedCalls++;
                return EndTurn();
            }, pricing: new ModelPricing(1m, 1m)).Ask("blocked by quote", "system", boundedSession,
                boundedRun.RunId, boundedRun.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, bounded.StopReason);
            Assert.Equal(0, boundedCalls);
            Assert.Empty(boundedStore.ReadFrom(boundedSession, 1).Select(boundedCodecs.Decode)
                .OfType<ModelStepStarted>());

            var zeroStore = new InMemoryEventStore();
            var zeroCodecs = EventCodecs.Create();
            var zeroSession = SessionId.New();
            var zeroStream = new EventStream(zeroStore, zeroCodecs, zeroSession);
            var zeroRun = TestRun.Open(zeroStream, zeroSession, "explicit zero pricing fixture");
            using (ExecutionScope.Begin(new ExecutionScopeState(zeroRun.RunId, zeroRun.RootTask, zeroRun.RootLane)))
                zeroStream.Append(new RunModeAuthoritySelected(Authority(zeroRun.RunId, "explicit zero pricing fixture",
                    maxTurns: 2, maxToolCalls: 2, maxElapsedSeconds: 300, maxSpendUsd: 0m),
                    "fixture-authority", "RunCreated"));
            var zeroCalls = 0;
            // Synthetic explicit zero-rate fixture only; never inferred from provider/account identity.
            var zero = CreateTurn(root, zeroStore, zeroCodecs, new CountingExecutor(), (_, _) =>
            {
                zeroCalls++;
                return EndTurn();
            }, pricing: new ModelPricing(0m, 0m)).Ask("allowed by explicit zero quote", "system", zeroSession,
                zeroRun.RunId, zeroRun.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, zero.StopReason);
            Assert.Equal(1, zeroCalls);
            Assert.NotNull(zero.TurnId);
            Assert.Single(zeroStore.ReadFrom(zeroSession, 1).Select(zeroCodecs.Decode).OfType<ModelStepCompleted>());
        });
    }

    private static RunModeAuthority Authority(RunId runId, string objective, int maxTurns, int maxToolCalls,
        int maxElapsedSeconds, decimal? maxSpendUsd, DateTimeOffset? grantedAtUtc = null) => new(runId, 1,
        RunMode.Act, ExecutionStrategy.Direct, ProductEffort.UltraCode, false, true, 1,
        RunModeAuthority.ObjectiveDigestFor(objective), 1,
        new ModeSwitchAuthorization(Guid.NewGuid(), 1, 1, RunModeAuthority.ObjectiveDigestFor(objective), 1,
            [RunMode.Act, RunMode.Plan], new ModeSwitchLimits(0, 0, maxTurns, maxToolCalls,
                maxElapsedSeconds, maxSpendUsd), grantedAtUtc ?? DateTimeOffset.UtcNow));

    private static ExplorerTurn CreateTurn(string root, IEventStore store, EventCodecs codecs,
        IToolExecutor executor, Func<ModelRequest, CancellationToken, ModelResponse> complete,
        FakeCatalog? catalog = null, ModelPricing? pricing = null) => new(complete, executor,
        catalog ?? new FakeCatalog(), new ContextMaterializer(new FakeTokenCounter(), []), Baseline,
        new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null, maxOutputTokens: 1024),
        store, codecs, new FileArtifactStore(Path.Combine(root, "cas-" + Guid.NewGuid().ToString("N"))),
        new InMemoryAuditSink(), new RedactionPolicy(), pricing: pricing,
        modelContextCapacity: 8192, maximumGenerationRequestAttempts: 1);

    private static ModelResponse EndTurn() => new([new TextBlock("fixture response")], StopReason.EndTurn,
        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("offline-fixture", "fixture-model", null));

    private static ModelResponse ToolCall(string toolName) => new(
        [new ToolCallBlock(ToolCallId.New(), "offline-call", toolName, "{}")], StopReason.ToolUse,
        new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("offline-fixture", "fixture-model", null));

    private sealed class CountingExecutor : IToolExecutor
    {
        public int Calls { get; private set; }

        public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
            CancellationToken cancellationToken, EventStream stream)
        {
            Calls++;
            return ToolOutcome.Failed("offline scripted rejection", null, ToolCallState.Rejected,
                [new ToolCallRequested(validated.ToolCallId, validated.ProviderCallId,
                    validated.ToolId.ToString(), validated.ArgumentsJson),
                 new ToolCallRejected(validated.ToolCallId, "offline scripted rejection", ToolErrorCode.UnknownTool)]);
        }
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-ultracode-execution-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
