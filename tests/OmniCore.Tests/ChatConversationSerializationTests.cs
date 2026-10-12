using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class ChatConversationSerializationTests
{
    [Fact]
    public async Task Assistant_text_and_parallel_calls_remain_one_turn_with_matching_results()
    {
        var first = new ToolCallBlock(ToolCallId.New(), "server-first", "filesystem.read", "{\"path\":\"uno.txt\"}");
        var second = new ToolCallBlock(ToolCallId.New(), "server-second", "filesystem.read", "{\"path\":\"dos.txt\"}");
        using var body = await Serialize([
            new(MessageRole.User, [new TextBlock("Lee ambos.")]),
            new(MessageRole.Assistant, [new TextBlock("Voy a leer "), first, new TextBlock("ambos."), second]),
            new(MessageRole.Tool, [new ToolResultBlock(first.Id, [new TextBlock("uno")], false),
                new ToolResultBlock(second.Id, [new TextBlock("dos")], false)])]);

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Voy a leer ambos.", messages[1].GetProperty("content").GetString());
        var calls = messages[1].GetProperty("tool_calls");
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal("server-first", calls[0].GetProperty("id").GetString());
        Assert.Equal("server-second", calls[1].GetProperty("id").GetString());
        Assert.Equal(first.ArgumentsJson, calls[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(second.ArgumentsJson, calls[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("server-first", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("server-second", messages[3].GetProperty("tool_call_id").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_result_preserves_every_text_block_unicode_and_opaque_identifiers(bool isError)
    {
        const string receipt = "RESULT-0123456789abcdef0123456789abcdef";
        var call = new ToolCallBlock(ToolCallId.New(), null, "fixture", "{}");
        using var body = await Serialize([
            new(MessageRole.Assistant, [call]),
            new(MessageRole.Tool, [new ToolResultBlock(call.Id,
                [new TextBlock("Español: áéíóú, ñ, ¿qué?\n"), new TextBlock(receipt)], isError)])]);

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
        Assert.Equal(call.Id.ToString(), messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("Español: áéíóú, ñ, ¿qué?\n" + receipt, messages[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(MessageRole.User)]
    [InlineData(MessageRole.System)]
    [InlineData(MessageRole.Assistant)]
    public async Task Text_blocks_do_not_create_extra_conversation_turns(MessageRole role)
    {
        using var body = await Serialize([new(role, [new TextBlock("Primero\n"), new TextBlock("segundo")])]);
        var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal(role.ToString().ToLowerInvariant(), message.GetProperty("role").GetString());
        Assert.Equal("Primero\nsegundo", message.GetProperty("content").GetString());
        Assert.False(message.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task Stream_reassembles_receipt_without_changing_characters()
    {
        const string receipt = "RESULT-0123456789abcdef0123456789abcdef";
        var sse = string.Concat(receipt.Chunk(3).Select(part => "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = new string(part) } } }
        }) + "\n\n")) + "data: [DONE]\n\n";
        var handler = new CaptureHandler(sse);
        ModelResponse? response = null;
        var deltas = new StringBuilder();
        await foreach (var item in Provider(handler).StreamAsync(Request([]), TestContext.Current.CancellationToken))
        {
            if (item is TextDelta delta) deltas.Append(delta.Text);
            if (item is ResponseCompleted completed) response = completed.Response;
        }
        Assert.Equal(receipt, deltas.ToString());
        Assert.Equal(receipt, Assert.Single(Assert.IsType<ModelResponse>(response).Content.OfType<TextBlock>()).Text);
    }

    private static async Task<JsonDocument> Serialize(IReadOnlyList<ModelMessage> messages)
    {
        var handler = new CaptureHandler("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        await foreach (var _ in Provider(handler).StreamAsync(Request(messages), TestContext.Current.CancellationToken)) { }
        return JsonDocument.Parse(Assert.IsType<string>(handler.Body));
    }

    private static ModelRequest Request(IReadOnlyList<ModelMessage> messages) => new(
        new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null),
        messages, null, [], ToolChoice.None(), null, null, null, null);

    private static OpenAiChatCompatibleProvider Provider(CaptureHandler handler) => new(
        new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible, "https://fixture.invalid/v1",
            AuthConfig.None(), false, false, true), new NoSecrets(), () => new HttpClient(handler, false));

    private sealed class CaptureHandler(string sse) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
