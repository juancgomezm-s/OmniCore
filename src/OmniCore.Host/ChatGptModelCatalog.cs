namespace OmniCore.Host;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OmniCore.Models;

/// <summary>Account-specific discovery, never an inference or a paid API-key fallback.</summary>
public sealed record AvailableChatGptModel(string Id, string DisplayName, int? ContextWindow = null,
    int? MaxOutputTokens = null, bool IsDefault = false);

public sealed class ChatGptModelCatalog
{
    // Catalog protocol compatibility, not our application identity. The service filters
    // entries by this version; User-Agent and originator remain honestly OmniCore.
    internal const string CodexCatalogCompatibilityVersion = "0.159.2";
    private readonly ISubscriptionCredentialSource _credentials;
    private readonly Func<HttpClient> _http;
    private readonly Action<string>? _diagnostics;
    public ChatGptModelCatalog(ISubscriptionCredentialSource credentials, Func<HttpClient>? http = null, Action<string>? diagnostics = null)
    {
        _credentials = credentials;
        _diagnostics = diagnostics;
        _http = http ?? (() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) });
    }

    public async Task<IReadOnlyList<AvailableChatGptModel>> ListAsync(CancellationToken ct)
    {
        var credential = await _credentials.GetAsync(ct).ConfigureAwait(false);
        using var http = _http();
        using var response = await Send(http, "https://api.openai.com/v1/models", credential, ct).ConfigureAwait(false);
        IReadOnlyList<AvailableChatGptModel>? publicCatalog = null;
        if (response.IsSuccessStatusCode) publicCatalog = await Read(response, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || publicCatalog?.Count == 0)
        {
            // Existing OmniCore accounts use the ADR-0011 Codex OAuth client, not the
            // newer token-sharing client. Keep that account on its existing provider.
            using var legacy = await Send(http, "https://chatgpt.com/backend-api/codex/models?client_version=" + CodexCatalogCompatibilityVersion, credential, ct).ConfigureAwait(false);
            return await Read(legacy, ct).ConfigureAwait(false);
        }
        return publicCatalog ?? await Read(response, ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, string url, SubscriptionCredential credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        request.Headers.TryAddWithoutValidation("originator", "omnicore");
        request.Headers.TryAddWithoutValidation("User-Agent", OpenAIResponsesProvider.UserAgent("0.1.0"));
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AvailableChatGptModel>> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Model catalog HTTP " + (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (_diagnostics is not null)
        {
            _diagnostics("Catalog HTTP " + (int)response.StatusCode);
            if (json.RootElement.TryGetProperty("models", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    _diagnostics("Model " + String(item, "slug") + " visibility=" + (String(item, "visibility") ?? "(absent)"));
        }
        return Parse(json.RootElement);
    }

    internal static IReadOnlyList<AvailableChatGptModel> Parse(JsonElement root)
    {
        // A malformed body is not authoritative evidence that a model was withdrawn.
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Invalid account model catalog.");
        var result = new List<AvailableChatGptModel>();
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid model catalog item.");
            var id = String(item, "slug");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
                throw new InvalidOperationException("Invalid catalog model ID.");
            var visibility = String(item, "visibility") ?? throw new InvalidOperationException("Missing catalog visibility.");
            if (visibility != "list") continue;
            if (result.Any(m => m.Id == id)) continue;
            result.Add(new(id, new string((String(item, "display_name") ?? id).Where(c => !char.IsControl(c)).ToArray()), Integer(item, "context_window"),
                Integer(item, "max_output_tokens"), item.TryGetProperty("is_default", out var d) && d.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    private static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Integer(JsonElement item, string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : null;
}
