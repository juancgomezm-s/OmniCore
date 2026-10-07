using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Explicit reasoning must not silently disappear at adapters that have no
/// translation for it. Every endpoint, response and API-key value is synthetic/in-memory.
/// </summary>
public sealed class OtherAdaptersReasoningBoundaryTests
{
    private const string AnthropicEndpoint = "https://anthropic.fixture.invalid";
    private const string ChatEndpoint = "https://chat.fixture.invalid/v1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anthropic_effort_without_budget_is_rejected_before_secret_or_http(bool selectionOnly)
    {
        var handler = new CaptureHandler(AnthropicCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = AnthropicFixture(handler, secrets,
            new ReasoningCapability(true, ["high"]), new ReasoningRequest("high", null));
        if (selectionOnly) request = SelectionOnly(request);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal(0, secrets.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Anthropic_explicit_budget_control_keeps_native_thinking_and_output_behavior()
    {
        var handler = new CaptureHandler(AnthropicCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = AnthropicFixture(handler, secrets,
            new ReasoningCapability(true, ["budget"]), new ReasoningRequest("budget", 1024));

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(1024, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal(5120, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(AnthropicEndpoint + "/v1/messages", sent.Url);
        Assert.Equal("synthetic-api-key", sent.Headers["x-api-key"]);
        Assert.Equal(1, secrets.Calls);
    }

    [Fact]
    public async Task Anthropic_null_reasoning_keeps_default_request_without_thinking()
    {
        var handler = new CaptureHandler(AnthropicCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = AnthropicFixture(handler, secrets,
            ReasoningCapability.Unknown, reasoning: null);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(8192, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(1, secrets.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_compatible_explicit_effort_is_rejected_before_secret_or_http(bool selectionOnly)
    {
        var handler = new CaptureHandler(ChatCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = ChatFixture(handler, secrets,
            new ReasoningCapability(true, ["high"]), new ReasoningRequest("high", null));
        if (selectionOnly) request = SelectionOnly(request);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal(0, secrets.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Chat_compatible_null_reasoning_keeps_chat_shape_and_parses_response_reasoning()
    {
        var handler = new CaptureHandler(ChatCompletionWithReasoning);
        var secrets = new CountingSecrets();
        var (provider, request) = ChatFixture(handler, secrets,
            ReasoningCapability.Unknown, reasoning: null);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        var events = await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("Bearer synthetic-api-key", sent.Headers["Authorization"]);
        Assert.Equal(1, secrets.Calls);

        var response = Assert.IsType<ResponseCompleted>(events[^1]).Response;
        Assert.Equal("think", Assert.Single(response.Content.OfType<ReasoningBlock>()).VisibleText);
        Assert.Equal(ReasoningVisibility.Full, Assert.Single(response.Content.OfType<ReasoningBlock>()).Visibility);
        Assert.Equal("ok", Assert.Single(response.Content.OfType<TextBlock>()).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1023)]
    public async Task Anthropic_invalid_manual_budget_fails_before_secret_or_http(int budget)
    {
        var handler = new CaptureHandler(AnthropicCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = AnthropicFixture(handler, secrets,
            ReasoningCapability.Unknown, new ReasoningRequest("budget", budget));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));
        Assert.Equal(0, secrets.Calls);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("high", 2048)]
    [InlineData("budget", null)]
    public async Task Anthropic_unmapped_or_missing_budget_is_not_reinterpreted(string kind, int? budget)
    {
        var handler = new CaptureHandler(AnthropicCompletion);
        var secrets = new CountingSecrets();
        var (provider, request) = AnthropicFixture(handler, secrets,
            ReasoningCapability.Unknown, new ReasoningRequest(kind, budget));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));
        Assert.Equal(0, secrets.Calls);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Direct_adapter_rejects_declared_contradiction_before_secret_or_http(
        bool anthropic, bool supportedButDifferentLabel)
    {
        var handler = new CaptureHandler(anthropic ? AnthropicCompletion : ChatCompletion);
        var secrets = new CountingSecrets();
        var capability = supportedButDifferentLabel
            ? new ReasoningCapability(true, ["different-label"])
            : new ReasoningCapability(false);
        IModelProvider provider;
        ModelRequest request;
        if (anthropic)
        {
            var fixture = AnthropicFixture(handler, secrets, capability, new ReasoningRequest("budget", 1024));
            provider = fixture.Provider;
            request = fixture.Request;
        }
        else
        {
            var fixture = ChatFixture(handler, secrets, capability, new ReasoningRequest("high", null));
            provider = fixture.Provider;
            request = fixture.Request;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
            provider.StreamAsync(SelectionOnly(request), TestContext.Current.CancellationToken)));
        Assert.Equal(0, secrets.Calls);
        Assert.Empty(handler.Requests);
    }

    private static (AnthropicMessagesProvider Provider, ModelRequest Request) AnthropicFixture(
        CaptureHandler handler, CountingSecrets secrets, ReasoningCapability capability,
        ReasoningRequest? reasoning)
    {
        const string providerId = "anthropic-fixture";
        const string modelId = "claude-fixture";
        var descriptor = new ProviderDescriptor(providerId, ProviderFamily.AnthropicMessages,
            AnthropicEndpoint, AuthConfig.ApiKey("synthetic-anthropic-ref"), false, false, true);
        var request = Request(descriptor, modelId, capability, reasoning);
        var options = new AnthropicProviderOptions
        {
            Resilience = new OpenAiProviderOptions { DelayAsync = static (_, _) => ValueTask.CompletedTask },
        };
        var provider = new AnthropicMessagesProvider(descriptor, secrets,
            () => new HttpClient(handler, disposeHandler: false), options);
        return (provider, request);
    }

    private static (OpenAiChatCompatibleProvider Provider, ModelRequest Request) ChatFixture(
        CaptureHandler handler, CountingSecrets secrets, ReasoningCapability capability,
        ReasoningRequest? reasoning)
    {
        const string providerId = "chat-compatible-fixture";
        const string modelId = "fixture-model";
        var descriptor = new ProviderDescriptor(providerId, ProviderFamily.OpenAiChatCompatible,
            ChatEndpoint, AuthConfig.ApiKey("synthetic-chat-ref"), false, false, true);
        var request = Request(descriptor, modelId, capability, reasoning);
        var options = new OpenAiProviderOptions { DelayAsync = static (_, _) => ValueTask.CompletedTask };
        var provider = new OpenAiChatCompatibleProvider(descriptor, secrets,
            () => new HttpClient(handler, disposeHandler: false), options);
        return (provider, request);
    }

    private static ModelRequest Request(ProviderDescriptor descriptor, string modelId,
        ReasoningCapability capability, ReasoningRequest? reasoning)
    {
        var route = new ModelRoute(descriptor.Id, descriptor.BaseUrl, descriptor.Family,
            profile: null, providerModelName: modelId, reasoningCapability: capability);
        var selection = new ModelSelection(new ModelIdValue(modelId), 4096,
            ToolMode.Direct, reasoning, route: route);
        return new ModelRequest(selection,
            [new ModelMessage(MessageRole.User, [new TextBlock("synthetic request")])],
            null, [], ToolChoice.None(), null, reasoning, null, null);
    }

    private static ModelRequest SelectionOnly(ModelRequest request) => new(request.Model,
        request.Messages, request.Instructions, request.Tools, request.ToolChoice,
        request.Output, null, request.Cache, request.Continuation);

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private const string AnthropicCompletion = """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":3,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string ChatCompletion = """
        data: {"choices":[{"delta":{"content":"ok"},"finish_reason":null}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}

        data: [DONE]

        """;

    private const string ChatCompletionWithReasoning = """
        data: {"choices":[{"delta":{"content":"ok","reasoning_content":"think"},"finish_reason":null}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}

        data: [DONE]

        """;

    private sealed class CountingSecrets : ISecretProvider
    {
        public int Calls { get; private set; }
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken)
        {
            Calls++;
            return Secret.Of("synthetic-api-key");
        }
    }

    private sealed record SentRequest(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class CaptureHandler(string responseBody) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SentRequest> _requests = new();
        public IReadOnlyList<SentRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(pair => pair.Key,
                pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new SentRequest(request.RequestUri!.ToString(), headers, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
