namespace OmniCore.Host;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;

/// <summary>Brave Search API adapter. Credentials are resolved only when a request is dispatched.</summary>
public sealed class BraveWebSearchProvider : IWebSearchProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<string?> _apiKey;
    private readonly bool _ownsClient;
    public string Id => "brave";
    public Uri Endpoint { get; }

    public BraveWebSearchProvider(Func<string?> apiKey, Uri? endpoint = null)
        : this(ProviderHttp.CreateNoRedirectClient(), apiKey, endpoint, ownsClient: true) { }

    internal BraveWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint = null)
        : this(client, apiKey, endpoint, ownsClient: false) { }

    private BraveWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _ownsClient = ownsClient;
        Endpoint = ProviderHttp.ValidateEndpoint(endpoint ?? new Uri("https://api.search.brave.com/res/v1/web/search"));
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ProviderHttp.ValidateRequest(request);
        var applied = new List<string> { "maxResults" };
        var unsupported = new List<string>();
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("q", request.Query), new("count", request.MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        foreach (var (key, value) in request.OptionalFilters)
        {
            var wire = key switch { "country" => "country", "search_lang" => "search_lang", "freshness" => "freshness", _ => null };
            if (wire is null) unsupported.Add(key);
            else { pairs.Add(new(wire, value)); applied.Add(key); }
        }
        if (request.RequiredDomains.Count != 0) unsupported.Add("requiredDomains");
        if (request.PreferPrimarySources) unsupported.Add("preferPrimarySources");
        var uri = ProviderHttp.WithQuery(Endpoint, pairs);
        return await ProviderHttp.SendAsync(_client, uri, Id, request.MaxResults,
            message => message.Headers.TryAddWithoutValidation("X-Subscription-Token", RequireKey()),
            Parse, applied, unsupported, cancellationToken).ConfigureAwait(false);
    }

    private string RequireKey() => ProviderHttp.RequireKey(_apiKey());

    private static IReadOnlyList<WebSearchCandidate> Parse(JsonElement root, string provider, int max)
    {
        var candidates = new List<WebSearchCandidate>();
        if (!root.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return candidates;
        foreach (var row in rows.EnumerateArray())
        {
            var url = ProviderHttp.String(row, "url");
            if (url is null || !ProviderHttp.IsHttpUrl(url)) continue;
            candidates.Add(new(url, ProviderHttp.String(row, "title") ?? "", ProviderHttp.String(row, "description") ?? "", provider));
            if (candidates.Count == max) break;
        }
        return candidates;
    }
}

/// <summary>Tavily Search API adapter.</summary>
public sealed class TavilyWebSearchProvider : IWebSearchProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<string?> _apiKey;
    private readonly bool _ownsClient;
    public string Id => "tavily";
    public Uri Endpoint { get; }

    public TavilyWebSearchProvider(Func<string?> apiKey, Uri? endpoint = null)
        : this(ProviderHttp.CreateNoRedirectClient(), apiKey, endpoint, ownsClient: true) { }

    internal TavilyWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint = null)
        : this(client, apiKey, endpoint, ownsClient: false) { }

    private TavilyWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _ownsClient = ownsClient;
        Endpoint = ProviderHttp.ValidateEndpoint(endpoint ?? new Uri("https://api.tavily.com/search"));
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ProviderHttp.ValidateRequest(request);
        var applied = new List<string> { "maxResults" };
        var unsupported = new List<string>();
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject(); writer.WriteString("query", request.Query); writer.WriteNumber("max_results", request.MaxResults);
            if (request.RequiredDomains.Count != 0)
            {
                writer.WritePropertyName("include_domains"); writer.WriteStartArray();
                foreach (var domain in request.RequiredDomains) writer.WriteStringValue(domain);
                writer.WriteEndArray(); writer.WriteString("include_domains_mode", "restrict"); applied.Add("requiredDomains");
            }
            foreach (var (key, value) in request.OptionalFilters)
            {
                if (key is "search_depth" && value is "basic" or "advanced") { writer.WriteString("search_depth", value); applied.Add(key); }
                else if (key is "topic" && value is "general" or "news") { writer.WriteString("topic", value); applied.Add(key); }
                else unsupported.Add(key);
            }
            writer.WriteBoolean("include_answer", false); writer.WriteBoolean("include_raw_content", false); writer.WriteEndObject();
        }
        if (request.PreferPrimarySources) unsupported.Add("preferPrimarySources");
        var payload = body.ToArray();
        return await ProviderHttp.SendAsync(_client, Endpoint, Id, request.MaxResults, message =>
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ProviderHttp.RequireKey(_apiKey()));
            message.Content = new ByteArrayContent(payload); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }, Parse, applied, unsupported, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<WebSearchCandidate> Parse(JsonElement root, string provider, int max)
    {
        var candidates = new List<WebSearchCandidate>();
        if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return candidates;
        foreach (var row in rows.EnumerateArray())
        {
            var url = ProviderHttp.String(row, "url");
            if (url is null || !ProviderHttp.IsHttpUrl(url)) continue;
            candidates.Add(new(url, ProviderHttp.String(row, "title") ?? "", ProviderHttp.String(row, "content") ?? "", provider,
                ProviderHttp.String(row, "published_date")));
            if (candidates.Count == max) break;
        }
        return candidates;
    }
}

