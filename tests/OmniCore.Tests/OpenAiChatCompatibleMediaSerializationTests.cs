namespace OmniCore.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class OpenAiChatCompatibleMediaSerializationTests
{
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
        private readonly string _response;
        public CaptureHandler(string? response = null) => _response = response ??
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response,
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
