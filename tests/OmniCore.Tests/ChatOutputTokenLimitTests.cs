using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Verifies the exact legacy Chat Completions request field sent by the adapter. The handler is
/// in-memory: this does not certify API/model compatibility or an actual provider-side budget.
/// </summary>
public sealed class ChatOutputTokenLimitTests
{
    [Theory]
    [InlineData(16L)]
    [InlineData(4096L)]
    [InlineData(long.MaxValue)]
    public async Task Selection_output_limit_is_sent_exactly_as_legacy_max_tokens(long maxOutputTokens)
    {
        var handler = new CapturingHandler(_ => Success());
        var provider = CreateProvider(handler);

        await Collect(provider.StreamAsync(Request(maxOutputTokens), TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(maxOutputTokens, body.RootElement.GetProperty("max_tokens").GetInt64());
    }

    [Fact]
    public async Task Missing_selection_output_limit_omits_max_tokens()
    {
        var handler = new CapturingHandler(_ => Success());
        var provider = CreateProvider(handler);

        await Collect(provider.StreamAsync(Request(null), TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Bad_request_for_output_limit_is_not_retried_without_a_limit()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"max_tokens exceeds model limit\"}}",
                Encoding.UTF8, "application/json"),
        });
        var delays = 0;
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 4,
            DelayAsync = (_, _) =>
            {
                delays++;
                return ValueTask.CompletedTask;
            },
            Jitter = () => 0,
        });

        var exception = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Collect(provider.StreamAsync(Request(4096), TestContext.Current.CancellationToken)));

        Assert.Equal("ProviderError", exception.Kind);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, delays);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(4096L, body.RootElement.GetProperty("max_tokens").GetInt64());
    }

    private static OpenAiChatCompatibleProvider CreateProvider(CapturingHandler handler,
        OpenAiProviderOptions? options = null) =>
        new(new ProviderDescriptor("chat-limit-fixture", ProviderFamily.OpenAiChatCompatible,
                "https://fixture.invalid/v1", AuthConfig.None(), false, false, true),
            new EmptySecrets(), () => new HttpClient(handler, disposeHandler: false), options);

    private static ModelRequest Request(long? maxOutputTokens)
    {
        var selection = new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null,
            maxOutputTokens: maxOutputTokens);
        var messages = new ModelMessage[]
        {
            new(MessageRole.User, [new TextBlock("Reply with one word.")]),
        };
        return new ModelRequest(selection, messages, null, [], ToolChoice.None(), null, null, null, null);
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private static async Task Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        await foreach (var _ in stream) { }
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Auth.None must not request a secret");
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }
}
