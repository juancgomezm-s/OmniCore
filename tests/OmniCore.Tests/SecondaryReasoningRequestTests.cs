using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Offline proposals for secondary model invocation boundaries. A mismatch with an explicit route
/// declaration must fail before reservation, observer admission/receipt, or provider entry. Matching
/// and Unknown legacy declarations preserve the selected neutral request without inferring support
/// from a model name. The scripted provider is in-memory; this does not assert wire dialect or billing.
/// </summary>
public sealed class SecondaryReasoningRequestTests
{
    private static readonly ReasoningRequest High = new("high", null);

    [Theory]
    [InlineData("unsupported")]
    [InlineData("unlisted")]
    public async Task Meta_invocation_rejects_contradictory_selection_before_reserve_dispatch_or_receipt(
        string declaration)
    {
        using var fx = new Fixture(Capability(declaration));
        var provider = new ProviderSpy();
        var sink = new ContextEventSinkSpy();
        var reserveCalls = 0;
        var dispatchCalls = 0;
        var receiptCalls = 0;
        var releaseCalls = 0;
        var service = new MetaModelService(provider, fx.Artifacts, sink, fx.Selection,
            redact: static text => text,
            reserve: _ => { reserveCalls++; return true; },
            dispatch: _ => dispatchCalls++,
            afterReceipt: (_, _, _) => receiptCalls++,
            releaseBeforeDispatch: _ => releaseCalls++);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SummarizeAsync(
            RunId.New(), "fixture-summary", "private fixture content", 100, CancellationToken.None));

        Assert.Equal(0, provider.Calls);
        Assert.Null(provider.LastRequest);
        Assert.Equal(0, reserveCalls);
        Assert.Equal(0, dispatchCalls);
        Assert.Equal(0, receiptCalls);
        Assert.Equal(0, releaseCalls);
        Assert.Empty(sink.Payloads);
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("unknown-suggestive-name")]
    public async Task Meta_invocation_preserves_matching_or_unknown_selection_on_request(string scenario)
    {
        var capability = scenario == "supported"
            ? new ReasoningCapability(true, ["high"])
            : ReasoningCapability.Unknown;
        var modelId = scenario == "unknown-suggestive-name" ? "reasoning-ultracode-high" : "fixture-model";
        using var fx = new Fixture(capability, modelId);
        var provider = new ProviderSpy();
        var sink = new ContextEventSinkSpy();
        var reserveCalls = 0;
        var dispatchCalls = 0;
        var receiptCalls = 0;
        var receiptSendCounts = new List<long>();
        var service = new MetaModelService(provider, fx.Artifacts, sink, fx.Selection,
            redact: static text => text,
            reserve: _ => { reserveCalls++; return true; },
            dispatch: _ => dispatchCalls++,
            afterReceipt: (_, _, sends) => { receiptCalls++; receiptSendCounts.Add(sends); });

        var summary = await service.SummarizeAsync(RunId.New(), "fixture-summary", "fixture content",
            100, CancellationToken.None);

        Assert.Equal("summary", summary);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(High, Assert.IsType<ModelRequest>(provider.LastRequest).Reasoning);
        Assert.Equal(1, reserveCalls);
        Assert.Equal(1, dispatchCalls);
        Assert.Equal(1, receiptCalls);
        Assert.Equal(new long[] { 1 }, receiptSendCounts);
        Assert.Collection(sink.Payloads,
            payload => Assert.IsType<MetaModelInvocationStarted>(payload),
            payload => Assert.IsType<MetaModelInvocationCompleted>(payload));
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("unlisted")]
    public async Task Probe_runner_rejects_contradictory_selection_before_observer_or_provider(
        string declaration)
    {
        var provider = new ProviderSpy();
        var observer = new ProbeObserverSpy();
        var request = new ProbeRequest(Probe(), Selection(Capability(declaration)));
        var runner = new ProbeRunner(provider, TimeSpan.FromSeconds(2), quoteCost: null, observer: observer);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunProbeAsync(request, CancellationToken.None));

        Assert.Equal(0, provider.Calls);
        Assert.Null(provider.LastRequest);
        Assert.Equal(0, observer.BeforeDispatchCalls);
        Assert.Equal(0, observer.CompletedCalls);
        Assert.Empty(observer.Observations);
    }

    [Theory]
    [InlineData("supported")]
    [InlineData("unknown-suggestive-name")]
    public async Task Probe_runner_preserves_matching_or_unknown_selection_on_request(string scenario)
    {
        var capability = scenario == "supported"
            ? new ReasoningCapability(true, ["high"])
            : ReasoningCapability.Unknown;
        var modelId = scenario == "unknown-suggestive-name" ? "reasoning-ultracode-high" : "fixture-model";
        var provider = new ProviderSpy();
        var observer = new ProbeObserverSpy();
        var runner = new ProbeRunner(provider, TimeSpan.FromSeconds(2), quoteCost: null, observer: observer);

        var result = await runner.RunProbeAsync(new ProbeRequest(Probe(), Selection(capability, modelId)),
            CancellationToken.None);

        Assert.Equal(ProbeStatus.Passed, result.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(High, Assert.IsType<ModelRequest>(provider.LastRequest).Reasoning);
        Assert.Equal(1, observer.BeforeDispatchCalls);
        Assert.Equal(1, observer.CompletedCalls);
        var observation = Assert.Single(observer.Observations);
        Assert.Equal(ProbeExecutionTermination.Completed, observation.Termination);
        Assert.Equal(1, observation.ObservedGenerationSends); // one in-memory send, not a provider charge
    }

    private static ReasoningCapability Capability(string declaration) => declaration switch
    {
        "unsupported" => new ReasoningCapability(false),
        "unlisted" => new ReasoningCapability(true, ["low"]),
        _ => throw new ArgumentOutOfRangeException(nameof(declaration)),
    };

    private static Probe Probe() => new(ProbeId.WellKnown("secondary-reasoning-selection"),
        ProbeKind.Reasoning, "Reply with only the word summary.", "summary", 0m);

    private static ModelSelection Selection(ReasoningCapability capability, string modelId = "fixture-model")
    {
        var route = ModelRoute.DefaultForModel(modelId, "fixture-provider", "https://fixture.invalid/v1",
            ProviderFamily.OpenAIResponses, reasoningCapability: capability);
        return new ModelSelection(new ModelIdValue(modelId), 8192, ToolMode.Direct, High, route.Id, route);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omni-secondary-reasoning-" + Guid.NewGuid().ToString("N"));

        public Fixture(ReasoningCapability capability, string modelId = "fixture-model")
        {
            Directory.CreateDirectory(_root);
            Artifacts = new FileArtifactStore(_root);
            Selection = SecondaryReasoningRequestTests.Selection(capability, modelId);
        }

        public FileArtifactStore Artifacts { get; }
        public ModelSelection Selection { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ProviderSpy : IModelProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ModelRequest? LastRequest { get; private set; }
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Interlocked.Increment(ref _calls);
            GenerationRequestAttemptScope.RecordGenerationSend(); // scripted fixture only
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("summary")], StopReason.EndTurn,
                new TokenUsage(3, 1, 0, 0, 0), null, new ProviderMetadata("offline", "fixture", null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }

    private sealed class ContextEventSinkSpy : IContextEventSink
    {
        public List<DomainEventPayload> Payloads { get; } = [];
        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payloads.Add(payload);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeObserverSpy : IProbeExecutionObserver
    {
        public int BeforeDispatchCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public List<ProbeExecutionObservation> Observations { get; } = [];

        public ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeDispatchCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation)
        {
            CompletedCalls++;
            Observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }
}
