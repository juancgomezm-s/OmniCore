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

public sealed class SpendPricingTests
{
    [Theory]
    [InlineData(-1L, 0L, 0L, 0L, 0L)]
    [InlineData(0L, -1L, 0L, 0L, 0L)]
    [InlineData(0L, 0L, -1L, 0L, 0L)]
    [InlineData(0L, 0L, 0L, -1L, 0L)]
    [InlineData(0L, 0L, 0L, 0L, -1L)]
    [InlineData(long.MinValue, 0L, 0L, 0L, 0L)]
    public void Invalid_signed_counters_are_unavailable_not_zero_or_negative_cost(
        long input, long output, long cacheRead, long cacheWrite, long reasoning)
    {
        var usage = new TokenUsage(input, output, cacheRead, cacheWrite, reasoning);
        Assert.Null(new ModelPricing(1m, 1m).CostUsd(usage));
        Assert.Null(new ModelPricing(0m, 0m).CostUsd(usage));
    }

    [Fact]
    public void Unrepresentable_cost_estimate_is_unavailable_and_zero_measured_cost_remains_zero()
    {
        var usage = new TokenUsage(long.MaxValue, long.MaxValue, 0, 0, 0);
        Assert.Null(new ModelPricing(decimal.MaxValue, decimal.MaxValue).CostUsd(usage));
        Assert.Equal(0m, new ModelPricing(0m, 0m).CostUsd(usage));
        Assert.Equal(0m, new ModelPricing(1m, 1m).CostUsd(new TokenUsage(0, 0, 0, 0, 0)));
        Assert.Null(new ModelPricing(null, 1m).CostUsd(new TokenUsage(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Typed_model_prices_change_calculated_cost_and_invalid_negative_rates_are_rejected()
    {
        var yaml = "models:\n  worker:\n    provider: local\n    inputPricePerMillionUsd: 2\n    outputPricePerMillionUsd: 8\n";
        var loaded = new ConfigLoader().Load(null, yaml);
        var pricing = loaded.Pricing("worker");
        Assert.NotNull(pricing);
        Assert.Equal(0.01m, pricing!.CostUsd(new TokenUsage(1000, 1000, 0, 0, 0)));

        var changed = new ConfigLoader().Load(null,
            "models:\n  worker:\n    provider: local\n    inputPricePerMillionUsd: 20\n    outputPricePerMillionUsd: 80\n").Pricing("worker");
        Assert.Equal(0.1m, changed!.CostUsd(new TokenUsage(1000, 1000, 0, 0, 0)));
        var providerPricing = new ConfigLoader().Load(
            "providers:\n  paid: { baseUrl: https://api.example.test, inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 4 }\n",
            "models:\n  worker: { provider: paid }\n").Pricing("worker");
        Assert.Equal(0.007m, providerPricing!.CostUsd(new TokenUsage(1000, 1000, 0, 0, 0)));
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null,
            "models:\n  worker:\n    provider: local\n    inputPricePerMillionUsd: -1\n    outputPricePerMillionUsd: 8\n"));
    }

    [Fact]
    public void Unknown_price_fails_closed_before_call_when_cost_cap_is_configured()
    {
        var setup = CreateTurn(new TaskBudget(1m, null, null, null), pricing: null,
            enforceCaps: false, response: null);
        var calls = 0;
        var turn = CreateTurn(setup.Store, setup.Artifacts, setup.Session, setup.Run,
            new TaskBudget(1m, null, null, null), null, false, (_, _) =>
            {
                calls++;
                return Response(new TokenUsage(100, 100, 0, 0, 0));
            });
        var result = turn.Ask("hola", "sys", setup.Session, setup.Run, "", CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Contains(setup.Store.ReadFrom(setup.Session, 1).Where(e => e.Type.ToString() == "interaction.requested")
            .Select(e => (InteractionRequested)EventCodecs.Create().Decode(e)),
            interaction => interaction.Kind == InteractionKind.BudgetExceeded);
    }

    [Fact]
    public void Configured_session_and_daily_caps_change_refusal_behavior()
    {
        var sessionSetup = CreateTurn(new TaskBudget(null, null, null, null), null, false, null);
        var sessionCap = CreateTurn(sessionSetup.Store, sessionSetup.Artifacts, sessionSetup.Session, sessionSetup.Run,
            new TaskBudget(null, null, null, null), new ModelPricing(10_000m, 10_000m), true,
            (_, _) => Response(new TokenUsage(1_000, 1_000, 0, 0, 0)), sessionCapUsd: 0.001m, dailyCapUsd: 10m);
        Assert.Equal(StopReason.Cancelled, sessionCap.Ask("x", "sys", sessionSetup.Session, sessionSetup.Run,
            "", CancellationToken.None).StopReason);

        var raisedSetup = CreateTurn(new TaskBudget(null, null, null, null), null, false, null);
        var raisedCaps = CreateTurn(raisedSetup.Store, raisedSetup.Artifacts, raisedSetup.Session, raisedSetup.Run,
            new TaskBudget(null, null, null, null), new ModelPricing(10_000m, 10_000m), true,
            (_, _) => Response(new TokenUsage(1_000, 1_000, 0, 0, 0)), sessionCapUsd: 30m, dailyCapUsd: 30m);
        Assert.Equal(StopReason.EndTurn, raisedCaps.Ask("x", "sys", raisedSetup.Session, raisedSetup.Run,
            "", CancellationToken.None).StopReason);
        // The daily cap is independently restrictive even when the session cap is raised.
        var dailySetup = CreateTurn(new TaskBudget(null, null, null, null), null, false, null);
        var dailyCap = CreateTurn(dailySetup.Store, dailySetup.Artifacts, dailySetup.Session, dailySetup.Run,
            new TaskBudget(null, null, null, null), new ModelPricing(10_000m, 10_000m), true,
            (_, _) => Response(new TokenUsage(1_000, 1_000, 0, 0, 0)), sessionCapUsd: 1m, dailyCapUsd: 0.001m);
        Assert.Equal(StopReason.Cancelled, dailyCap.Ask("x", "sys", dailySetup.Session, dailySetup.Run,
            "", CancellationToken.None).StopReason);
    }

    [Fact]
    public void Spend_is_replayed_from_journal_and_refuses_the_next_model_call_after_restart()
    {
        var setup = CreateTurn(new TaskBudget(0.001m, null, null, null),
            new ModelPricing(10_000m, 10_000m), false, null);
        var initialCalls = 0;
        var first = CreateTurn(setup.Store, setup.Artifacts, setup.Session, setup.Run,
            new TaskBudget(0.001m, null, null, null), new ModelPricing(10_000m, 10_000m), false,
            (_, _) => { initialCalls++; return Response(new TokenUsage(1_000, 1_000, 0, 0, 0)); });
        var firstResult = first.Ask("primero", "sys", setup.Session, setup.Run, "", CancellationToken.None);
        Assert.Equal(1, initialCalls);
        Assert.Equal(StopReason.Cancelled, firstResult.StopReason);

        var restartedCalls = 0;
        var restarted = CreateTurn(setup.Store, setup.Artifacts, setup.Session, setup.Run,
            new TaskBudget(0.001m, null, null, null), new ModelPricing(10_000m, 10_000m), false,
            (_, _) => { restartedCalls++; return Response(new TokenUsage(1, 1, 0, 0, 0)); });
        var secondResult = restarted.Ask("después del reinicio", "sys", setup.Session, setup.Run, "",
            CancellationToken.None);
        Assert.Equal(0, restartedCalls);
        Assert.Equal(StopReason.Cancelled, secondResult.StopReason);
    }

    private sealed record Setup(IEventStore Store, IArtifactStore Artifacts, SessionId Session, RunId Run);

    private static Setup CreateTurn(TaskBudget budget, ModelPricing? pricing, bool enforceCaps,
        Func<ModelRequest, CancellationToken, ModelResponse>? response)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var stream = new EventStream(store, codecs, session);
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new RunCreated(run, session, "budget test", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run), new TaskCreated(task, run, "budget test", Array.Empty<TaskDependency>(), budget),
            new TaskReady(task), new LaneCreated(lane, task, ProfileId.New()), new LaneStarted(lane),
            new TaskStarted(task, lane),
        }, DurabilityClass.Standard);
        var artifacts = new FileArtifactStore(Path.Combine(Path.GetTempPath(), "omnicore-spend-" + Guid.NewGuid().ToString("N")));
        return new Setup(store, artifacts, session, run);
    }

    private static ExplorerTurn CreateTurn(IEventStore store, IArtifactStore artifacts, SessionId session,
        RunId run, TaskBudget budget, ModelPricing? pricing, bool enforceCaps,
        Func<ModelRequest, CancellationToken, ModelResponse> complete,
        decimal sessionCapUsd = 5m, decimal dailyCapUsd = 20m)
    {
        _ = budget; // RunCreated is the canonical source read by ExplorerTurn.
        var catalog = OmniHost.CreateExplorerTools().Catalog();
        var executor = OmniHost.CreateExplorerExecutor(catalog, Path.GetTempPath());
        var codecs = EventCodecs.Create();
        return new ExplorerTurn(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("test-model", "h", "tools", "tokens", "opaque", "M2"),
            new ModelSelection(new ModelIdValue("test-model"), 4096, ToolMode.Direct, null),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), null, null,
            pricing, enforceCaps, sessionCapUsd, dailyCapUsd);
    }

    private static ModelResponse Response(TokenUsage usage) => new(new ContentBlock[] { new TextBlock("ok") },
        StopReason.EndTurn, usage, null, new ProviderMetadata("", "test-model", null));
}
