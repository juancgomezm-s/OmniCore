namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Adaptador nativo de la Responses API (perfiles api y codex), con SSE guionado sin red.</summary>
public sealed class OpenAIResponsesProviderTests
{
    private const string ToolStream = """
event: response.created
data: {"type":"response.created","response":{"id":"resp_1","status":"in_progress"}}

event: response.output_item.added
data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}

event: response.reasoning_summary_text.delta
data: {"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"mirar el archivo"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","encrypted_content":"ENC-XYZ","summary":[]}}

event: response.output_item.added
data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant"}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","output_index":1,"delta":"Leo a.cs"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"msg_1","content":[{"type":"output_text","text":"Leo a.cs"}]}}

event: response.output_item.added
data: {"type":"response.output_item.added","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"filesystem.read"}}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","output_index":2,"delta":"{\"path\":"}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","output_index":2,"delta":"\"a.cs\"}"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"filesystem.read","arguments":"{\"path\":\"a.cs\"}"}}

event: response.completed
data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","model":"gpt-test-resolved","usage":{"input_tokens":20,"output_tokens":9,"input_tokens_details":{"cached_tokens":4},"output_tokens_details":{"reasoning_tokens":3}}}}

""";

    [Fact]
    public async System.Threading.Tasks.Task Streams_reasoning_text_and_function_call_with_usage()
    {
        var events = await Collect(CreateProvider(new QueueHandler(_ => Sse(ToolStream)))
            .StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains(events, e => e is ReasoningDelta { Text: "mirar el archivo" });
        Assert.Contains(events, e => e is TextDelta { Text: "Leo a.cs" });
        Assert.Contains(events, e => e is ToolArgumentsDelta { PartialJson: "{\"path\":" });
        var response = Assert.IsType<ResponseCompleted>(events[^1]).Response;
        Assert.Equal(StopReason.ToolUse, response.StopReason);
        Assert.Equal(new TokenUsage(20, 9, 4, 0, 3), response.Usage);
        Assert.Equal("gpt-test-resolved", response.Metadata.Model);
        var call = Assert.Single(response.Content.OfType<ToolCallBlock>());
        Assert.Equal("call_1", call.ProviderCallId);
        Assert.Equal("{\"path\":\"a.cs\"}", call.ArgumentsJson);
        Assert.Equal("Leo a.cs", Assert.Single(response.Content.OfType<TextBlock>()).Text);
        Assert.Equal(ReasoningVisibility.Summarized, Assert.Single(response.Content.OfType<ReasoningBlock>()).Visibility);
    }

    [Fact]
    public async System.Threading.Tasks.Task Api_profile_request_is_native_responses_with_store_false_and_api_key()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("hola")));
        var provider = CreateProvider(handler, auth: AuthConfig.ApiKey("openai"), secrets: new FixedSecrets("sk-test-value"));
        var request = Request(instructions: "You are OmniCore.", reasoning: new ReasoningRequest("high", null),
            tools: [new ToolDefinition("filesystem.read", "Read a file", "{\"type\":\"object\"}")]);

        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = handler.Requests.Single();
        Assert.Equal("https://api.example.test/v1/responses", sent.Url);
        Assert.Equal("Bearer sk-test-value", sent.Headers["Authorization"]);
        Assert.StartsWith("omnicore/", sent.Headers["User-Agent"]);
        Assert.False(sent.Headers.ContainsKey("ChatGPT-Account-Id"));
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("You are OmniCore.", root.GetProperty("instructions").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", root.GetProperty("tools")[0].GetProperty("name").GetString()!);
        Assert.NotEqual("filesystem.read", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("messages", out _));
        Assert.Equal("input_text", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_history_maps_to_function_call_and_function_call_output_items()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("ok")));
        var call = new ToolCallBlock(ToolCallId.New(), "call_9", "filesystem.read", "{\"path\":\"a.cs\"}");
        var messages = new ModelMessage[]
        {
            new(MessageRole.User, [new TextBlock("lee a.cs")]),
            new(MessageRole.Assistant, [new TextBlock("Voy"), call]),
            new(MessageRole.Tool, [new ToolResultBlock(call.Id, [new TextBlock("contenido")], false)]),
        };

        await Collect(CreateProvider(handler).StreamAsync(Request(messages: messages), TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["message", "message", "function_call", "function_call_output"],
            input.Select(i => i.GetProperty("type").GetString()!).ToArray());
        Assert.Equal("output_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("call_9", input[2].GetProperty("call_id").GetString());
        Assert.Equal("{\"path\":\"a.cs\"}", input[2].GetProperty("arguments").GetString());
        Assert.Equal("call_9", input[3].GetProperty("call_id").GetString());
        Assert.Equal("contenido", input[3].GetProperty("output").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Provider_tool_alias_roundtrips_to_canonical_name_without_collisions()
    {
        var handler = new QueueHandler(http =>
        {
            using var body = JsonDocument.Parse(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var names = body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
            Assert.All(names, name => Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", name));
            return Sse(ToolStream.Replace("filesystem.read", names[0], StringComparison.Ordinal));
        });
        var events = await Collect(CreateProvider(handler).StreamAsync(Request(tools: [
            new ToolDefinition("filesystem.read", "Read", "{}"),
            new ToolDefinition("filesystem_read", "Other", "{}"),
            new ToolDefinition("omni_reserved", "Reserved", "{}")]), TestContext.Current.CancellationToken));
        Assert.Equal("filesystem.read", Assert.Single(Assert.IsType<ResponseCompleted>(events[^1]).Response.Content.OfType<ToolCallBlock>()).ToolName);
        Assert.Contains(events, e => e is BlockCompleted { Block: ToolCallBlock { ToolName: "filesystem.read" } });
    }

    [Fact]
    public async System.Threading.Tasks.Task Encrypted_reasoning_is_opaque_and_replayed_only_to_the_same_model()
    {
        var first = await Collect(CreateProvider(new QueueHandler(_ => Sse(ToolStream)))
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        var state = Assert.IsType<ResponseCompleted>(first[^1]).Response.State;
        Assert.NotNull(state);
        Assert.Contains("ENC-XYZ", state!.PayloadJson);
        var messages = new ModelMessage[]
        {
            new(MessageRole.User, [new TextBlock("hola")]),
            new(MessageRole.Assistant, [new TextBlock("Leo a.cs")]),
        };

        var same = new QueueHandler(_ => Sse(MinimalStream("ok")));
        await Collect(CreateProvider(same).StreamAsync(Request(messages: messages, continuation: state), TestContext.Current.CancellationToken));
        using (var body = JsonDocument.Parse(same.Requests.Single().Body))
        {
            var input = body.RootElement.GetProperty("input");
            Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
            Assert.Equal("ENC-XYZ", input[1].GetProperty("encrypted_content").GetString());
            Assert.Equal("message", input[2].GetProperty("type").GetString());
        }

        var other = new QueueHandler(_ => Sse(MinimalStream("ok")));
        await Collect(CreateProvider(other).StreamAsync(Request(model: "gpt-other", messages: messages, continuation: state),
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain("ENC-XYZ", other.Requests.Single().Body);
    }

    [Fact]
    public async System.Threading.Tasks.Task Codex_profile_uses_subscription_bearer_account_header_and_honest_originator()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("hola")));
        var subscription = new FakeSubscription(new SubscriptionCredential("oauth-access-1", "acct-42"));
        var provider = CreateProvider(handler, baseUrl: "https://chatgpt.example.test/backend-api",
            options: new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex }, subscription: subscription);

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var sent = handler.Requests.Single();
        Assert.Equal("https://chatgpt.example.test/backend-api/codex/responses", sent.Url);
        Assert.Equal("Bearer oauth-access-1", sent.Headers["Authorization"]);
        Assert.Equal("acct-42", sent.Headers["ChatGPT-Account-Id"]);
        Assert.Equal("omnicore", sent.Headers["originator"]);
        Assert.DoesNotContain("codex", sent.Headers["User-Agent"], StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async System.Threading.Tasks.Task Codex_401_refreshes_once_then_retries_and_a_second_401_is_authentication_failed()
    {
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"message\":\"expired\"}}") },
            _ => Sse(MinimalStream("tras refresh")));
        var subscription = new FakeSubscription(new SubscriptionCredential("old", "acct"), new SubscriptionCredential("new", "acct"));
        var provider = CreateProvider(handler, options: new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex }, subscription: subscription);

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(1, subscription.Refreshes);
        Assert.Equal("Bearer new", handler.Requests[1].Headers["Authorization"]);
        Assert.Equal("tras refresh", Assert.Single(Assert.IsType<ResponseCompleted>(events[^1]).Response.Content.OfType<TextBlock>()).Text);

        var always401 = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") },
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(CreateProvider(always401,
            options: new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex },
            subscription: new FakeSubscription(new SubscriptionCredential("a", "b"), new SubscriptionCredential("c", "b")))
            .StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal("AuthenticationFailed", ex.Kind);
    }

    [Theory]
    [InlineData("max_output_tokens", StopReason.MaxOutputTokens)]
    [InlineData("content_filter", StopReason.ContentFilter)]
    public async System.Threading.Tasks.Task Incomplete_responses_map_their_reason(string reason, StopReason expected)
    {
        var stream = MinimalStream("x").Replace("\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"",
            "\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"" + reason + "\"}");
        var events = await Collect(CreateProvider(new QueueHandler(_ => Sse(stream))).StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.IsType<ResponseCompleted>(events[^1]).Response.StopReason);
    }

    [Fact]
    public async System.Threading.Tasks.Task Failed_response_event_is_a_typed_error()
    {
        var failing = new QueueHandler(_ => Sse("""
event: response.failed
data: {"type":"response.failed","response":{"status":"failed","error":{"code":"rate_limit_exceeded","message":"slow down"}}}

"""));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(CreateProvider(failing)
            .StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal("RateLimited", ex.Kind);
    }

    [Fact]
    public async System.Threading.Tasks.Task Configurable_max_output_tokens_is_sent_only_when_set()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("a")), _ => Sse(MinimalStream("b")));
        await Collect(CreateProvider(handler).StreamAsync(Request(), TestContext.Current.CancellationToken));
        await Collect(CreateProvider(handler, options: new OpenAIResponsesOptions { MaxOutputTokens = 777 })
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        using var plain = JsonDocument.Parse(handler.Requests[0].Body);
        using var capped = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(plain.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.Equal(777, capped.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    [Fact]
    public void Codex_profile_without_subscription_source_and_foreign_family_are_rejected()
    {
        var descriptor = Descriptor("https://x.test", AuthConfig.None());
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesProvider(descriptor, new FixedSecrets(""),
            () => new HttpClient(), new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex }));
        var chat = new ProviderDescriptor("c", ProviderFamily.OpenAiChatCompatible, "https://x.test", AuthConfig.None(), false, false, true);
        Assert.Throws<ArgumentException>(() => new OpenAIResponsesProvider(chat, new FixedSecrets(""), () => new HttpClient()));
    }

    private static string MinimalStream(string text) => """
event: response.output_item.added
data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","output_index":0,"delta":"TEXT"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"TEXT"}]}}

event: response.completed
data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

""".Replace("TEXT", text);

    private static ProviderDescriptor Descriptor(string baseUrl, AuthConfig auth) =>
        new("openai", ProviderFamily.OpenAIResponses, baseUrl, auth, false, false, true);

    private static OpenAIResponsesProvider CreateProvider(QueueHandler handler, string baseUrl = "https://api.example.test/v1",
        AuthConfig? auth = null, ISecretProvider? secrets = null, OpenAIResponsesOptions? options = null,
        ISubscriptionCredentialSource? subscription = null) =>
        new(Descriptor(baseUrl, auth ?? AuthConfig.None()), secrets ?? new FixedSecrets(""),
            () => new HttpClient(handler, disposeHandler: false),
            options ?? new OpenAIResponsesOptions { Resilience = new OpenAiProviderOptions { DelayAsync = static (_, _) => ValueTask.CompletedTask } },
            subscription);

    private static ModelRequest Request(string model = "gpt-test", IReadOnlyList<ModelMessage>? messages = null,
        string? instructions = null, IReadOnlyList<ToolDefinition>? tools = null, ReasoningRequest? reasoning = null,
        ProviderState? continuation = null) =>
        new(new ModelSelection(new ModelIdValue(model), 4096, ToolMode.Direct, null),
            messages ?? [new ModelMessage(MessageRole.User, [new TextBlock("hola")])], instructions, tools ?? [],
            ToolChoice.Auto(), null, reasoning, null, continuation);

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class FixedSecrets(string value) : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of(value);
    }

    private sealed class FakeSubscription(SubscriptionCredential current, SubscriptionCredential? refreshed = null) : ISubscriptionCredentialSource
    {
        public int Refreshes { get; private set; }
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(current);
        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            return ValueTask.FromResult(refreshed ?? current);
        }
    }

    private sealed record SentRequest(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public List<SentRequest> Requests { get; } = [];
        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var next = _responses.TryDequeue(out var response) ? response : throw new InvalidOperationException("No scripted response remaining");
            return next(request);
        }
    }
}
