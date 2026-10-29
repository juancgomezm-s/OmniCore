namespace OmniCore.Tests;

using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Xunit;

public sealed class OpenRouterImageGenerationProviderTests
{
    [Fact]
    public async System.Threading.Tasks.Task Images_profile_preserves_still_webp_for_the_complete_Host_decoder()
    {
        var bytes = Convert.FromBase64String(WebpImageValidationTests.Lossless);
        var handler = new CaptureHandler(_ => JsonResponse("{\"data\":[{\"b64_json\":\""
            + Convert.ToBase64String(bytes) + "\",\"media_type\":\"image/webp\"}]}"));
        var completed = await Consume(Provider(handler), Request(), TestContext.Current.CancellationToken);
        var image = Assert.IsType<ImageBlock>(Assert.Single(completed.Content));
        Assert.Equal("image/webp", image.MediaType);
        Assert.Equal(bytes, image.Bytes.ToArray());
        var validated = OmniCore.Host.GeneratedImageValidator.Decode(image.MediaType, image.Bytes.ToArray());
        Assert.Equal(2, validated.Width);
        Assert.Equal(3, validated.Height);
        Assert.Equal(TokenUsageFields.None, completed.ReportedUsageFields);
        Assert.Equal(0.25m, completed.AdditionalCostUpperBoundUsd);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_posts_dedicated_bounded_request_and_returns_bytes_without_fabricated_usage()
    {
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=");
        var handler = new CaptureHandler(_ => JsonResponse("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(image) + "\",\"media_type\":\"image/png\"}]}"));
        var provider = Provider(handler);
        var response = await Consume(provider, Request(), TestContext.Current.CancellationToken);

        Assert.Equal("https://openrouter.test/api/v1/images", handler.Uri?.ToString());
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        Assert.Equal("model-for-test", handler.Body?.GetProperty("model").GetString());
        Assert.False(handler.Body?.TryGetProperty("response_format", out _));
        Assert.Equal(1, handler.Body?.GetProperty("n").GetInt32());
        Assert.Equal(1, handler.Body?.GetProperty("n").GetInt32());
        Assert.Contains("draw a red circle", handler.Body?.GetProperty("prompt").GetString());
        var completed = Assert.IsType<ModelResponse>(response);
        var returned = Assert.IsType<ImageBlock>(Assert.Single(completed.Content));
        Assert.Equal(image, returned.Bytes.ToArray());
        Assert.Equal(TokenUsageFields.None, completed.ReportedUsageFields);
        Assert.Equal(0, completed.Usage.Input);
        Assert.Equal(0, completed.Usage.Output);
        Assert.Equal(0.25m, completed.AdditionalCostUpperBoundUsd);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_preserves_reported_tokens_and_usd_cost_when_provider_supplies_them()
    {
        using var sends = GenerationRequestAttemptScope.Enter();
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=");
        var handler = new CaptureHandler(_ =>
        {
            Assert.Equal(1L, sends.ObservedSends);
            return JsonResponse("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(image)
                + "\",\"media_type\":\"image/png\"}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":23,\"total_tokens\":40,\"cost\":0.0142}}");
        });
        var completed = await Consume(Provider(handler), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, sends.ObservedSends);
        Assert.Equal(new TokenUsage(17, 23, 0, 0, 0), completed.Usage);
        Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, completed.ReportedUsageFields);
        Assert.Equal(0.0142m, completed.ReportedProviderCostUsd);
        Assert.Equal(0.25m, completed.AdditionalCostUpperBoundUsd);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_rejects_without_effective_image_cap_before_http()
    {
        using var sends = GenerationRequestAttemptScope.Enter();
        var handler = new CaptureHandler(_ => JsonResponse("{}"));
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await Consume(Provider(handler), Request(imageEnabled: false), TestContext.Current.CancellationToken));
        Assert.Equal("ImageGenerationNotAuthorized", error.Kind);
        Assert.Null(handler.Uri);
        Assert.Equal(0, handler.Count);
        Assert.Equal(0L, sends.ObservedSends);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async System.Threading.Tasks.Task Images_profile_counts_http_failures_without_retrying_image_generation(HttpStatusCode status)
    {
        using var sends = GenerationRequestAttemptScope.Enter();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(status));
        var provider = Provider(handler);

        var error = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Consume(provider, Request(), TestContext.Current.CancellationToken));

        Assert.Equal("ProviderRequestFailed", error.Kind);
        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, sends.ObservedSends);
        Assert.Equal(1L, provider.MaximumGenerationRequestAttempts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_honors_before_send_admission_without_dispatching_or_counting_a_rejected_request()
    {
        var admissionCalls = 0;
        using var sends = GenerationRequestAttemptScope.Enter(attempt =>
        {
            Assert.Equal(1L, attempt);
            admissionCalls++;
            throw new InvalidOperationException("fixture_admission_rejected");
        });
        var handler = new CaptureHandler(_ => JsonResponse("{}"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Consume(Provider(handler), Request(), TestContext.Current.CancellationToken));

        Assert.Equal("fixture_admission_rejected", error.Message);
        Assert.Equal(1, admissionCalls);
        Assert.Equal(0, handler.Count);
        Assert.Equal(0L, sends.ObservedSends);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_does_not_count_a_request_cancelled_before_dispatch()
    {
        using var sends = GenerationRequestAttemptScope.Enter();
        var handler = new CaptureHandler(_ => JsonResponse("{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Consume(Provider(handler), Request(), new CancellationToken(canceled: true)));

        Assert.Equal(0, handler.Count);
        Assert.Equal(0L, sends.ObservedSends);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_counts_a_request_cancelled_inside_the_http_handler()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var sends = GenerationRequestAttemptScope.Enter();
        var handler = new CaptureHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Consume(Provider(handler), Request(), cancellation.Token));

        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, sends.ObservedSends);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_counts_a_send_even_when_the_response_cannot_be_decoded()
    {
        using var sends = GenerationRequestAttemptScope.Enter();
        var handler = new CaptureHandler(_ => JsonResponse("{}"));

        var error = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Consume(Provider(handler), Request(), TestContext.Current.CancellationToken));

        Assert.Equal("ImageResponseInvalid", error.Kind);
        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, sends.ObservedSends);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_projects_prior_text_and_instructions_for_dedicated_endpoint()
    {
        var handler = new CaptureHandler(_ => JsonResponse("{\"data\":[{\"b64_json\":\"aQ==\",\"media_type\":\"image/png\"}]}"));
        var provider = Provider(handler);
        var original = Request();
        var request = new ModelRequest(original.Model,
            [new ModelMessage(MessageRole.Assistant, [new TextBlock("earlier plan")]),
             new ModelMessage(MessageRole.User, [new TextBlock("draw a red circle")])],
            "Keep the answer safe", [new ToolDefinition("tool", "description", "{}")], ToolChoice.None(),
            null, null, null, null);
        _ = await Consume(provider, request, TestContext.Current.CancellationToken);
        var prompt = handler.Body!.Value.GetProperty("prompt").GetString()!;
        Assert.Contains("Keep the answer safe", prompt);
        Assert.Contains("earlier plan", prompt);
        Assert.Contains("draw a red circle", prompt);
        Assert.False(handler.Body.Value.TryGetProperty("tools", out _));
    }

    private static OpenRouterImageGenerationProvider Provider(CaptureHandler handler) => new(
        new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible, "https://openrouter.test/api/v1",
            AuthConfig.ApiKey("fixture-ref"), false, false, false) { Profile = OpenRouterImageGenerationProvider.ProfileName },
        new FixtureSecrets(), () => new HttpClient(handler, disposeHandler: false));

    private static ModelRequest Request(bool imageEnabled = true) => new(
        new ModelSelection(new ModelIdValue("model-for-test"), 4096, ToolMode.Direct, null,
            outputModalities: imageEnabled ? ["text", "image"] : ["text"],
            imageGenerationMaxCostUsd: imageEnabled ? 0.25m : null),
        [new ModelMessage(MessageRole.User, [new TextBlock("draw a red circle")])], null, [], ToolChoice.None(), null, null, null, null);

    private static async System.Threading.Tasks.Task<ModelResponse> Consume(IModelProvider provider, ModelRequest request,
        CancellationToken cancellationToken)
    {
        ModelResponse? response = null;
        await foreach (var item in provider.StreamAsync(request, cancellationToken))
            if (item is ResponseCompleted completed) response = completed.Response;
        return response ?? throw new InvalidOperationException("response_missing");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FixtureSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("fixture-key");
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public System.Text.Json.JsonElement? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            using var json = await System.Text.Json.JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            Body = json.RootElement.Clone();
            return response(request);
        }
    }

}
