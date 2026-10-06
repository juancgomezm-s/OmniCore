namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Adaptador nativo de la Messages API (ADR-0005 §1), probado con SSE guionado sin red.</summary>
public sealed class AnthropicMessagesProviderTests
{
    [Fact]
    public async System.Threading.Tasks.Task Host_native_limit_reaches_the_http_request_including_thinking()
    {
        var descriptor = new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages,
            "https://api.example.test", AuthConfig.None(), false, false, true);
        var model = new ModelDefinition("claude-test", descriptor.Id, 4096, 4096, 2048);
        var limit = OmniCore.Host.ModelRoutingHost.OutputTokenLimit(model, descriptor);
        Assert.Equal(2048L, limit);
        var handler = new QueueHandler(_ => Sse(MinimalStream("bounded")));
        var events = await Collect(CreateProvider(handler).StreamAsync(
            Request(reasoning: new ReasoningRequest("budget", 1024), maxOutputTokens: limit),
            TestContext.Current.CancellationToken));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(2048L, body.RootElement.GetProperty("max_tokens").GetInt64());
        Assert.Equal(1024, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.IsType<ResponseCompleted>(events[^1]);
    }

    private const string TextAndToolStream = """
event: message_start
data: {"type":"message_start","message":{"id":"msg_1","model":"claude-test-resolved","usage":{"input_tokens":12,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"let me look"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG-ABC"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Voy a leer"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: content_block_start
data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"filesystem.read","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.cs\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":2}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}

event: message_stop
data: {"type":"message_stop"}

""";

