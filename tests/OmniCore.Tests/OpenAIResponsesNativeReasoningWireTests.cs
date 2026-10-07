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
/// Offline adapter wire regressions. The fixture route explicitly declares each tested value;
/// that is not a claim that every real model supports every value. No credentials or network are used.
/// </summary>
public sealed class OpenAIResponsesNativeReasoningWireTests
{
    private const string Endpoint = "https://responses.fixture.invalid/v1";

    [Theory]
    [InlineData("Api", "none")]
    [InlineData("Api", "minimal")]
    [InlineData("Api", "low")]
    [InlineData("Api", "medium")]
    [InlineData("Api", "high")]
    [InlineData("Api", "xhigh")]
    [InlineData("Api", "max")]
    [InlineData("Codex", "none")]
    [InlineData("Codex", "minimal")]
    [InlineData("Codex", "low")]
    [InlineData("Codex", "medium")]
    [InlineData("Codex", "high")]
    [InlineData("Codex", "xhigh")]
    [InlineData("Codex", "max")]
    public async Task Declared_fixture_effort_is_serialized_exactly_for_each_native_profile(
        string profileName, string effort)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var (provider, request) = Fixture(handler, profile, selectionReasoning: new(effort, null),
            requestReasoning: new(effort, null), declaredLevels: [effort], subscription: subscription);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);
        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var sent = Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(profile == ResponsesProfile.Codex
            ? Endpoint + "/codex/responses" : Endpoint + "/responses", sent.Url);
        Assert.Equal(profile == ResponsesProfile.Codex ? 1 : 0, subscription?.GetCalls ?? 0);
        Assert.Equal(0, subscription?.RefreshCalls ?? 0);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Codex")]
    public async Task ModelSelection_reasoning_is_used_when_request_level_fallback_is_null(string profileName)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var (provider, request) = Fixture(handler, profile,
            selectionReasoning: new ReasoningRequest("xhigh", null), requestReasoning: null,
            declaredLevels: ["xhigh"], subscription: subscription);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Model.Reasoning);
        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("xhigh", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(profile == ResponsesProfile.Codex ? 1 : 0, subscription?.GetCalls ?? 0);
        Assert.Equal(0, subscription?.RefreshCalls ?? 0);
    }

    [Theory]
    [InlineData("Api", "provider-preview-level")]
    [InlineData("Codex", "provider-preview-level")]
    [InlineData("Api", "High")]
    [InlineData("Codex", "High")]
    public async Task Unmapped_declared_label_is_not_aliased_or_sent(string profileName, string label)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var (provider, request) = Fixture(handler, profile,
            selectionReasoning: new ReasoningRequest(label, null),
            requestReasoning: new ReasoningRequest(label, null), declaredLevels: [label], subscription: subscription);

        // The fixture route explicitly accepts this opaque label. It does not claim OpenAI supports it.
        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);

        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, subscription?.GetCalls ?? 0);
        Assert.Equal(0, subscription?.RefreshCalls ?? 0);
    }

    [Theory]
    [InlineData("Api", false)]
    [InlineData("Codex", false)]
    [InlineData("Api", true)]
    [InlineData("Codex", true)]
    public async Task Direct_adapter_caller_cannot_bypass_declared_route_contradiction(string profileName, bool enumerated)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var (provider, request) = Fixture(handler, profile, new("high", null), new("high", null),
            ["high"], subscription, enumerated ? new ReasoningCapability(true, ["low"]) : new ReasoningCapability(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, subscription?.GetCalls ?? 0);
        Assert.Equal(0, subscription?.RefreshCalls ?? 0);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Codex")]
    public async Task No_reasoning_selection_does_not_invent_an_effort_or_numeric_budget(string profileName)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var (provider, request) = Fixture(handler, profile, null, null, [], subscription);
        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("Api", 2048)]
    [InlineData("Codex", 2048)]
    [InlineData("Api", 0)]
    [InlineData("Codex", 0)]
    [InlineData("Api", -1)]
    [InlineData("Codex", -1)]
    public async Task Numeric_budget_not_represented_by_this_adapter_is_rejected_before_auth_or_http(
        string profileName, int budget)
    {
        var profile = Profile(profileName);
        var handler = new CaptureHandler();
        var subscription = profile == ResponsesProfile.Codex ? new FakeSubscription() : null;
        var reasoning = new ReasoningRequest("high", budget);
        var (provider, request) = Fixture(handler, profile,
            selectionReasoning: reasoning, requestReasoning: reasoning, declaredLevels: ["high"], subscription: subscription);

        request.Model.Route!.ReasoningCapability.ValidateRequest(request.Reasoning);

        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(
            provider.StreamAsync(request, TestContext.Current.CancellationToken)));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, subscription?.GetCalls ?? 0);
        Assert.Equal(0, subscription?.RefreshCalls ?? 0);
    }

    private static ResponsesProfile Profile(string name) => name switch
    {
        "Api" => ResponsesProfile.Api,
        "Codex" => ResponsesProfile.Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static (OpenAIResponsesProvider Provider, ModelRequest Request) Fixture(
        CaptureHandler handler, ResponsesProfile profile, ReasoningRequest? selectionReasoning,
        ReasoningRequest? requestReasoning, IReadOnlyList<string> declaredLevels,
        FakeSubscription? subscription, ReasoningCapability? capability = null)
    {
        var descriptor = new ProviderDescriptor("responses-fixture", ProviderFamily.OpenAIResponses,
            Endpoint, AuthConfig.None(), false, false, true);
        var route = new ModelRoute(descriptor.Id, Endpoint, ProviderFamily.OpenAIResponses,
            profile == ResponsesProfile.Codex ? "codex" : null, "fixture-model",
            reasoningCapability: capability ?? new ReasoningCapability(true, declaredLevels));
        var selection = new ModelSelection(new ModelIdValue("fixture-model"), 4096,
            ToolMode.Direct, selectionReasoning, route: route);
        // No explicit output limit: the Codex profile has not qualified that parameter.
        var request = new ModelRequest(selection,
            [new ModelMessage(MessageRole.User, [new TextBlock("synthetic reasoning wire test")])],
            null, [], ToolChoice.None(), null, requestReasoning, null, null);
        var options = new OpenAIResponsesOptions
        {
            Profile = profile,
            Resilience = new OpenAiProviderOptions
            {
                DelayAsync = static (_, _) => ValueTask.CompletedTask,
            },
        };
        var provider = new OpenAIResponsesProvider(descriptor, new EmptySecrets(),
            () => new HttpClient(handler, disposeHandler: false), options, subscription);
        return (provider, request);
    }

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static string Completion => """
        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","output_index":0,"delta":"ok"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"ok"}]}}

        event: response.completed
        data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":2,"output_tokens":1}}}

        """;

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Auth.None fixture must not request a secret.");
    }

    private sealed class FakeSubscription : ISubscriptionCredentialSource
    {
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
        {
            GetCalls++;
            return ValueTask.FromResult(new SubscriptionCredential("fixture-token", "fixture-account"));
        }

        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return ValueTask.FromResult(new SubscriptionCredential("fixture-refreshed-token", "fixture-account"));
        }
    }

    private sealed record SentRequest(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class CaptureHandler : HttpMessageHandler
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
                Content = new StringContent(Completion, Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
