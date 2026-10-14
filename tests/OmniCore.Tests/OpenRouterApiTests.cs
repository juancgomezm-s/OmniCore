namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class OpenRouterApiTests
{
    private const string Key = "or-fixture-key-with-enough-length-0123456789";

    private sealed class FixtureHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _replies = new(replies);
        public List<(string Method, string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            if (_replies.Count == 0) throw new InvalidOperationException("Unexpected request.");
            return System.Threading.Tasks.Task.FromResult(_replies.Dequeue()(request));
        }
    }

    private sealed class StalledBody : HttpContent
    {
        protected override System.Threading.Tasks.Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            System.Threading.Tasks.Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override System.Threading.Tasks.Task<Stream> CreateContentReadStreamAsync() =>
            System.Threading.Tasks.Task.FromResult<Stream>(new StalledStream());

        private sealed class StalledStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenRouterApi Api(FixtureHandler handler, DateTimeOffset? now = null) =>
        new(() => new HttpClient(handler, disposeHandler: false), () => now ?? DateTimeOffset.Parse("2026-10-13T18:00:00Z"));

    [Fact]
    public async System.Threading.Tasks.Task Key_endpoint_measures_limit_fields_without_calling_them_account_balance()
    {
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            """{"data":{"limit":20,"limit_remaining":7.25,"usage":12.75,"limit_reset":"daily","is_management_key":false}}"""));
        var result = await Api(handler).TestKeyAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderConnectionState.Connected, result.State);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v1/key", request.Uri, StringComparison.Ordinal);
        Assert.Equal("Bearer " + Key, request.Authorization);
    }

    [Fact]
    public async System.Threading.Tasks.Task Catalog_page_preserves_reported_metadata_and_uses_official_string_pricing()
    {
        var body = """{"data":[{"id":"vendor/model-a","name":"Model A","description":"fixture","context_length":131072,"top_provider":{"max_completion_tokens":8192},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools","temperature"],"pricing":{"prompt":"0.0000004","completion":"0.0000012"},"new_field":{"opaque":true}}]}""";
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK, body));
        var page = await Api(handler).ListModelsAsync(Key, offset: 1000, limit: 100,
            TestContext.Current.CancellationToken);

        Assert.Equal(1000, page.Offset);
        Assert.Equal(100, page.Limit);
        Assert.False(page.HasMore);
        var model = Assert.Single(page.Models);
        Assert.Equal("vendor/model-a", model.Id);
        Assert.Equal(131072, model.ContextLength);
        Assert.Equal(8192, model.MaxCompletionTokens);
        Assert.Equal("0.0000004", model.PromptPricePerToken);
        Assert.Equal("0.0000012", model.CompletionPricePerToken);
        Assert.Contains("new_field", model.RawJson, StringComparison.Ordinal);
        Assert.Equal("https://openrouter.ai/api/v1/models?offset=1000&limit=100", Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async System.Threading.Tasks.Task Normal_key_credits_response_keeps_key_limit_and_account_balance_scopes_separate()
    {
        var handler = new FixtureHandler(
            _ => Json(HttpStatusCode.OK, """{"data":{"limit":12,"limit_remaining":4.5,"usage":7.5,"is_management_key":false}}"""),
            _ => Json(HttpStatusCode.Forbidden, """{"error":{"message":"management key required"}}"""));
        var snapshot = await Api(handler).ReadCreditsAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(MetricAvailability.NotSupported, snapshot.AccountBalanceAvailability);
        Assert.Equal("management_key_required", snapshot.Limitation);
        var keyLimit = Assert.Single(snapshot.Measurements);
        Assert.Equal(CreditScope.KeyLimit, keyLimit.Scope);
        Assert.Equal(4.5m, keyLimit.Amount.Value);
        Assert.DoesNotContain(snapshot.Measurements, item => item.Scope == CreditScope.AccountBalance);
        Assert.Equal("Bearer " + Key, handler.Requests[0].Authorization);
        Assert.Equal("Bearer " + Key, handler.Requests[1].Authorization);
    }

    [Fact]
    public async System.Threading.Tasks.Task Explicit_credit_read_maps_to_status_line_quota_without_inventing_account_balance()
    {
        var handler = new FixtureHandler(
            _ => Json(HttpStatusCode.OK, """{"data":{"limit":12,"limit_remaining":4.5,"usage":7.5,"is_management_key":false}}"""),
            _ => Json(HttpStatusCode.Forbidden, """{"error":{"message":"management key required"}}"""));
        var snapshot = await Api(handler).ReadCreditsAsync(Key, TestContext.Current.CancellationToken);

        var quota = OmniServer.OpenRouterQuota("openrouter", snapshot);
        Assert.Equal(MetricAvailability.Reported, quota.Availability);
        Assert.Equal("openrouter.credits", quota.Source);
        Assert.Empty(quota.Windows);
        Assert.Equal(CreditScope.KeyLimit, Assert.Single(quota.Credits).Scope);
        Assert.Contains("key limit 4.5 USD", OmniCore.Client.UsagePresentation.AccountQuota(quota, "en"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("account balance 0", OmniCore.Client.UsagePresentation.AccountQuota(quota, "en"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Management_key_balance_is_derived_only_from_both_reported_totals()
    {
        var handler = new FixtureHandler(
            _ => Json(HttpStatusCode.OK, """{"data":{"limit_remaining":9,"is_management_key":true}}"""),
            _ => Json(HttpStatusCode.OK, """{"data":{"total_credits":40,"total_usage":13.5}}"""));
        var snapshot = await Api(handler).ReadCreditsAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(MetricAvailability.Reported, snapshot.AccountBalanceAvailability);
        Assert.Collection(snapshot.Measurements,
            key => { Assert.Equal(CreditScope.KeyLimit, key.Scope); Assert.Equal(9m, key.Amount.Value); },
            account => { Assert.Equal(CreditScope.AccountBalance, account.Scope); Assert.Equal(26.5m, account.Amount.Value); });
        Assert.All(snapshot.Measurements, metric => Assert.Equal("USD", metric.Currency));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 1001)]
    public async System.Threading.Tasks.Task Catalog_page_bounds_are_rejected_without_http(int offset, int limit)
    {
        var handler = new FixtureHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api(handler).ListModelsAsync(Key,
            offset, limit, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task Redirect_and_transient_rate_limit_are_not_reported_as_connected()
    {
        var redirect = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect));
        var redirected = await Api(redirect).TestKeyAsync(Key, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderConnectionState.Unknown, redirected.State);
        Assert.Single(redirect.Requests);

        var limited = new FixtureHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}"));
        var rateLimited = await Api(limited).TestKeyAsync(Key, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderConnectionState.Unknown, rateLimited.State);
    }

    [Fact]
    public async System.Threading.Tasks.Task TimeoutCoversResponseBodyAndUserCancellationStillPropagates()
    {
        var handler = new FixtureHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody() },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody() });
        var api = new OpenRouterApi(() => new HttpClient(handler, disposeHandler: false),
            () => DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(40));
        var timedOut = await api.TestKeyAsync(Key, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderConnectionState.Unknown, timedOut.State);
        Assert.Equal("timeout", timedOut.Detail);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.TestKeyAsync(Key, cancellation.Token));
    }
}
