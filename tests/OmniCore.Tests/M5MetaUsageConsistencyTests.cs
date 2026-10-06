namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

public sealed class M5MetaUsageConsistencyTests
{
    public enum Counter { CacheRead, CacheWrite, Reasoning }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public async Task Reported_positive_contradiction_never_returns_provider_summary_or_quotes_cost(Counter counter)
    {
        using var fixture = new Fixture(ContradictoryUsage(counter), TokenUsageFields.All);
        var quoteCalls = 0;
        string? returnedSummary = null;
        var service = fixture.Service(_ =>
        {
            quoteCalls++;
            return 0.000014m;
        });

        // MetaModelService exposes no typed invalid-usage exception today. Assert the durable
        // failed outcome and absent provider summary instead of inventing a new exception contract.
        var exception = await Record.ExceptionAsync(async () =>
        {
            returnedSummary = await service.SummarizeAsync(fixture.Run, "CompressContext",
                "fixture older conversation", 500, CancellationToken.None);
        });

        Assert.NotNull(exception);
        Assert.Null(returnedSummary);
        Assert.Equal(0, quoteCalls);
        var payloads = fixture.Payloads();
        var started = Assert.Single(payloads.OfType<MetaModelInvocationStarted>());
        Assert.True(fixture.Artifacts.Verify(started.InputArtifact.Hash, started.InputArtifact.Size));
        var failed = Assert.Single(payloads.OfType<MetaModelInvocationFailed>());
        Assert.Equal(started.InvocationId, failed.InvocationId);
        Assert.Equal(ContradictoryUsage(counter), failed.Usage);
        Assert.Equal(TokenUsageFields.All, failed.ReportedUsageFields);
        Assert.Null(failed.CostUsd);
        Assert.DoesNotContain(payloads, payload => payload is MetaModelInvocationCompleted);
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public async Task Equality_with_reported_aggregate_keeps_summary_and_quotes_only_aggregate_usage(Counter counter)
    {
        using var fixture = new Fixture(BoundaryUsage(counter), TokenUsageFields.All);
        var quoteCalls = 0;
        TokenUsage? quoted = null;
        var service = fixture.Service(usage =>
        {
            quoteCalls++;
            quoted = usage;
            return AggregateQuote(usage);
        });

        var summary = await service.SummarizeAsync(fixture.Run, "CompressContext",
            "fixture older conversation", 500, CancellationToken.None);

        Assert.Equal("fixture summary", summary);
        Assert.Equal(1, quoteCalls);
        Assert.Equal(BoundaryUsage(counter), quoted);
        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(BoundaryUsage(counter), completed.Usage);
        Assert.Equal(TokenUsageFields.All, completed.ReportedUsageFields);
        Assert.Equal(0.000014m, completed.CostUsd);
        Assert.True(fixture.Artifacts.Verify(completed.OutputArtifact.Hash, completed.OutputArtifact.Size));
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationFailed>());
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public async Task Unreported_raw_detail_does_not_invalidate_reported_aggregates_or_cost_quote(Counter counter)
    {
        using var fixture = new Fixture(ContradictoryUsage(counter),
            TokenUsageFields.Input | TokenUsageFields.Output);
        var quoteCalls = 0;
        TokenUsage? quoted = null;
        var service = fixture.Service(usage =>
        {
            quoteCalls++;
            quoted = usage;
            return AggregateQuote(usage);
        });

        var summary = await service.SummarizeAsync(fixture.Run, "CompressContext",
            "fixture older conversation", 500, CancellationToken.None);

        Assert.Equal("fixture summary", summary);
        Assert.Equal(1, quoteCalls);
        Assert.Equal(ContradictoryUsage(counter), quoted); // raw auxiliary placeholder is preserved
        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(ContradictoryUsage(counter), completed.Usage);
        Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, completed.ReportedUsageFields);
        Assert.Equal(0.000014m, completed.CostUsd); // cost is derived from Input + Output only
        Assert.True(fixture.Artifacts.Verify(completed.OutputArtifact.Hash, completed.OutputArtifact.Size));
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public async Task Missing_aggregate_keeps_auxiliary_summary_but_cost_is_unknown_without_quote(
        Counter counter)
    {
        var (usage, fields) = MissingAggregateUsage(counter);
        using var fixture = new Fixture(usage, fields);
        var quoteCalls = 0;
        var service = fixture.Service(_ =>
        {
            quoteCalls++;
            return 0m;
        });

        var summary = await service.SummarizeAsync(fixture.Run, "CompressContext",
            "fixture older conversation", 500, CancellationToken.None);

        Assert.Equal("fixture summary", summary);
        Assert.Equal(0, quoteCalls);
        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(usage, completed.Usage);
        Assert.Equal(fields, completed.ReportedUsageFields);
        Assert.Null(completed.CostUsd);
        Assert.True(fixture.Artifacts.Verify(completed.OutputArtifact.Hash, completed.OutputArtifact.Size));
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationFailed>());
    }