/// <summary>Exa Search API adapter.</summary>
public sealed class ExaWebSearchProvider : IWebSearchProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<string?> _apiKey;
    private readonly bool _ownsClient;
    public string Id => "exa";
    public Uri Endpoint { get; }

    public ExaWebSearchProvider(Func<string?> apiKey, Uri? endpoint = null)
        : this(ProviderHttp.CreateNoRedirectClient(), apiKey, endpoint, ownsClient: true) { }

    internal ExaWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint = null)
        : this(client, apiKey, endpoint, ownsClient: false) { }

    private ExaWebSearchProvider(HttpClient client, Func<string?> apiKey, Uri? endpoint, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _ownsClient = ownsClient;
        Endpoint = ProviderHttp.ValidateEndpoint(endpoint ?? new Uri("https://api.exa.ai/search"));
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
    {
        ProviderHttp.ValidateRequest(request);
        var applied = new List<string> { "maxResults" };
        var unsupported = new List<string>();
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject(); writer.WriteString("query", request.Query); writer.WriteNumber("numResults", request.MaxResults);
            writer.WritePropertyName("contents"); writer.WriteStartObject(); writer.WriteBoolean("highlights", true); writer.WriteEndObject();
            if (request.RequiredDomains.Count != 0)
            {
                writer.WritePropertyName("includeDomains"); writer.WriteStartArray();
                foreach (var domain in request.RequiredDomains) writer.WriteStringValue(domain);
                writer.WriteEndArray(); applied.Add("requiredDomains");
            }
            foreach (var (key, value) in request.OptionalFilters)
            {
                if (key is "startPublishedDate" or "endPublishedDate" && DateTimeOffset.TryParse(value, out _)) { writer.WriteString(key, value); applied.Add(key); }
                else unsupported.Add(key);
            }
            writer.WriteEndObject();
        }
        if (request.PreferPrimarySources) unsupported.Add("preferPrimarySources");
        var payload = body.ToArray();
        return await ProviderHttp.SendAsync(_client, Endpoint, Id, request.MaxResults, message =>
        {
            message.Headers.TryAddWithoutValidation("x-api-key", ProviderHttp.RequireKey(_apiKey()));
            message.Content = new ByteArrayContent(payload); message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }, Parse, applied, unsupported, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<WebSearchCandidate> Parse(JsonElement root, string provider, int max)
    {
        var candidates = new List<WebSearchCandidate>();
        if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array) return candidates;
        foreach (var row in rows.EnumerateArray())
        {
            var url = ProviderHttp.String(row, "url");
            if (url is null || !ProviderHttp.IsHttpUrl(url)) continue;
            var snippet = ProviderHttp.String(row, "text") ?? "";
            if (snippet.Length == 0 && row.TryGetProperty("highlights", out var highlights) && highlights.ValueKind == JsonValueKind.Array)
                snippet = string.Join("\n", highlights.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()));
            candidates.Add(new(url, ProviderHttp.String(row, "title") ?? "", snippet, provider,
                ProviderHttp.String(row, "publishedDate"), ProviderHttp.String(row, "updatedDate"), ProviderHttp.String(row, "version")));
            if (candidates.Count == max) break;
        }
        return candidates;
    }
}

internal static class ProviderHttp
{
    private sealed class ProviderCredentialException : Exception { }

    internal static HttpClient CreateNoRedirectClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    internal static void ValidateRequest(WebSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 600 || request.MaxResults is < 1 or > 100)
            throw new ArgumentException("Web search query or result limit is invalid.", nameof(request));
        if (request.RequiredDomains.Count > 1200 || request.RequiredDomains.Any(domain => string.IsNullOrWhiteSpace(domain) || domain.Contains('/')))
            throw new ArgumentException("Required domains are invalid.", nameof(request));
    }

    internal static Uri ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0
            || endpoint.Fragment.Length != 0 || endpoint.Query.Length != 0)
            throw new ArgumentException("Provider endpoints must be absolute HTTPS URLs without user information, query, or fragments.", nameof(endpoint));
        return endpoint;
    }

    internal static string RequireKey(string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new ProviderCredentialException() : value;
    internal static string? String(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;

    internal static Uri WithQuery(Uri endpoint, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var query = string.Join("&", pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    internal static async Task<WebSearchResponse> SendAsync(HttpClient client, Uri endpoint, string provider, int max,
        Action<HttpRequestMessage> prepare, Func<JsonElement, string, int, IReadOnlyList<WebSearchCandidate>> parse,
        List<string> applied, List<string> unsupported, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(endpoint.Query.Length == 0 ? HttpMethod.Post : HttpMethod.Get, endpoint);
            prepare(message);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                var code = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited"
                    : (int)response.StatusCode >= 500 ? "provider_unavailable" : "provider_http_" + (int)response.StatusCode;
                return new([], applied, unsupported, code, transient);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (bounded.Length <= 2_097_152)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 2_097_153 - bounded.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                bounded.Write(buffer, 0, read);
            }
            if (bounded.Length > 2_097_152) return new([], applied, unsupported, "provider_response_too_large");
            using var document = JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (applied.Contains("requiredDomains", StringComparer.Ordinal) && RestrictionsRelaxed(document.RootElement))
            {
                applied.Remove("requiredDomains");
                unsupported.Add("requiredDomains");
            }
            return new(parse(document.RootElement, provider, max), applied, unsupported);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new([], applied, unsupported, "provider_unavailable", true); }
        catch (JsonException) { return new([], applied, unsupported, "invalid_provider_response"); }
        catch (ProviderCredentialException) { return new([], applied, unsupported, "credentials_unavailable"); }
        catch (InvalidOperationException) { return new([], applied, unsupported, "invalid_provider_response"); }
    }

    private static bool RestrictionsRelaxed(JsonElement root)
    {
        foreach (var name in new[] { "notices", "warnings" })
            if (root.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array
                && entries.EnumerateArray().Any(item => String(item, "code") == "RESTRICTIONS_RELAXED"))
                return true;
        return false;
    }
}
