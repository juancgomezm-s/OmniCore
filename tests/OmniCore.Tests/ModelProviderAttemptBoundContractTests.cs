using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class ModelProviderAttemptBoundContractTests
{
    [Fact]
    public void Three_adapters_expose_default_generation_attempt_bounds_and_retry_zero_bounds()
    {
        Assert.Equal(3L, Chat(options: new OpenAiProviderOptions()).MaximumGenerationRequestAttempts);
        Assert.Equal(3L, Anthropic(options: new AnthropicProviderOptions()).MaximumGenerationRequestAttempts);
        Assert.Equal(3L, Responses(options: new OpenAIResponsesOptions()).MaximumGenerationRequestAttempts);
        Assert.Equal(6L, Responses(options: CodexOptions()).MaximumGenerationRequestAttempts);

        Assert.Equal(1L, Chat(options: new OpenAiProviderOptions { MaxRetries = 0 }).MaximumGenerationRequestAttempts);
        Assert.Equal(1L, Anthropic(options: new AnthropicProviderOptions
        { Resilience = new OpenAiProviderOptions { MaxRetries = 0 } }).MaximumGenerationRequestAttempts);
        Assert.Equal(1L, Responses(options: new OpenAIResponsesOptions
        { Resilience = new OpenAiProviderOptions { MaxRetries = 0 } }).MaximumGenerationRequestAttempts);
        Assert.Equal(2L, Responses(options: CodexOptions(retries: 0)).MaximumGenerationRequestAttempts);
    }

    [Fact]
    public void Retry_limit_int_max_value_is_widened_before_adding_and_multiplying()
    {
        Assert.Equal(2_147_483_648L, Chat(options: new OpenAiProviderOptions
        { MaxRetries = int.MaxValue }).MaximumGenerationRequestAttempts);
        Assert.Equal(2_147_483_648L, Anthropic(options: new AnthropicProviderOptions
        { Resilience = new OpenAiProviderOptions { MaxRetries = int.MaxValue } }).MaximumGenerationRequestAttempts);
        Assert.Equal(2_147_483_648L, Responses(options: new OpenAIResponsesOptions
        { Resilience = new OpenAiProviderOptions { MaxRetries = int.MaxValue } }).MaximumGenerationRequestAttempts);
        Assert.Equal(4_294_967_296L, Responses(options: CodexOptions(int.MaxValue)).MaximumGenerationRequestAttempts);
    }

    [Fact]
    public void Catalog_bound_uses_the_first_acquired_resilience_options_for_the_provider_id()
    {
        var catalog = new ProviderResilienceCatalog();
        var first = Chat("shared-provider", new OpenAiProviderOptions
        { CircuitCatalog = catalog, MaxRetries = 4 });
        var second = Anthropic("shared-provider", new AnthropicProviderOptions
        { Resilience = new OpenAiProviderOptions { CircuitCatalog = catalog, MaxRetries = 0 } });

        Assert.Equal(5L, first.MaximumGenerationRequestAttempts);
        Assert.Equal(5L, second.MaximumGenerationRequestAttempts);
    }

    [Fact]
    public async Task Codex_retries_then_refreshes_once_then_retries_again_with_separate_auth_counts()
    {
        var handler = new QueueHandler(
            _ => ServiceUnavailable(),
            _ => ServiceUnavailable(),
            _ => Unauthorized(),
            _ => ServiceUnavailable(),
            _ => ServiceUnavailable(),
            _ => Success());
        var subscription = new CountingSubscription();
        var options = CodexOptions(retries: 2);
        var descriptor = new ProviderDescriptor("responses-codex-fixture", ProviderFamily.OpenAIResponses,
            "https://responses.fixture.test/backend-api", AuthConfig.None(), false, false, true)
        { Profile = "codex" };
        var provider = new OpenAIResponsesProvider(descriptor, new EmptySecrets(),
            () => new HttpClient(handler, disposeHandler: false), options, subscription);

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(6L, provider.MaximumGenerationRequestAttempts);
        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
            Assert.Equal("https://responses.fixture.test/backend-api/codex/responses", request.RequestUri!.ToString()));
        Assert.Equal(1, subscription.GetCalls);
        Assert.Equal(1, subscription.RefreshCalls);
        Assert.Contains(events, item => item is ResponseCompleted);
    }

    private static OpenAiChatCompatibleProvider Chat(string id = "chat-fixture", OpenAiProviderOptions? options = null) =>
        new(Descriptor(id, ProviderFamily.OpenAiChatCompatible), new EmptySecrets(), NewClient, options);

    private static AnthropicMessagesProvider Anthropic(string id = "anthropic-fixture",
        AnthropicProviderOptions? options = null) =>
        new(Descriptor(id, ProviderFamily.AnthropicMessages), new EmptySecrets(), NewClient, options);

    private static OpenAIResponsesProvider Responses(string id = "responses-fixture",
        OpenAIResponsesOptions? options = null) =>
        new(Descriptor(id, ProviderFamily.OpenAIResponses), new EmptySecrets(), NewClient, options,
            options?.Profile == ResponsesProfile.Codex ? new CountingSubscription() : null);

    private static ProviderDescriptor Descriptor(string id, ProviderFamily family) =>
        new(id, family, "https://provider.fixture.test/v1", AuthConfig.None(), false, false, true);

    private static HttpClient NewClient() => new(new NeverSendHandler());

    private static OpenAIResponsesOptions CodexOptions(int retries = 2) => new()
    {
        Profile = ResponsesProfile.Codex,
        Resilience = NoDelay(retries),
    };

    private static OpenAiProviderOptions NoDelay(int retries) => new()
    {
        MaxRetries = retries,
        DelayAsync = static (_, _) => ValueTask.CompletedTask,
        Jitter = static () => 0,
    };

    private static HttpResponseMessage ServiceUnavailable() =>
        new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("temporary fixture failure") };

    private static HttpResponseMessage Unauthorized() =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"message\":\"expired\"}}") };

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","output_index":0,"delta":"fixture-ok"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"fixture-ok"}]}}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

            """, Encoding.UTF8, "text/event-stream"),
    };

    private static ModelRequest Request() => new(
        new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
        [new ModelMessage(MessageRole.User, [new TextBlock("fixture prompt")])], null, [], ToolChoice.Auto(),
        null, null, null, null);

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(
        IAsyncEnumerable<ModelStreamEvent> stream, CancellationToken cancellationToken)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream.WithCancellation(cancellationToken)) events.Add(item);
        return events;
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture uses Auth.None.");
    }

    private sealed class CountingSubscription : ISubscriptionCredentialSource
    {
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult(new SubscriptionCredential("fixture-access", "fixture-account"));
        }

        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            return ValueTask.FromResult(new SubscriptionCredential("fixture-refreshed", "fixture-account"));
        }
    }

    private sealed class NeverSendHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Attempt-bound assertions must not send HTTP requests.");
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("Fixture received more requests than scripted.");
            return System.Threading.Tasks.Task.FromResult(response(request));
        }
    }
}
