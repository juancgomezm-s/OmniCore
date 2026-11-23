namespace OmniCore.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class OpenAiChatCompatibleMediaSerializationTests
{
    [Fact]
    public void Final_pass_can_use_dialect_advertised_by_the_generation_response_without_metadata_probe()
    {
        using var handler = new CaptureHandler(server: "ik_llama.cpp");
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:9999/v1", AuthConfig.None(), false, false, true) { BillingMode = BillingMode.Local };
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false));
        var model = new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null);
        provider.Complete(new ModelRequest(model, [], null, [], ToolChoice.None(), null, null, null, null),
            TestContext.Current.CancellationToken);
        Assert.True(provider.UsesLlamaCppDialect);
        provider.Complete(new ModelRequest(model, [], null, [], ToolChoice.None(), null, null, null, null)
            { FinalAnswerOnly = true }, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(0, handler.MetadataQueries);
        Assert.Equal(2, handler.GenerationRequests);
    }

    [Theory]
    [InlineData("partial-text")]
    [InlineData("partial-tools")]
    [InlineData("usage-only")]
    [InlineData("unknown-stop")]
    [InlineData("missing-tools")]
    [InlineData("other-choice-stop")]
    public async System.Threading.Tasks.Task Incomplete_or_unknown_completion_never_emits_a_completed_response(string scenario)
    {
        var payload = scenario switch
        {
            "partial-text" => "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
            "partial-tools" => "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call\",\"function\":{\"name\":\"fixture\",\"arguments\":\"{\"}}]}}]}\n\ndata: [DONE]\n\n",
            "usage-only" => "data: {\"choices\":[],\"usage\":{\"completion_tokens\":3}}\n\ndata: [DONE]\n\n",
            "unknown-stop" => "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"backend_error\"}]}\n\n",
            "missing-tools" => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n",
            _ => "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}},{\"index\":1,\"delta\":{\"content\":\"other\"},\"finish_reason\":\"stop\"}]}\n\n",
        };
        using var handler = new CaptureHandler(payload);
        var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true), new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null),
            [], null, [], ToolChoice.None(), null, null, null, null);
        var events = new List<ModelStreamEvent>();
        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken)) events.Add(item);
        });
        Assert.Empty(events.OfType<ResponseCompleted>());
        Assert.Empty(events.OfType<BlockCompleted>().Select(item => item.Block).OfType<ToolCallBlock>());
        Assert.Equal(1, handler.GenerationRequests);
    }

    [Fact]
    public void Stream_uses_only_choice_zero_and_preserves_trailing_usage()
    {
        const string payload = "data: {\"choices\":[{\"index\":1,\"delta\":{\"content\":\"wrong\"},\"finish_reason\":\"length\"},{\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n";
        using var handler = new CaptureHandler(payload);
        var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true), new NoSecrets(), () => new HttpClient(handler, false));
        var response = provider.Complete(new ModelRequest(new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null),
            [], null, [], ToolChoice.None(), null, null, null, null), TestContext.Current.CancellationToken);
        Assert.Equal("answer", Assert.Single(response.Content.OfType<TextBlock>()).Text);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        Assert.Equal(2, response.Usage.Output);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("auto-choice")]
    [InlineData("unsupported")]
    [InlineData("no-off-level")]
    public void Final_answer_hint_does_not_override_tools_or_declared_capabilities(string scenario)
    {
        using var handler = new CaptureHandler();
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:9999/v1", AuthConfig.None(), false, false, true)
            { BillingMode = BillingMode.Local, Profile = "llamaCpp" };
        var capability = scenario == "unsupported" ? new ReasoningCapability(false)
            : scenario == "no-off-level" ? new ReasoningCapability(true, ["high"]) : ReasoningCapability.Unknown;
        var route = new ModelRoute("fixture", descriptor.BaseUrl, ProviderFamily.OpenAiChatCompatible,
            "llamaCpp", "fixture-model", reasoningCapability: capability);
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null, route: route),
            [new ModelMessage(MessageRole.User, [new TextBlock("Question")])], null,
            scenario == "tools" ? [new ToolDefinition("fixture", "fixture", "{\"type\":\"object\"}")] : [],
            scenario == "auto-choice" ? ToolChoice.Auto() : ToolChoice.None(), null, null, null, null)
            { FinalAnswerOnly = true };
        provider.Complete(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("llama.cpp", true, BillingMode.Local, true)]
    [InlineData("ik_llama.cpp", true, BillingMode.Local, true)]
    [InlineData("vllm", true, BillingMode.Local, false)]
    [InlineData("ik_llama.cpp", false, BillingMode.Local, false)]
    [InlineData("ik_llama.cpp", true, BillingMode.MeteredCurrency, false)]
    public void Final_answer_pass_disables_only_known_local_automatic_thinking(
        string server, bool finalAnswer, BillingMode billing, bool disabled)
    {
        using var handler = new CaptureHandler(server: server);
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:9999/v1", AuthConfig.None(), false, false, true)
        { BillingMode = billing, LocalHost = new LocalHostConfig(LocalHostMode.Attach, Declared: true) };
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("Question")])], null, [], ToolChoice.None(), null, null, null, null)
            { FinalAnswerOnly = finalAnswer };
        provider.Complete(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(disabled, body.RootElement.TryGetProperty("chat_template_kwargs", out var kwargs));
        if (disabled)
        {
            Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
            Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
        }
        Assert.Null(request.Reasoning);
        Assert.Null(request.Model.Reasoning);
        Assert.False(body.RootElement.TryGetProperty("reasoning_budget_tokens", out _));
    }

    [Theory]
    [InlineData("llama.cpp", true)]
    [InlineData("ik_llama.cpp", true)]
    [InlineData("vllm", false)]
    public void Automatic_reasoning_preserves_server_defaults_after_local_dialect_detection(string server, bool llama)
    {
        using var handler = new CaptureHandler(server: server);
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:9999/v1", AuthConfig.None(), false, false, true)
        { BillingMode = BillingMode.Local, LocalHost = new LocalHostConfig(LocalHostMode.Attach, Declared: true) };
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false),
            new OpenAiProviderOptions { LimitAutomaticLocalReasoning = true });
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null,
            maxOutputTokens: 120), [new ModelMessage(MessageRole.User, [new TextBlock("Question")])],
            null, [], ToolChoice.None(), null, null, null, null);

        provider.Complete(request, TestContext.Current.CancellationToken);
        provider.Complete(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.MetadataQueries);
        Assert.Equal(llama, provider.UsesLlamaCppDialect);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("reasoning_budget_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("thinking_budget_tokens", out _));
    }

    [Theory]
    [InlineData(BillingMode.Local, false, "null", TokenUsageFields.None, 0, 0)]
    [InlineData(BillingMode.Local, false, "{}", TokenUsageFields.None, 0, 0)]
    [InlineData(BillingMode.Local, false, "{\"prompt_tokens\":14}", TokenUsageFields.Input, 14, 0)]
    [InlineData(BillingMode.Local, false, "{\"prompt_tokens\":14,\"completion_tokens\":7}", TokenUsageFields.Input | TokenUsageFields.Output, 14, 7)]
    [InlineData(BillingMode.Local, false, "{\"prompt_tokens\":0,\"completion_tokens\":0}", TokenUsageFields.Input | TokenUsageFields.Output, 0, 0)]
    [InlineData(BillingMode.Unknown, true, "{\"prompt_tokens\":14,\"completion_tokens\":7}", TokenUsageFields.Input | TokenUsageFields.Output, 14, 7)]
    [InlineData(BillingMode.Unknown, false, "null", TokenUsageFields.None, 0, 0)]
    public async System.Threading.Tasks.Task Local_stream_requests_usage_and_preserves_reported_fields(
        BillingMode billing, bool localHost, string usageJson, TokenUsageFields fields, long input, long output)
    {
        const string answer = "data: {\"choices\":[{\"delta\":{\"content\":\"visible\"},\"finish_reason\":\"stop\"}]}\n\n";
        using var handler = new CaptureHandler(answer + "data: {\"choices\":[],\"usage\":" + usageJson + "}");
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true)
        { BillingMode = billing, LocalHost = localHost ? new LocalHostConfig(LocalHostMode.Attach) : null };
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("Question")])], null, [], ToolChoice.None(), null, null, null, null);
        var events = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken)) events.Add(item);
        using var body = JsonDocument.Parse(handler.Body!);
        if (billing == BillingMode.Local || localHost)
            Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        else
            Assert.False(body.RootElement.TryGetProperty("stream_options", out _));

        var result = Assert.Single(events.OfType<ResponseCompleted>()).Response;
        Assert.Equal("visible", Assert.Single(result.Content.OfType<TextBlock>()).Text);
        Assert.Equal(fields, result.ReportedUsageFields);
        Assert.Equal(new TokenUsage(input, output, 0, 0, 0), result.Usage);
    }

    [Theory]
    [InlineData(true, "reasoning_content")]
    [InlineData(true, "reasoning")]
    [InlineData(false, "reasoning_content")]
    [InlineData(false, "reasoning")]
    public async System.Threading.Tasks.Task Chat_stream_preserves_terminal_answer_at_eof_and_rejects_incomplete_streams(
        bool complete, string reasoningField)
    {
        string ReasoningEvent(string fragment) =>
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"" + reasoningField + "\":\"" + fragment + "\"}}]}\n\n";
        var reasoning = ReasoningEvent("private ") + ReasoningEvent("draft");
        const string final = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"visible answer\"},\"finish_reason\":\"stop\"}]}";
        using var handler = new CaptureHandler(complete ? reasoning + final : reasoning);
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true);
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(), () => new HttpClient(handler, false));
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("Question")])], null, [], ToolChoice.None(), null, null, null, null);
        var events = new List<ModelStreamEvent>();
        async System.Threading.Tasks.Task Read()
        {
            await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken)) events.Add(item);
        }
        if (!complete)
        {
            await Assert.ThrowsAsync<ModelProviderException>(Read);
            Assert.Empty(events.OfType<ResponseCompleted>());
            return;
        }
        await Read();
        var result = Assert.Single(events.OfType<ResponseCompleted>()).Response;
        Assert.Equal("visible answer", Assert.Single(result.Content.OfType<TextBlock>()).Text);
        Assert.Equal("private draft", Assert.Single(result.Content.OfType<ReasoningBlock>()).VisibleText);
        Assert.Equal("private draft", string.Concat(events.OfType<ReasoningDelta>().Select(delta => delta.Text)));
        Assert.Equal("visible answer", Assert.Single(events.OfType<TextDelta>()).Text);
        using var opaque = JsonDocument.Parse(result.State!.PayloadJson);
        Assert.Equal("private draft", opaque.RootElement.GetProperty(reasoningField).GetString());
    }

    [Theory]
    [InlineData("llamaCpp", true)]
    [InlineData(null, false)]
    [InlineData("other", false)]
    public async System.Threading.Tasks.Task Reasoning_off_uses_only_the_explicit_llama_cpp_dialect(string? profile, bool supported)
    {
        var handler = new CaptureHandler();
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true) { Profile = profile };
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(),
            () => new HttpClient(handler, disposeHandler: false));
        const string literal = "Prueba literal: café  ";
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096,
            ToolMode.Direct, new ReasoningRequest("off", null)),
            [new ModelMessage(MessageRole.User, [new TextBlock(literal)])], null, [], ToolChoice.Auto(), null, null, null, null);
        if (!supported)
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken)) { }
            });
            Assert.Null(handler.Body);
            return;
        }
        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken)) { }
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(literal, body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Multiple_tool_results_are_serialized_before_one_native_image_message()
    {
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var first = ToolCallId.New();
        var second = ToolCallId.New();
        var handler = new CaptureHandler();
        var descriptor = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "https://fixture.invalid/v1", AuthConfig.None(), false, false, true);
        var provider = new OpenAiChatCompatibleProvider(descriptor, new NoSecrets(),
            () => new HttpClient(handler, disposeHandler: false));
        var request = new ModelRequest(
            new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
            [
                new ModelMessage(MessageRole.Assistant,
                [
                    new ToolCallBlock(first, "provider-1", "media.read", "{\"path\":\"a.png\"}"),
                    new ToolCallBlock(second, "provider-2", "media.read", "{\"path\":\"b.png\"}"),
                ]),
                new ModelMessage(MessageRole.Tool, [new ToolResultBlock(first,
                    [new TextBlock("first"), new ImageBlock("image/png", imageBytes)], false)]),
                new ModelMessage(MessageRole.Tool, [new ToolResultBlock(second,
                    [new TextBlock("second"), new ImageBlock("image/png", imageBytes)], false)]),
            ], null, [], ToolChoice.Auto(), null, null, null, null);

        await foreach (var _ in provider.StreamAsync(request, TestContext.Current.CancellationToken)) { }

        using var body = JsonDocument.Parse(handler.Body!);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "assistant", "tool", "tool", "user" },
            messages.Select(message => message.GetProperty("role").GetString()).ToArray());
        Assert.Equal("provider-1", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("provider-2", messages[2].GetProperty("tool_call_id").GetString());
        var imageParts = messages[3].GetProperty("content").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "image_url").ToArray();
        Assert.Equal(2, imageParts.Length);
        Assert.All(imageParts, part => Assert.StartsWith("data:image/png;base64,",
            part.GetProperty("image_url").GetProperty("url").GetString()));
    }

    [Theory]
    [InlineData("filesystem.read", "read_file")]
    [InlineData("filesystem.search", "grep_files")]
    [InlineData("filesystem.patch", "apply_patch")]
    [InlineData("shell.exec", "exec_command")]
    [InlineData("process.send_input", "write_stdin")]
    [InlineData("user.ask", "request_user_input")]
    public async System.Threading.Tasks.Task Dotted_tool_names_map_in_choice_and_history_and_restore_stream_results(
        string canonical, string expectedAlias)
    {
        var call = new ToolCallBlock(ToolCallId.New(), "old-call", canonical, "{}");
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096,
                ToolMode.Direct, null),
            [new ModelMessage(MessageRole.Assistant, [call]),
                new ModelMessage(MessageRole.Tool, [new ToolResultBlock(call.Id, [new TextBlock("ok")], false)]),
                new ModelMessage(MessageRole.User, [new TextBlock("again")])],
            "Use `" + canonical + "`.", [new ToolDefinition(canonical, "Use `" + canonical + "`.", "{\"type\":\"object\"}")], ToolChoice.Exact(canonical),
            null, null, null, null);
        var names = ToolNameWireMap.ForRequest(request, 64);
        var alias = names.ToWire(canonical);
        Assert.Equal(expectedAlias, alias);
        Assert.DoesNotContain('.', alias);
        var handler = new CaptureHandler("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"new-call\",\"function\":{\"name\":\"" + alias + "\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n");
        var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("fixture",
            ProviderFamily.OpenAiChatCompatible, "https://fixture.invalid/v1", AuthConfig.None(), false, false, true),
            new NoSecrets(), () => new HttpClient(handler, disposeHandler: false));

        var events = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken)) events.Add(item);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal(alias, root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Use `" + alias + "`.", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Use `" + alias + "`.", root.GetProperty("tools")[0].GetProperty("function").GetProperty("description").GetString());
        Assert.Equal(alias, root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        var historical = root.GetProperty("messages")[1].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("name").GetString();
        Assert.Equal(alias, historical);
        var responseCall = Assert.Single(Assert.IsType<ResponseCompleted>(events[^1]).Response.Content
            .OfType<ToolCallBlock>());
        Assert.Equal(canonical, responseCall.ToolName);
        Assert.Equal(canonical, Assert.Single(events.OfType<BlockCompleted>()
            .Select(item => item.Block).OfType<ToolCallBlock>()).ToolName);
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int MetadataQueries { get; private set; }
        public int GenerationRequests { get; private set; }
        private readonly string _response;
        private readonly string? _server;
        public CaptureHandler(string? response = null, string? server = null)
        {
            _response = response ?? "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            _server = server;
        }
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
                MetadataQueries++;
                var metadata = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
                if (_server is not null) metadata.Headers.Server.ParseAdd(_server);
                return metadata;
            }
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            GenerationRequests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(_response,
                    Encoding.UTF8, "text/event-stream"),
            };
            if (_server is not null) response.Headers.Server.ParseAdd(_server);
            return response;
        }
    }
}
