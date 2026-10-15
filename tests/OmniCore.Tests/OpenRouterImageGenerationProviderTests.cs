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
    public async System.Threading.Tasks.Task Images_profile_posts_dedicated_bounded_request_and_returns_bytes_without_fabricated_usage()
    {
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=");
        var handler = new CaptureHandler(_ => JsonResponse("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(image) + "\",\"media_type\":\"image/png\"}]}"));
        var provider = Provider(handler);
        var response = await Consume(provider, Request());

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
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=");
        var handler = new CaptureHandler(_ => JsonResponse("{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(image)
            + "\",\"media_type\":\"image/png\"}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":23,\"total_tokens\":40,\"cost\":0.0142}}"));
        var completed = await Consume(Provider(handler), Request());

        Assert.Equal(new TokenUsage(17, 23, 0, 0, 0), completed.Usage);
        Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, completed.ReportedUsageFields);
        Assert.Equal(0.0142m, completed.ReportedProviderCostUsd);
        Assert.Equal(0.25m, completed.AdditionalCostUpperBoundUsd);
    }

    [Fact]
    public async System.Threading.Tasks.Task Images_profile_rejects_without_effective_image_cap_before_http()
    {
        var handler = new CaptureHandler(_ => JsonResponse("{}"));
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () => await Consume(Provider(handler), Request(imageEnabled: false)));
        Assert.Equal("ImageGenerationNotAuthorized", error.Kind);
        Assert.Null(handler.Uri);
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
        _ = await Consume(provider, request);
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

    private static async System.Threading.Tasks.Task<ModelResponse> Consume(IModelProvider provider, ModelRequest request)
    {
        ModelResponse? response = null;
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
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
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public System.Text.Json.JsonElement? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            using var json = await System.Text.Json.JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            Body = json.RootElement.Clone();
            return response(request);
        }
    }

}
