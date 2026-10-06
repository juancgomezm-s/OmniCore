namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class SharedProviderCircuitTests
{
    [Fact]
    public async System.Threading.Tasks.Task Catalog_shares_open_state_by_provider_key_and_isolates_other_providers()
    {
        var now = DateTimeOffset.UnixEpoch;
        var catalog = new ProviderResilienceCatalog();
        var options = Options(catalog, () => now);
        Assert.Null(catalog.Snapshot("provider-a"));

        var failedHandler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var firstAdapter = CreateProvider("provider-a", failedHandler, options);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(firstAdapter.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal(1, failedHandler.Count);

        var open = Assert.IsType<ProviderCircuitSnapshot>(catalog.Snapshot("provider-a"));
        Assert.Equal(now, open.MeasuredAt);
        Assert.Equal(now + options.CircuitCooldown, open.OpenUntil);
        Assert.False(open.ProbeInFlight);
        Assert.False(open.CanAttempt);
        Assert.Equal(open, catalog.Snapshot("provider-a")); // A read neither reserves nor mutates the breaker.

        var sameProviderHandler = new ResponseHandler(_ => Success());
        var secondAdapter = CreateProvider("provider-a", sameProviderHandler, options);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(secondAdapter.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal(0, sameProviderHandler.Count); // A new adapter cannot reset the shared provider handle.

        var otherProviderHandler = new ResponseHandler(_ => Success());
        var otherProvider = CreateProvider("provider-b", otherProviderHandler, options);
        await Collect(otherProvider.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(1, otherProviderHandler.Count);
        Assert.True(catalog.Snapshot("provider-b")!.CanAttempt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Cooldown_allows_one_half_open_probe_then_success_closes_circuit()
    {
        var now = DateTimeOffset.UnixEpoch;
        var catalog = new ProviderResilienceCatalog();
        var options = Options(catalog, () => now);
        var failure = CreateProvider("provider-a",
            new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), options);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(failure.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        now += options.CircuitCooldown;
        var expired = catalog.Snapshot("provider-a")!;
        Assert.True(expired.CanAttempt);
        Assert.False(expired.ProbeInFlight);
        Assert.Equal(expired, catalog.Snapshot("provider-a")); // Availability reads do not claim the probe.

        var blockedHandler = new BlockingResponseHandler();
        var probeAdapter = CreateProvider("provider-a", blockedHandler, options);
        var probeTask = Collect(probeAdapter.StreamAsync(Request(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        try
        {
            await blockedHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var probing = catalog.Snapshot("provider-a")!;
            Assert.True(probing.ProbeInFlight);
            Assert.False(probing.CanAttempt);

            var competingHandler = new ResponseHandler(_ => Success());
            var competingAdapter = CreateProvider("provider-a", competingHandler, options);
            await Assert.ThrowsAsync<ModelProviderException>(() => Collect(competingAdapter.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
            Assert.Equal(0, competingHandler.Count); // Concurrent half-open work is rejected before HTTP.
        }
        finally
        {
            // Avoid leaving a blocked HTTP worker alive if an assertion or wait fails.
            blockedHandler.Complete(Success());
        }
        await probeTask;
        var closed = catalog.Snapshot("provider-a")!;
        Assert.Null(closed.OpenUntil);
        Assert.False(closed.ProbeInFlight);
        Assert.True(closed.CanAttempt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Canceling_half_open_request_releases_only_its_probe()
    {
        var now = DateTimeOffset.UnixEpoch;
        var catalog = new ProviderResilienceCatalog();
        var options = Options(catalog, () => now);
        var failure = CreateProvider("provider-a",
            new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), options);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(failure.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        now += options.CircuitCooldown;

        var blockedHandler = new BlockingResponseHandler();
        var probeAdapter = CreateProvider("provider-a", blockedHandler, options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probeTask = Collect(probeAdapter.StreamAsync(Request(), cancellation.Token), cancellation.Token);
        try
        {
            await blockedHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(catalog.Snapshot("provider-a")!.ProbeInFlight);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
        }
        finally
        {
            cancellation.Cancel();
        }
        var released = catalog.Snapshot("provider-a")!;
        Assert.False(released.ProbeInFlight);
        Assert.True(released.CanAttempt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Canceled_subscription_refresh_does_not_leave_half_open_probe_reserved()
    {
        var now = DateTimeOffset.UnixEpoch;
        var catalog = new ProviderResilienceCatalog();
        var resilience = Options(catalog, () => now);
        var descriptor = new ProviderDescriptor("responses-provider", ProviderFamily.OpenAIResponses,
            "https://example.test/v1", AuthConfig.None(), false, false, true) { Profile = "codex" };
        var subscription = new TestSubscriptionCredentialSource(cancelRefresh: true);
        var opening = new OpenAIResponsesProvider(descriptor, new EmptySecrets(),
            () => new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                disposeHandler: false),
            new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex, Resilience = resilience }, subscription);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(opening.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        now += resilience.CircuitCooldown;
        var refreshing = new OpenAIResponsesProvider(descriptor, new EmptySecrets(),
            () => new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                disposeHandler: false),
            new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex, Resilience = resilience }, subscription);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(refreshing.StreamAsync(Request(), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        var afterRefreshFailure = catalog.Snapshot("responses-provider")!;
        Assert.False(afterRefreshFailure.ProbeInFlight);
        Assert.Equal(now + resilience.CircuitCooldown, afterRefreshFailure.OpenUntil);
        Assert.False(afterRefreshFailure.CanAttempt);
    }

    private static OpenAiProviderOptions Options(ProviderResilienceCatalog catalog, Func<DateTimeOffset> clock) =>
        new()
        {
            CircuitCatalog = catalog,
            CircuitFailureThreshold = 1,
            CircuitCooldown = TimeSpan.FromMinutes(1),
            MaxRetries = 0,
            UtcNow = clock,
            DelayAsync = static (_, _) => ValueTask.CompletedTask,
        };

    private static OpenAiChatCompatibleProvider CreateProvider(string providerId, HttpMessageHandler handler,
        OpenAiProviderOptions options) => new(
        new ProviderDescriptor(providerId, ProviderFamily.OpenAiChatCompatible, "https://example.test/v1",
            AuthConfig.None(), false, false, true), new EmptySecrets(),
        () => new HttpClient(handler, disposeHandler: false), options, providerId);

    private static ModelRequest Request()
    {
        var selection = new ModelSelection(new ModelIdValue("model-a"), 4096, ToolMode.Direct, null);
        return new ModelRequest(selection,
            [new ModelMessage(MessageRole.User, [new TextBlock("hello")])], null, [], ToolChoice.Auto(),
            null, null, null, null);
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(
        IAsyncEnumerable<ModelStreamEvent> stream, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(cancellationToken)) events.Add(item);
        return events;
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No API key expected");
    }

    private sealed class TestSubscriptionCredentialSource(bool cancelRefresh) : ISubscriptionCredentialSource
    {
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SubscriptionCredential("fixture-access-token", "fixture-account"));

        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken) => cancelRefresh
            ? ValueTask.FromException<SubscriptionCredential>(new OperationCanceledException(cancellationToken))
            : ValueTask.FromResult(new SubscriptionCredential("fixture-refreshed-token", "fixture-account"));
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return System.Threading.Tasks.Task.FromResult(responseFactory(request));
        }
    }

    private sealed class BlockingResponseHandler : HttpMessageHandler
    {
        private int _count;
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => Volatile.Read(ref _count);
        public void Complete(HttpResponseMessage response) => _response.TrySetResult(response);

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            Started.TrySetResult(true);
            return await _response.Task.WaitAsync(cancellationToken);
        }
    }
}
