namespace OmniCore.Models;

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Explicit OpenRouter /images dialect. It accepts prompt text only and returns verified-later PNG/JPEG bytes.</summary>
public sealed class OpenRouterImageGenerationProvider : IModelProvider, IModelRequestAttemptBound
{
    public const string ProfileName = "openrouter-images";
    private const int MaximumResponseBytes = 90 * 1024 * 1024;
    private readonly ProviderDescriptor _descriptor;
    private readonly ISecretProvider _secrets;
    private readonly Func<HttpClient> _httpFactory;

    public OpenRouterImageGenerationProvider(ProviderDescriptor descriptor, ISecretProvider secrets,
        Func<HttpClient>? httpFactory = null)
    {
        if (descriptor.Family != ProviderFamily.OpenAiChatCompatible
            || !StringComparer.Ordinal.Equals(descriptor.Profile, ProfileName))
            throw new ArgumentException("Provider must explicitly select the OpenRouter images profile.", nameof(descriptor));
        _descriptor = descriptor;
        _secrets = secrets;
        _httpFactory = httpFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
    }

    public ProviderCapabilities Capabilities { get; } = new(false, false, false);
    public long? MaximumGenerationRequestAttempts => 1;

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!request.Model.OutputModalities.Contains("image", StringComparer.Ordinal)
            || request.Model.ImageGenerationMaxCostUsd is not > 0m)
            throw new ModelProviderException("ImageGenerationNotAuthorized", "Image output has no effective modality and spend bound.");
        if (request.Continuation is not null)
            throw new ModelProviderException("ImageGenerationUnsupportedRequest", "The /images dialect does not accept provider continuation.");
        var prompt = BuildPrompt(request.Instructions, request.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ModelProviderException("ImageGenerationUnsupportedRequest", "The /images dialect requires user prompt text.");

        var secretRef = _descriptor.Auth.SecretRef;
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new ModelProviderException("AuthenticationRequired", "OpenRouter image generation requires a configured credential.");
        var secret = _secrets.GetSecret(secretRef, cancellationToken);
        using var http = _httpFactory();
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(_descriptor.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Value());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(JsonSerializer.Serialize(new OpenRouterImageRequest
        {
            Model = request.Model.Model.ToString(), Prompt = prompt,
        }, OpenRouterImageJsonContext.Default.OpenRouterImageRequest), Encoding.UTF8, "application/json");

        cancellationToken.ThrowIfCancellationRequested();
        GenerationRequestAttemptScope.RecordGenerationSend();
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Keep provider response bodies and credentials out of logs/UI.
            throw new ModelProviderException("ProviderRequestFailed", $"OpenRouter image request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bounded.Length + read > MaximumResponseBytes)
                throw new ModelProviderException("ImageResponseTooLarge", "OpenRouter image response exceeds the byte limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        using var json = JsonDocument.Parse(bounded.ToArray());
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() != 1 || !data[0].TryGetProperty("b64_json", out var encoded)
            || encoded.ValueKind != JsonValueKind.String || !data[0].TryGetProperty("media_type", out var media)
            || media.ValueKind != JsonValueKind.String)
            throw new ModelProviderException("ImageResponseInvalid", "OpenRouter image response does not match the configured b64_json contract.");
        var encodedImage = encoded.GetString()!;
        var maxBase64Chars = checked(((64 * 1024 * 1024 + 2) / 3) * 4);
        if (encodedImage.Length > maxBase64Chars)
            throw new ModelProviderException("ImageResponseTooLarge", "OpenRouter base64 image exceeds the byte limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedImage); }
        catch (FormatException ex) { throw new ModelProviderException("ImageResponseInvalid", "OpenRouter returned invalid base64 image data.", innerException: ex); }
        if (bytes.Length == 0 || bytes.Length > 64 * 1024 * 1024)
            throw new ModelProviderException("ImageResponseTooLarge", "OpenRouter image bytes exceed the supported limit.");
        var mediaType = media.GetString() switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/webp" => "image/webp",
            _ => throw new ModelProviderException("ImageMediaTypeUnsupported", "OpenRouter returned an unsupported image media type."),
        };
        var image = new ImageBlock(mediaType, bytes);
        yield return new ResponseStarted(0);
        yield return new BlockStarted(0, "image");
        yield return new BlockCompleted(0, image);
        var requestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
            ? requestIds.FirstOrDefault() ?? "" : "";
        var (usage, reportedFields, reportedCost) = ReadUsage(json.RootElement);
        yield return new ResponseCompleted(new ModelResponse([image], StopReason.EndTurn,
            usage, null, new ProviderMetadata(requestId, request.Model.Model.ToString(), null),
            reportedFields, request.Model.ImageGenerationMaxCostUsd, reportedCost));
    }

    private static string BuildPrompt(string? instructions, IReadOnlyList<ModelMessage> messages)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            text.Append("System instructions:\n").Append(instructions.Trim());
        }
        foreach (var message in messages)
        {
            text.Append(text.Length > 0 ? "\n\n" : "");
            text.Append(message.Role switch
            {
                MessageRole.System => "System context:\n",
                MessageRole.User => "User request:\n",
                MessageRole.Assistant => "Prior assistant context:\n",
                MessageRole.Tool => "Prior tool result context:\n",
                _ => throw new ModelProviderException("ImageGenerationUnsupportedRequest", "The /images dialect received an unsupported message role."),
            });
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock value:
                        text.Append(value.Text);
                        break;
                    case ImageBlock image:
                        text.Append("[Prior image omitted from /images prompt: ")
                            .Append(image.MediaType).Append(", ")
                            .Append(image.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .Append(" bytes; this endpoint accepts prompt text only.]");
                        break;
                    case DocumentBlock document:
                        text.Append("[Document ").Append(document.FileName).Append("]\n")
                            .Append(document.ExtractedText);
                        break;
                    case ToolResultBlock result:
                        if (result.Content.Any(block => block is not TextBlock))
                            throw new ModelProviderException("ImageGenerationUnsupportedRequest", "The /images dialect cannot represent a non-text tool result.");
                        foreach (var resultText in result.Content.OfType<TextBlock>())
                            text.Append(resultText.Text);
                        break;
                    default:
                        throw new ModelProviderException("ImageGenerationUnsupportedRequest", "The /images dialect cannot represent a prior non-text block.");
                }
            }
        }
        return text.ToString();
    }

    private static Uri Endpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http"))
            throw new ModelProviderException("ProviderConfigurationInvalid", "OpenRouter image endpoint URL is invalid.");
        var builder = new UriBuilder(baseUri);
        builder.Path = builder.Path.TrimEnd('/') + "/images";
        builder.Query = string.Empty;
        return builder.Uri;
    }

    private static (TokenUsage Usage, TokenUsageFields Fields, decimal? ReportedCostUsd) ReadUsage(JsonElement root)
    {
        var input = 0L;
        var output = 0L;
        var fields = TokenUsageFields.None;
        decimal? cost = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out input) && input >= 0)
                fields |= TokenUsageFields.Input;
            else input = 0;
            if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt64(out output) && output >= 0)
                fields |= TokenUsageFields.Output;
            else output = 0;
            if (usage.TryGetProperty("cost", out var reportedCost)
                && reportedCost.ValueKind == JsonValueKind.Number && reportedCost.TryGetDecimal(out var parsed)
                && parsed >= 0m && parsed <= 1_000_000m)
                cost = parsed;
            else if (usage.TryGetProperty("cost", out reportedCost) && reportedCost.ValueKind != JsonValueKind.Null)
                throw new ModelProviderException("ImageResponseInvalid", "OpenRouter image usage cost is invalid.");
        }
        return (new TokenUsage(input, output, 0, 0, 0), fields, cost);
    }
}

internal sealed class OpenRouterImageRequest
{
    public string Model { get; init; } = "";
    public string Prompt { get; init; } = "";
    public int N { get; init; } = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenRouterImageRequest))]
internal partial class OpenRouterImageJsonContext : JsonSerializerContext;