    private static TokenUsage ContradictoryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new(10, 4, 11, 0, 0),
        Counter.CacheWrite => new(10, 4, 0, 11, 0),
        Counter.Reasoning => new(10, 4, 0, 0, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static TokenUsage BoundaryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new(10, 4, 10, 0, 0),
        Counter.CacheWrite => new(10, 4, 0, 10, 0),
        Counter.Reasoning => new(10, 4, 0, 0, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static (TokenUsage Usage, TokenUsageFields Fields) MissingAggregateUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => (new(0, 4, 11, 0, 0), TokenUsageFields.Output | TokenUsageFields.CacheRead),
        Counter.CacheWrite => (new(0, 4, 0, 11, 0), TokenUsageFields.Output | TokenUsageFields.CacheWrite),
        Counter.Reasoning => (new(10, 0, 0, 0, 5), TokenUsageFields.Input | TokenUsageFields.Reasoning),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static decimal AggregateQuote(TokenUsage usage) =>
        usage.Input / 1_000_000m + usage.Output / 1_000_000m;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omnicore-m5-meta-usage-consistency-" + Guid.NewGuid().ToString("N"));
        private readonly InMemoryEventStore _store = new();
        private readonly IEventCodecRegistry _codecs = EventCodecs.Create();
        private readonly EventStream _stream;
        private readonly JournalSink _sink;
        private readonly ScriptedProvider _provider;
        public RunId Run { get; } = RunId.New();
        public SessionId Session { get; } = SessionId.New();
        public FileArtifactStore Artifacts { get; }

        public Fixture(TokenUsage usage, TokenUsageFields fields)
        {
            Directory.CreateDirectory(_root);
            Artifacts = new FileArtifactStore(_root);
            Run = TestRun.Open(_store, Session).RunId;
            _stream = new EventStream(_store, _codecs, Session);
            _sink = new JournalSink(_stream);
            _provider = new ScriptedProvider(usage, fields);
        }

        public MetaModelService Service(Func<TokenUsage, decimal?> quoteCost) => new(_provider, Artifacts,
            _sink, new ModelSelection(new ModelIdValue("fixture-meta"), 512, ToolMode.Direct, null),
            costEstimator: quoteCost);

        public DomainEventPayload[] Payloads() => _store.ReadFrom(Session, 1)
            .Select(evt => _codecs.Decode(evt)).ToArray();

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        private sealed class JournalSink(EventStream stream) : IContextEventSink
        {
            public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Append(payload, DurabilityClass.Barrier);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class ScriptedProvider(TokenUsage usage, TokenUsageFields fields) : IModelProvider
        {
            public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();

            public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await System.Threading.Tasks.Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ResponseCompleted(new ModelResponse(
                    new ContentBlock[] { new TextBlock("fixture summary") }, StopReason.EndTurn,
                    usage, null, new ProviderMetadata("scripted-meta", "fixture-meta", null), fields));
            }
        }
    }
}
