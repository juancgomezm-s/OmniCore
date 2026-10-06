using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Qualification;
using OmniCore.Host;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Regression tests for qualification cost evidence. The provider is an in-memory fixture; no
/// billing mode, price, or real provider call is implied by these cases.
/// </summary>
public sealed class M5QualificationUsageEvidenceTests
{
    private static ProbeRequest Request() => new(
        new Probe(ProbeId.WellKnown("usage-evidence"), ProbeKind.Reading,
            "Reply with only the word: measured", "measured", 0m),
        new ModelSelection(new ModelIdValue("usage-evidence-model"), 8192, ToolMode.Direct, null));

    private static ModelResponse CompleteResponse(string text, TokenUsage usage,
        TokenUsageFields reportedUsageFields) => new(
        [new TextBlock(text)], StopReason.EndTurn, usage, null,
        new ProviderMetadata("fixture-request", "usage-evidence-model", null), reportedUsageFields);

    private static ProbeRunner Runner(params ModelStreamEvent[] events) =>
        new(new FixtureProvider(events));

    [Fact]
    public async Task Runner_rejects_unrepresentable_declared_total_before_streaming()
    {
        var provider = new FixtureProvider([]);
        var selection = Request().Selection;
        var requests = new[]
        {
            new ProbeRequest(new Probe(ProbeId.WellKnown("sum-first"), ProbeKind.Reading,
                "prompt", "expected", decimal.MaxValue), selection),
            new ProbeRequest(new Probe(ProbeId.WellKnown("sum-second"), ProbeKind.Reading,
                "prompt", "expected", decimal.MaxValue), selection),
        };
        await Assert.ThrowsAsync<QualificationCostEstimateUnavailableException>(() =>
            new ProbeRunner(provider).RunSuiteAsync(requests, new QualificationConsent(true, decimal.MaxValue),
                CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    private static void AssertScoredResponse(ProbeResult result)
    {
        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Equal(1.0, result.Score);
        Assert.Equal("measured", result.Output);
        Assert.Null(result.Error);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(TokenUsageFields.None)]
    [InlineData(TokenUsageFields.Input)]
    [InlineData(TokenUsageFields.Output)]
    public async Task Partial_usage_is_preserved_but_never_quoted(TokenUsageFields fields)
    {
        var usage = new TokenUsage(17, 4, 3, 2, 1);
        var calls = 0;
        var runner = new ProbeRunner(new FixtureProvider([new ResponseCompleted(
            CompleteResponse("measured", usage, fields))]), TimeSpan.FromSeconds(10),
            _ => { calls++; return 0m; });
        var result = await runner.RunProbeAsync(Request(), CancellationToken.None);
        AssertScoredResponse(result);
        Assert.Null(result.CostUsd);
        Assert.Equal(usage, result.Usage);
        Assert.Equal(fields, result.ReportedUsageFields);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_usage_has_explicit_quote_including_known_zero(bool zeroPrices)
    {
        var usage = new TokenUsage(17, 4, 3, 2, 1);
        var pricing = new ModelPricing(zeroPrices ? 0m : 2m, zeroPrices ? 0m : 8m);
        var runner = new ProbeRunner(new FixtureProvider([new ResponseCompleted(
            CompleteResponse("measured", usage, TokenUsageFields.All))]), TimeSpan.FromSeconds(10), pricing.CostUsd);
        var result = await runner.RunProbeAsync(Request(), CancellationToken.None);
        AssertScoredResponse(result);
        // Cache is part of input; reasoning part of output. Do not count either twice.
        Assert.Equal(zeroPrices ? 0m : 0.000066m, result.CostUsd);
        Assert.Equal(usage, result.Usage);
        Assert.Equal(TokenUsageFields.All, result.ReportedUsageFields);
    }

    [Fact]
    public async Task Partial_price_and_overflow_remain_unknown()
    {
        var response = CompleteResponse("measured", new TokenUsage(17, 4, 0, 0, 0),
            TokenUsageFields.Input | TokenUsageFields.Output);
        foreach (var quote in new Func<TokenUsage, decimal?>[]
        {
            new ModelPricing(2m, null).CostUsd,
            _ => throw new OverflowException(),
            _ => -1m,
        })
        {
            var result = await new ProbeRunner(new FixtureProvider([new ResponseCompleted(response)]),
                TimeSpan.FromSeconds(10), quote).RunProbeAsync(Request(), CancellationToken.None);
            AssertScoredResponse(result);
            Assert.Null(result.CostUsd);
        }
    }

    [Theory]
    [InlineData(-1L, 4L, 0L, 0L, 0L)]
    [InlineData(17L, -1L, 0L, 0L, 0L)]
    [InlineData(17L, 4L, -1L, 0L, 0L)]
    [InlineData(17L, 4L, 0L, -1L, 0L)]
    [InlineData(17L, 4L, 0L, 0L, -1L)]
    public async Task Invalid_usage_is_not_quoted_even_with_zero_prices(
        long input, long output, long cacheRead, long cacheWrite, long reasoning)
    {
        var calls = 0;
        var usage = new TokenUsage(input, output, cacheRead, cacheWrite, reasoning);
        var result = await new ProbeRunner(new FixtureProvider([new ResponseCompleted(
            CompleteResponse("measured", usage, TokenUsageFields.All))]), TimeSpan.FromSeconds(10),
            _ => { calls++; return 0m; }).RunProbeAsync(Request(), CancellationToken.None);
        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(0d, result.Score);
        Assert.Null(result.Output);
        Assert.Equal("provider reported inconsistent token usage", result.Error);
        Assert.Equal(TokenUsageFields.All, result.ReportedUsageFields);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Equal(usage, result.Usage);
        Assert.Null(result.CostUsd);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Scoring_failure_preserves_usage_and_quote()
    {
        var usage = new TokenUsage(17, 4, 0, 0, 0);
        var result = await new ProbeRunner(new FixtureProvider([new ResponseCompleted(
            CompleteResponse("wrong", usage, TokenUsageFields.Input | TokenUsageFields.Output))]),
            TimeSpan.FromSeconds(10), new ModelPricing(2m, 8m).CostUsd)
            .RunProbeAsync(Request(), CancellationToken.None);
        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Equal(0d, result.Score);
        Assert.Equal("wrong", result.Output);
        Assert.Equal(usage, result.Usage);
        Assert.Equal(0.000066m, result.CostUsd);
    }

    [Fact]
    public async Task Correct_answer_with_usage_not_reported_does_not_claim_zero_cost()
    {
        var response = CompleteResponse("measured", new TokenUsage(17, 4, 0, 0, 0),
            TokenUsageFields.None);

        var result = await Runner(new ResponseCompleted(response))
            .RunProbeAsync(Request(), CancellationToken.None);

        AssertScoredResponse(result);
        decimal? cost = result.CostUsd;
        Assert.False(cost.HasValue);
    }

    [Fact]
    public async Task Correct_answer_with_reported_usage_but_no_pricing_does_not_claim_zero_cost()
    {
        var response = CompleteResponse("measured", new TokenUsage(17, 4, 0, 0, 0),
            TokenUsageFields.Input | TokenUsageFields.Output);

        // The legacy constructor supplies no price quote: reported token counts alone cannot
        // establish a monetary cost.
        var result = await Runner(new ResponseCompleted(response))
            .RunProbeAsync(Request(), CancellationToken.None);

        AssertScoredResponse(result);
        decimal? cost = result.CostUsd;
        Assert.False(cost.HasValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_completed_response_does_not_claim_zero_cost(bool emitFailure)
    {
        var events = emitFailure
            ? new ModelStreamEvent[] { new ResponseFailed("fixture-error", "no completed response") }
            : Array.Empty<ModelStreamEvent>();

        var result = await Runner(events).RunProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(0.0, result.Score);
        Assert.Null(result.Output);
        Assert.Equal(emitFailure ? "fixture-error: no completed response"
            : "el provider no devolvió una respuesta completa", result.Error);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        decimal? cost = result.CostUsd;
        Assert.False(cost.HasValue);
    }

    // Scripted event fixture has one generation response per StreamAsync invocation; not a billing guarantee.
    private sealed class FixtureProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly IReadOnlyList<ModelStreamEvent> _events;
        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;

        public FixtureProvider(IReadOnlyList<ModelStreamEvent> events) => _events = events;

        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            foreach (var item in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
