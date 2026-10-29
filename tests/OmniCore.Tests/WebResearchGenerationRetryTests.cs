namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

public sealed class WebResearchGenerationRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task Codex_401_refresh_resend_is_charged_before_handler_or_is_allowed_with_sufficient_budget(bool enoughQuota)
    {
        using var fixture = new Fixture(enoughQuota ? 20_000 : 2_048);
        var (callId, context) = await fixture.ResearchContext();
        var before = fixture.Controller.State;
        var contextBytes = Encoding.UTF8.GetByteCount(context);
        using var handler = new AuthRetryHandler();
        using var subscription = new FakeSubscription();
        var descriptor = new ProviderDescriptor("openai", ProviderFamily.OpenAIResponses,
            "https://api.example.test/v1", AuthConfig.None(), false, false, true);
        var provider = new OpenAIResponsesProvider(descriptor, new NoSecrets(),
            () => new HttpClient(handler, disposeHandler: false),
            new OpenAIResponsesOptions
            {
                Profile = ResponsesProfile.Codex,
                Resilience = new OpenAiProviderOptions { MaxRetries = 0 },
            }, subscription);
        var invocation = fixture.Invocation(callId, context);
        using var attempts = GenerationRequestAttemptScope.Enter(invocation.BeforePhysicalSend);

        if (enoughQuota)
        {
            var events = await Collect(provider.StreamAsync(invocation.Request, TestContext.Current.CancellationToken));
            Assert.IsType<ResponseCompleted>(events[^1]);
            Assert.Equal(2, handler.Calls);
            Assert.Equal(2, attempts.ObservedSends);
            Assert.Equal(1, subscription.RefreshCalls);
            Assert.Equal("Bearer fixture-old", handler.Authorization[0]);
            Assert.Equal("Bearer fixture-refreshed", handler.Authorization[1]);
            Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
            Assert.Equal(context, ToolOutput(handler.Bodies[0], callId));
            Assert.Equal(contextBytes, fixture.Controller.State.CachedContextTokensUsed);
            var retry = Assert.Single(fixture.Controller.State.ContextDeliveries, delivery => delivery.PhysicalRetry);
            Assert.Equal(contextBytes, retry.Utf8Bytes);
            Assert.Equal(contextBytes, retry.AdditionalTokens);
            Assert.True(fixture.Controller.State.EvidenceTokensUsed + fixture.Controller.State.CachedContextTokensUsed
                <= fixture.Controller.State.Limits.EvidenceTokens);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.StreamAsync(invocation.Request,
                TestContext.Current.CancellationToken)));
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, attempts.ObservedSends);
            Assert.Equal(1, subscription.RefreshCalls);
            Assert.Equal("Bearer fixture-old", Assert.Single(handler.Authorization));
            Assert.Equal(context, ToolOutput(Assert.Single(handler.Bodies), callId));
            Assert.Equal(0, fixture.Controller.State.CachedContextTokensUsed);
            Assert.DoesNotContain(fixture.Controller.State.ContextDeliveries, delivery => delivery.PhysicalRetry);
            Assert.Equal(WebResearchStatus.BudgetExhausted, fixture.Controller.State.Status);
            Assert.True(fixture.Controller.State.EvidenceTokensUsed + fixture.Controller.State.CachedContextTokensUsed
                <= fixture.Controller.State.Limits.EvidenceTokens);
        }
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal(before.QueriesUsed, fixture.Controller.State.QueriesUsed);
        Assert.Equal(before.EvidenceTokensUsed, fixture.Controller.State.EvidenceTokensUsed);
    }

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static string ToolOutput(string requestBody, ToolCallId callId)
    {
        using var document = JsonDocument.Parse(requestBody);
        var output = document.RootElement.GetProperty("input").EnumerateArray().Single(item =>
            item.GetProperty("type").GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == callId.ToString());
        return output.GetProperty("output").GetString()!;
    }

    private static string SuccessSse => """
event: response.output_item.added
data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","output_index":0,"delta":"fixture success"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"fixture success"}]}}

event: response.completed
data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

""";

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-web-generation-retry-" + Guid.NewGuid().ToString("N"));
        private readonly InMemoryEventStore _journal = new();
        private readonly EventCodecs _codecs = EventCodecs.Create();
        private readonly SessionId _session = SessionId.New();
        private readonly TestRun.Opened _run;
        private readonly EventStream _stream;
        internal SqliteWebResearchStore Store { get; }
        internal WebResearchController Controller { get; }
        internal FakeSearch Provider { get; } = new();

        internal Fixture(long tokenLimit)
        {
            Directory.CreateDirectory(_root);
            _run = TestRun.Open(_journal, _session, mode: RunMode.Plan);
            _stream = new EventStream(_journal, _codecs, _session);
            Store = new SqliteWebResearchStore(Path.Combine(_root, "research.db"));
            Controller = new WebResearchController(Store, _run.RunId.ToString(), [new("q1", "Verify the release")],
                new WebResearchLimits(2, 2, 1, tokenLimit, 120), new FakeReader(), ["https://source.test"], Provider);
        }

        internal async System.Threading.Tasks.Task<(ToolCallId CallId, string Context)> ResearchContext()
        {
            var callId = ToolCallId.New();
            var reply = await Controller.SearchAsync(callId.ToString(), new WebSearchRequest("official release date", "q1", 1,
                [], false, new Dictionary<string, string>()), contextTokenCeiling: 2048,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(reply.ContextSuppressed);
            using (ExecutionScope.Begin(new(_run.RunId, _run.RootTask, _run.RootLane)))
                _stream.Append(new ToolCallRequested(callId, "fixture", "search.web", "{}"));
            return (callId, JsonSerializer.Serialize(reply, WebResearchJson.Default.WebResearchReply));
        }

        internal WebResearchContextGuard.Invocation Invocation(ToolCallId callId, string context)
        {
            var request = new ModelRequest(new ModelSelection(new ModelIdValue("gpt-test"), 4096, ToolMode.Direct, null),
                [new ModelMessage(MessageRole.Tool, [new ToolResultBlock(callId, [new TextBlock(context)], false)])],
                null, [], ToolChoice.Auto(), null, null, null, null);
            var guard = new WebResearchContextGuard(Store);
            var projection = guard.Project(_run.RunId.ToString(), _journal.ReadFrom(_session, 1), _codecs);
            return guard.Admit(_run.RunId.ToString(), "initial-generation", request, projection);
        }

        public void Dispose()
        {
            Controller.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSearch : IWebSearchProvider
    {
        public string Id => "fixture";
        public Uri Endpoint { get; } = new("https://search.test/search");
        public int Calls { get; private set; }
        public System.Threading.Tasks.Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new WebSearchResponse(
                [new WebSearchCandidate("https://source.test/release", "Release", "Fixture result", Id)], [], []));
        }
    }

    private sealed class FakeReader : IWebReadTransport
    {
        public System.Threading.Tasks.Task<WebReadResponse> ReadAsync(WebReadRequest request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new WebReadResponse(request.Url.AbsoluteUri, request.Url.AbsoluteUri, "fixture", "fixture",
                DateTimeOffset.UtcNow, [request.Url.AbsoluteUri], false));
    }

    private sealed class FakeSubscription : ISubscriptionCredentialSource, IDisposable
    {
        public int RefreshCalls { get; private set; }
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SubscriptionCredential("fixture-old", "fixture-account"));
        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return ValueTask.FromResult(new SubscriptionCredential("fixture-refreshed", "fixture-account"));
        }
        public void Dispose() { }
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The Codex subscription fixture must not resolve API-key secrets.");
    }

    private sealed class AuthRetryHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _authorization = new();
        private readonly ConcurrentQueue<string> _bodies = new();
        private int _calls;
        public int Calls => _calls;
        public IReadOnlyList<string> Authorization => _authorization.ToArray();
        public IReadOnlyList<string> Bodies => _bodies.ToArray();

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _authorization.Enqueue(request.Headers.Authorization?.ToString() ?? "");
            _bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            var call = Interlocked.Increment(ref _calls);
            if (call == 1) return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("{\"error\":{\"message\":\"fixture expired\"}}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(SuccessSse, Encoding.UTF8, "text/event-stream") };
        }
    }
}