    [Fact]
    public async System.Threading.Tasks.Task Streams_thinking_text_and_tool_use_with_usage_and_stop_reason()
    {
        var handler = new QueueHandler(_ => Sse(TextAndToolStream));
        var events = await Collect(CreateProvider(handler).StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.IsType<ResponseStarted>(events[0]);
        Assert.Contains(events, e => e is ReasoningDelta { Text: "let me look" });
        Assert.Contains(events, e => e is TextDelta { Text: "Voy a leer" });
        Assert.Contains(events, e => e is ToolArgumentsDelta { PartialJson: "{\"path\":" });
        var response = Assert.IsType<ResponseCompleted>(events[^1]).Response;
        Assert.Equal(StopReason.ToolUse, response.StopReason);
        // Anthropic's input_tokens excludes cache; the neutral Input counter includes it.
        Assert.Equal(new TokenUsage(19, 42, 5, 2, 0), response.Usage);
        Assert.Equal("claude-test-resolved", response.Metadata.Model);
        var call = Assert.Single(response.Content.OfType<ToolCallBlock>());
        Assert.Equal("toolu_1", call.ProviderCallId);
        Assert.Equal("filesystem.read", call.ToolName);
        Assert.Equal("{\"path\":\"a.cs\"}", call.ArgumentsJson);
        Assert.Equal("let me look", Assert.Single(response.Content.OfType<ReasoningBlock>()).VisibleText);
        Assert.Equal("Voy a leer", Assert.Single(response.Content.OfType<TextBlock>()).Text);
    }

    [Fact]
    public async System.Threading.Tasks.Task Request_uses_native_headers_endpoint_system_tools_and_never_openai_shape()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("hola")));
        var provider = CreateProvider(handler, AuthConfig.ApiKey("anthropic"), new FixedSecrets("sk-ant-test-value"));
        var request = Request(instructions: "You are OmniCore.",
            tools: [new ToolDefinition("filesystem.read", "Read a file", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}")]);

        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = handler.Requests.Single();
        Assert.Equal("https://api.example.test/v1/messages", sent.Url);
        Assert.Equal("sk-ant-test-value", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Equal("model-a", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(8192, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("You are OmniCore.", root.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal("filesystem.read", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("functions", out _));
        Assert.All(root.GetProperty("messages").EnumerateArray(), m => Assert.NotEqual("system", m.GetProperty("role").GetString()));
    }

    [Fact]
    public async System.Threading.Tasks.Task Tool_results_travel_as_user_tool_result_blocks_and_same_role_messages_merge()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("ok")));
        var call = new ToolCallBlock(ToolCallId.New(), "toolu_9", "filesystem.read", "{\"path\":\"a.cs\"}");
        var messages = new ModelMessage[]
        {
            new(MessageRole.User, [new TextBlock("lee a.cs")]),
            new(MessageRole.Assistant, [new TextBlock("Voy")]),
            new(MessageRole.Assistant, [call]),
            new(MessageRole.Tool, [new ToolResultBlock(call.Id, [new TextBlock("contenido")], false)]),
            new(MessageRole.User, [new TextBlock("sigue")]),
        };

        await Collect(CreateProvider(handler).StreamAsync(Request(messages: messages), TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        var sent = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user", "assistant", "user"], sent.Select(m => m.GetProperty("role").GetString()!).ToArray());
        var assistant = sent[1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("text", assistant[0].GetProperty("type").GetString());
        Assert.Equal("tool_use", assistant[1].GetProperty("type").GetString());
        Assert.Equal("toolu_9", assistant[1].GetProperty("id").GetString());
        Assert.Equal("a.cs", assistant[1].GetProperty("input").GetProperty("path").GetString());
        var user = sent[2].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("tool_result", user[0].GetProperty("type").GetString());
        Assert.Equal("toolu_9", user[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("contenido", user[0].GetProperty("content").GetString());
        Assert.Equal("sigue", user[1].GetProperty("text").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Signed_thinking_is_kept_opaque_and_replayed_only_to_the_same_model()
    {
        var first = await Collect(CreateProvider(new QueueHandler(_ => Sse(TextAndToolStream)))
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        var state = Assert.IsType<ResponseCompleted>(first[^1]).Response.State;
        Assert.NotNull(state);
        Assert.Contains("SIG-ABC", state!.PayloadJson);

        var messages = new ModelMessage[]
        {
            new(MessageRole.User, [new TextBlock("hola")]),
            new(MessageRole.Assistant, [new TextBlock("Voy a leer")]),
        };
        var sameModel = new QueueHandler(_ => Sse(MinimalStream("ok")));
        await Collect(CreateProvider(sameModel).StreamAsync(Request(messages: messages, continuation: state),
            TestContext.Current.CancellationToken));
        using (var body = JsonDocument.Parse(sameModel.Requests.Single().Body))
        {
            var assistant = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
            Assert.Equal("thinking", assistant.GetProperty("type").GetString());
            Assert.Equal("let me look", assistant.GetProperty("thinking").GetString());
            Assert.Equal("SIG-ABC", assistant.GetProperty("signature").GetString());
        }

        var otherModel = new QueueHandler(_ => Sse(MinimalStream("ok")));
        await Collect(CreateProvider(otherModel).StreamAsync(Request(model: "model-b", messages: messages, continuation: state),
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SIG-ABC", otherModel.Requests.Single().Body);
    }

    [Fact]
    public async System.Threading.Tasks.Task Reasoning_budget_enables_thinking_and_raises_max_tokens_above_it()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("ok")));
        var request = Request(reasoning: new ReasoningRequest("budget", 2000));

        await Collect(CreateProvider(handler).StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2000, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal(2000 + 4096, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async System.Threading.Tasks.Task Configurable_output_limits_change_max_tokens()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("ok")), _ => Sse(MinimalStream("ok")));
        var options = new AnthropicProviderOptions { DefaultMaxOutputTokens = 1234, OutputTokensAboveReasoningBudget = 10 };
        var provider = CreateProvider(handler, options: options);

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        await Collect(provider.StreamAsync(Request(reasoning: new ReasoningRequest("budget", 1024)), TestContext.Current.CancellationToken));

        using var plain = JsonDocument.Parse(handler.Requests[0].Body);
        using var budgeted = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(1234, plain.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(1034, budgeted.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async System.Threading.Tasks.Task Cache_hints_mark_the_system_prompt_as_an_ephemeral_breakpoint()
    {
        var handler = new QueueHandler(_ => Sse(MinimalStream("ok")));
        await Collect(CreateProvider(handler).StreamAsync(Request(instructions: "sys", cache: new CacheHints(1, "auto")),
            TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("ephemeral", body.RootElement.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Overloaded_529_is_retried_and_a_midstream_error_event_is_typed()
    {
        var handler = new QueueHandler(
            _ => new HttpResponseMessage((HttpStatusCode)529) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}") },
            _ => Sse(MinimalStream("tras reintento")));
        var options = new AnthropicProviderOptions { Resilience = new OpenAiProviderOptions { DelayAsync = static (_, _) => ValueTask.CompletedTask } };
        var events = await Collect(CreateProvider(handler, options: options).StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("tras reintento", Assert.Single(Assert.IsType<ResponseCompleted>(events[^1]).Response.Content.OfType<TextBlock>()).Text);

        var failing = new QueueHandler(_ => Sse("""
event: message_start
data: {"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}

event: error
data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

"""));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(CreateProvider(failing)
            .StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal("ProviderUnavailable", ex.Kind);
    }

    [Fact]
    public async System.Threading.Tasks.Task Authentication_error_is_typed_and_not_retried()
    {
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}"),
        });
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(CreateProvider(handler)
            .StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal("AuthenticationFailed", ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Descriptor_of_another_family_is_rejected()
    {
        var chat = new ProviderDescriptor("x", ProviderFamily.OpenAiChatCompatible, "https://x.test/v1", AuthConfig.None(), false, false, true);
        Assert.Throws<ArgumentException>(() => new AnthropicMessagesProvider(chat, new FixedSecrets("")));
    }

    [Theory]
    [InlineData("end_turn", StopReason.EndTurn)]
    [InlineData("tool_use", StopReason.ToolUse)]
    [InlineData("max_tokens", StopReason.MaxOutputTokens)]
    [InlineData("stop_sequence", StopReason.StopSequence)]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("model_context_window_exceeded", StopReason.ContextOverflow)]
    public async System.Threading.Tasks.Task Stop_reasons_map_to_the_neutral_contract(string native, StopReason expected)
    {
        var stream = MinimalStream("x").Replace("\"stop_reason\":\"end_turn\"", "\"stop_reason\":\"" + native + "\"");
        var events = await Collect(CreateProvider(new QueueHandler(_ => Sse(stream)))
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.IsType<ResponseCompleted>(events[^1]).Response.StopReason);
    }

    private static string MinimalStream(string text) => """
event: message_start
data: {"type":"message_start","message":{"usage":{"input_tokens":3,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"TEXT"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

event: message_stop
data: {"type":"message_stop"}

""".Replace("TEXT", text);

    private static AnthropicMessagesProvider CreateProvider(QueueHandler handler, AuthConfig? auth = null,
        ISecretProvider? secrets = null, AnthropicProviderOptions? options = null) =>
        new(new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages, "https://api.example.test",
            auth ?? AuthConfig.None(), false, false, true), secrets ?? new FixedSecrets(""),
            () => new HttpClient(handler, disposeHandler: false), options);

    private static ModelRequest Request(string model = "model-a", IReadOnlyList<ModelMessage>? messages = null,
        string? instructions = null, IReadOnlyList<ToolDefinition>? tools = null, ReasoningRequest? reasoning = null,
        CacheHints? cache = null, ProviderState? continuation = null, long? maxOutputTokens = null) =>
        new(new ModelSelection(new ModelIdValue(model), 4096, ToolMode.Direct, null, maxOutputTokens: maxOutputTokens),
            messages ?? [new ModelMessage(MessageRole.User, [new TextBlock("hola")])], instructions, tools ?? [],
            ToolChoice.Auto(), null, reasoning, cache, continuation);

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

    private sealed record SentRequest(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public List<SentRequest> Requests { get; } = [];
        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var next = _responses.TryDequeue(out var response) ? response : throw new InvalidOperationException("No scripted response remaining");
            return next(request);
        }
    }
}
