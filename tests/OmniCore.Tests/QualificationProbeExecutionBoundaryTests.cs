using System.Runtime.CompilerServices;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Injected provider/observer fixtures, no authenticated queries or account debits.
/// The observer is not a durable store; these tests cover the boundary used by that store.</summary>
public sealed class QualificationProbeExecutionBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 18, 0, 1, TimeSpan.FromHours(-6));
    private static ProbeRequest Request(string id) => new(
        new Probe(ProbeId.WellKnown(id), ProbeKind.Reading, "Reply measured", "measured", 0m),
        new ModelSelection(new ModelIdValue("boundary-fixture"), 8192, ToolMode.Direct, null));
    private static ModelResponse Response(string answer = "measured") => new(
        [new TextBlock(answer)], StopReason.EndTurn, new TokenUsage(17, 4, 0, 0, 0), null,
        new ProviderMetadata("fixture", "boundary-fixture", null),
        TokenUsageFields.Input | TokenUsageFields.Output);
    private static ProbeRunner Runner(IModelProvider provider, Observer observer,
        TimeSpan? timeout = null) => new(provider, timeout ?? TimeSpan.FromSeconds(5),
            new ModelPricing(2m, 8m).CostUsd, observer, () => Now);

    [Fact]
    public async Task Admission_rejection_never_enters_provider_or_emits_receipt()
    {
        var provider = new Provider();
        var observer = new Observer { AdmissionError = new IOException("admission unavailable") };
        var error = await Assert.ThrowsAsync<IOException>(() => Runner(provider, observer)
            .RunSuiteAsync([Request("first"), Request("second")], QualificationConsent.Local(), CancellationToken.None));
        Assert.Same(observer.AdmissionError, error);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(["admit:first"], observer.Order);
        Assert.Empty(observer.Receipts);
    }

    [Fact]
    public async Task Receipt_is_awaited_before_next_admission_and_keeps_exact_usage_and_utc()
    {
        var observer = new Observer();
        var provider = new Provider(observer.Order);
        var results = await Runner(provider, observer).RunSuiteAsync(
            [Request("first"), Request("second")], QualificationConsent.Local(), CancellationToken.None);
        Assert.Equal(["admit:first", "send:1", "receipt:first", "admit:second", "send:2", "receipt:second"], observer.Order);
        Assert.Equal(2, results.Count);
        Assert.Equal(2, observer.Receipts.Count);
        foreach (var observation in observer.Receipts)
        {
            Assert.Equal(ProbeExecutionTermination.Completed, observation.Termination);
            Assert.Equal(Now.ToUniversalTime(), observation.CompletedAtUtc);
            Assert.Equal(TimeSpan.Zero, observation.CompletedAtUtc.Offset);
            Assert.Equal(1, observation.ObservedGenerationSends);
            Assert.Equal(0.000066m, observation.Result.CostUsd);
            Assert.Equal(Response().Usage, observation.Result.Usage);
            Assert.Equal(Response().ReportedUsageFields, observation.Result.ReportedUsageFields);
        }
    }

    [Fact]
    public async Task Receipt_failure_propagates_unchanged_and_prevents_next_provider_call()
    {
        var observer = new Observer { ReceiptError = new IOException("receipt not durable") };
        var provider = new Provider();
        var error = await Assert.ThrowsAsync<IOException>(() => Runner(provider, observer).RunSuiteAsync(
            [Request("first"), Request("second")], QualificationConsent.Local(), CancellationToken.None));
        Assert.Same(observer.ReceiptError, error);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(["admit:first", "receipt:first"], observer.Order);
        Assert.Single(observer.Receipts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_or_timeout_retains_completed_probe_and_interrupted_probe_usage(bool callerCancel)
    {
        using var cts = new CancellationTokenSource();
        var observer = new Observer();
        var provider = new Provider(interruptAt: 2, cancelCaller: callerCancel ? cts : null);
        var runner = Runner(provider, observer, TimeSpan.FromMilliseconds(100));
        var error = await Record.ExceptionAsync(() => runner.RunSuiteAsync(
            [Request("first"), Request("second"), Request("never-started")], QualificationConsent.Local(), cts.Token));
        if (callerCancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<ProbeTimeoutException>(error);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, observer.Receipts.Count);
        Assert.Equal(ProbeStatus.Passed, observer.Receipts[0].Result.Status);
        var interrupted = observer.Receipts[1];
        Assert.Equal(callerCancel ? ProbeExecutionTermination.Cancelled : ProbeExecutionTermination.TimedOut,
            interrupted.Termination);
        Assert.Equal(ProbeStatus.Error, interrupted.Result.Status);
        Assert.Equal(0.000066m, interrupted.Result.CostUsd);
        Assert.Equal(Response().Usage, interrupted.Result.Usage);
        Assert.Equal(1, interrupted.ObservedGenerationSends);
        Assert.DoesNotContain("admit:never-started", observer.Order);
    }

    [Fact]
    public async Task Already_cancelled_probe_does_not_admit_send_or_observe()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var observer = new Observer();
        var provider = new Provider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(provider, observer)
            .RunProbeAsync(Request("first"), cts.Token));
        Assert.Equal(0, provider.Calls);
        Assert.Empty(observer.Order);
        Assert.Empty(observer.Receipts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Attempt_observations_do_not_invent_sends_or_aggregate_other_probes(int sends)
    {
        var observer = new Observer();
        var provider = new Provider(sends: sends);
        await Runner(provider, observer).RunSuiteAsync([Request("first"), Request("second")],
            QualificationConsent.Local(), CancellationToken.None);
        Assert.All(observer.Receipts, receipt => Assert.Equal(sends, receipt.ObservedGenerationSends));
        // Counter scope was restored; it cannot bleed into an unrelated invocation.
        using var later = GenerationRequestAttemptScope.Enter();
        Assert.Equal(0, later.ObservedSends);
    }

    [Fact]
    public async Task Failed_scoring_still_observes_known_consumption()
    {
        var observer = new Observer();
        await Runner(new Provider(answer: "wrong"), observer).RunProbeAsync(Request("first"), CancellationToken.None);
        var result = Assert.Single(observer.Receipts).Result;
        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Equal(0.000066m, result.CostUsd);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_without_completed_response_never_invents_usage_or_zero_cost(bool callerCancel)
    {
        using var cts = new CancellationTokenSource();
        var observer = new Observer();
        var provider = new Provider(interruptAt: 1, cancelCaller: callerCancel ? cts : null,
            emitResponse: false);
        var error = await Record.ExceptionAsync(() => Runner(provider, observer, TimeSpan.FromMilliseconds(100))
            .RunProbeAsync(Request("first"), cts.Token));
        if (callerCancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<ProbeTimeoutException>(error);
        var receipt = Assert.Single(observer.Receipts);
        Assert.Null(receipt.Result.Usage);
        Assert.Null(receipt.Result.CostUsd);
        Assert.Equal(TokenUsageFields.None, receipt.Result.ReportedUsageFields);
        Assert.Equal(1, receipt.ObservedGenerationSends);
    }

    [Fact]
    public async Task Provider_exception_after_complete_response_preserves_usage_in_observation()
    {
        var observer = new Observer();
        var provider = new Provider(providerError: new IOException("fixture provider failed after response"));
        var result = await Runner(provider, observer).RunProbeAsync(Request("first"), CancellationToken.None);
        Assert.Equal(ProbeStatus.Error, result.Status);
        Assert.Equal(Response().Usage, result.Usage);
        Assert.Equal(0.000066m, result.CostUsd);
        var observation = Assert.Single(observer.Receipts);
        Assert.Same(result, observation.Result);
        Assert.Equal(ProbeExecutionTermination.Failed, observation.Termination);
    }

    [Fact]
    public async Task Completed_response_then_failed_event_retains_usage_and_existing_suite_continuation()
    {
        var observer = new Observer();
        var provider = new Provider(failedEvent: true);
        var results = await Runner(provider, observer).RunSuiteAsync([Request("first"), Request("second")],
            QualificationConsent.Local(), CancellationToken.None);
        Assert.Equal(2, provider.Calls);
        Assert.All(results, result => Assert.Equal(ProbeStatus.Error, result.Status));
        Assert.All(observer.Receipts, receipt =>
        {
            Assert.Equal(ProbeExecutionTermination.Failed, receipt.Termination);
            Assert.Equal(Response().Usage, receipt.Result.Usage);
            Assert.Equal(Response().ReportedUsageFields, receipt.Result.ReportedUsageFields);
            Assert.Equal(0.000066m, receipt.Result.CostUsd);
        });
    }

    [Fact]
    public async Task Rejection_of_second_admission_keeps_first_receipt_and_prevents_second_or_third_send()
    {
        var observer = new Observer { AdmissionError = new IOException("second admission failed"), RejectAt = 2 };
        var provider = new Provider();
        await Assert.ThrowsAsync<IOException>(() => Runner(provider, observer).RunSuiteAsync(
            [Request("first"), Request("second"), Request("third")], QualificationConsent.Local(), CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        Assert.Single(observer.Receipts);
        Assert.Equal(["admit:first", "receipt:first", "admit:second"], observer.Order);
    }

    [Fact]
    public async Task Completion_clock_is_per_probe_and_normalized_across_utc_midnight()
    {
        var observer = new Observer();
        var clockCalls = 0;
        var before = new DateTimeOffset(2026, 10, 6, 17, 59, 59, TimeSpan.FromHours(-6));
        var runner = new ProbeRunner(new Provider(), TimeSpan.FromSeconds(5), new ModelPricing(2m, 8m).CostUsd,
            observer, () => before.AddSeconds(clockCalls++ * 2));
        await runner.RunSuiteAsync([Request("first"), Request("second")], QualificationConsent.Local(), CancellationToken.None);
        Assert.Equal(2, clockCalls);
        Assert.Equal(new DateOnly(2026, 10, 6), DateOnly.FromDateTime(observer.Receipts[0].CompletedAtUtc.UtcDateTime));
        Assert.Equal(new DateOnly(2026, 10, 7), DateOnly.FromDateTime(observer.Receipts[1].CompletedAtUtc.UtcDateTime));
        Assert.All(observer.Receipts, receipt => Assert.Equal(TimeSpan.Zero, receipt.CompletedAtUtc.Offset));
    }

    [Fact]
    public async Task Cancellation_after_admission_does_not_infer_free_from_zero_observed_sends()
    {
        using var cts = new CancellationTokenSource();
        var observer = new Observer { CancelAfterAdmission = cts };
        var provider = new Provider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(provider, observer).RunProbeAsync(Request("first"), cts.Token));
        var receipt = Assert.Single(observer.Receipts);
        Assert.Equal(ProbeExecutionTermination.Cancelled, receipt.Termination);
        Assert.Equal(0, receipt.ObservedGenerationSends);
        Assert.Null(receipt.Result.CostUsd);
        Assert.Null(receipt.Result.Usage);
    }

    [Fact]
    public async Task Native_http_retries_flow_through_runner_attempt_scope_but_only_last_usage_is_reported()
    {
        using var handler = new RetryHandler();
        var provider = new OpenAiChatCompatibleProvider(
            new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
                "https://fixture.invalid/v1", AuthConfig.None(), false, false, true),
            new EmptySecrets(), () => new HttpClient(handler, disposeHandler: false),
            new OpenAiProviderOptions
            {
                MaxRetries = 2, CircuitFailureThreshold = 10,
                DelayAsync = static (_, _) => ValueTask.CompletedTask, Jitter = static () => 0,
            });
        var observer = new Observer();
        await Runner(provider, observer).RunProbeAsync(Request("first"), CancellationToken.None);
        Assert.Equal(3, handler.Calls);
        var receipt = Assert.Single(observer.Receipts);
        Assert.Equal(3, receipt.ObservedGenerationSends);
        Assert.Equal(ProbeExecutionTermination.Completed, receipt.Termination);
        Assert.Equal(17, receipt.Result.Usage!.Input);
        Assert.Equal(4, receipt.Result.Usage.Output);
        Assert.Equal(0.000066m, receipt.Result.CostUsd);
        // This is the successful response only, not a claim the two failed sends were free.
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Auth.None fixture must not resolve credentials.");
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls <= 2)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("fixture transient error") });
            if (Calls > 3) throw new InvalidOperationException("unexpected fixture HTTP request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"measured\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":4}}\n\n" +
                    "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class Observer : IProbeExecutionObserver
    {
        public List<string> Order { get; } = [];
        public List<ProbeExecutionObservation> Receipts { get; } = [];
        public Exception? AdmissionError { get; init; }
        public Exception? ReceiptError { get; init; }
        public int RejectAt { get; init; } = 1;
        public CancellationTokenSource? CancelAfterAdmission { get; init; }
        private int _admissions;
        public ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Order.Add("admit:" + request.Probe.Id);
            if (++_admissions == RejectAt && AdmissionError is not null) throw AdmissionError;
            CancelAfterAdmission?.Cancel();
            return ValueTask.CompletedTask;
        }
        public async ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation)
        {
            await Task.Yield();
            Order.Add("receipt:" + request.Probe.Id);
            Receipts.Add(observation);
            if (ReceiptError is not null) throw ReceiptError;
        }
    }

    private sealed class Provider(List<string>? order = null, int interruptAt = 0,
        CancellationTokenSource? cancelCaller = null, int sends = 1, string answer = "measured",
        bool emitResponse = true, Exception? providerError = null, bool failedEvent = false) : IModelProvider
    {
        public int Calls { get; private set; }
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            order?.Add("send:" + Calls);
            for (var i = 0; i < sends; i++) GenerationRequestAttemptScope.RecordGenerationSend();
            await Task.Yield();
            if (emitResponse) yield return new ResponseCompleted(Response(answer));
            if (providerError is not null) throw providerError;
            if (failedEvent) yield return new ResponseFailed("fixture-failure", "fixture failed after response");
            if (Calls == interruptAt)
            {
                cancelCaller?.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }
}
