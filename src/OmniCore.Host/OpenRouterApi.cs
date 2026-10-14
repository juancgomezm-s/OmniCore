namespace OmniCore.Host;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>Small, bounded contracts for the official OpenRouter metadata API. Never stores a key.</summary>
public sealed record OpenRouterKeySnapshot(decimal? Limit, decimal? LimitRemaining, decimal? Usage,
    string? LimitResetInterval, DateTimeOffset? LimitResetAt, bool? IsManagementKey, DateTimeOffset AsOf,
    string RawJson = "{}");

/// <summary>Unmodified catalog facts; fields absent from OpenRouter remain null and raw metadata is preserved.</summary>
public sealed record OpenRouterModel(string Id, string? Name, string? Description, long? ContextLength,
    long? MaxCompletionTokens, string? PromptPricePerToken, string? CompletionPricePerToken,
    IReadOnlyList<string> InputModalities, IReadOnlyList<string> OutputModalities,
    IReadOnlyList<string> SupportedParameters, string RawJson);

public sealed record OpenRouterModelPage(int Offset, int Limit, IReadOnlyList<OpenRouterModel> Models,
    int? TotalCount, int? NextOffset, bool HasMore);

/// <summary>
/// Separate key-limit and account-balance readings. A normal API key may read its own key
/// limits while account balance is unsupported because the credits endpoint requires a
/// management key. Amounts are measurements; this type never authorizes model routing/spend.
/// </summary>
public sealed record OpenRouterCreditSnapshot(IReadOnlyList<CreditMeasurement> Measurements,
    MetricAvailability AccountBalanceAvailability, string? Limitation, DateTimeOffset AsOf,
    DateTimeOffset LastQueryAttemptAt, OpenRouterKeySnapshot Key)
{
    public string? ProviderId { get; init; }
    public string? CredentialGeneration { get; init; }
    public MetricAvailability KeyAvailability { get; init; } = MetricAvailability.Reported;
}

/// <summary>Bounded client for the official OpenRouter endpoints. Redirects are never followed.</summary>
public sealed class OpenRouterApi
{
    public const string ApiBaseUrl = "https://openrouter.ai/api/v1/";
    public const int MaximumPageSize = 1000;
    public const int MaximumBodyBytes = 8 * 1024 * 1024;
    private const int MaximumKeyOrCreditsBytes = 256 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    private readonly Func<HttpClient> _httpFactory;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _requestTimeout;

    public OpenRouterApi(Func<HttpClient>? httpFactory = null, Func<DateTimeOffset>? now = null,
        TimeSpan? requestTimeout = null)
    {
        _httpFactory = httpFactory ?? CreateHttpClient;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = DefaultRequestTimeout };

    /// <summary>Validate the current credential with GET /key; no model inference is sent.</summary>
    public async Task<ProviderTestResult> TestKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var response = await SendAsync("key", apiKey, MaximumKeyOrCreditsBytes, cancellationToken)
            .ConfigureAwait(false);
        if (response.Status.State != ProviderConnectionState.Connected)
            return response.Status;
        using var document = ParseObject(response.Body, "key");
        var data = Data(document.RootElement, "key");
        var snapshot = ParseKey(data, _now());
        return new ProviderTestResult(ProviderConnectionState.Connected,
            snapshot.IsManagementKey is true ? "HTTP 200; management key" : "HTTP 200",
            LocalizedText.Of("openrouter.connection.validated"));
    }

    /// <summary>Read one explicitly paginated catalog page; no eager unbounded catalog download.</summary>
    public async Task<OpenRouterModelPage> ListModelsAsync(string apiKey, int offset, int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), "OpenRouter catalog offset/limit is outside the bounded API range.");
        var response = await SendAsync("models?offset=" + offset.ToString(CultureInfo.InvariantCulture)
            + "&limit=" + limit.ToString(CultureInfo.InvariantCulture), apiKey, MaximumBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response.Status, "catalog");
        using var document = ParseObject(response.Body, "models");
        var data = Data(document.RootElement, "models");
        if (data.ValueKind != JsonValueKind.Array)
            throw new ProviderConnectionException("catalogInvalid", "OpenRouter models response did not contain a data array.");
        var models = new List<OpenRouterModel>();
        foreach (var item in data.EnumerateArray())
        {
            if (models.Count >= limit) break;
            var id = String(item, "id");
            if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(id))
                throw new ProviderConnectionException("catalogInvalid", "OpenRouter returned a model without an id.");
            models.Add(ParseModel(item, id!));
        }
        var total = Int(document.RootElement, "total_count") ?? Int(data, "total_count");
        var hasMore = models.Count == limit && (total is null || offset + models.Count < total.Value);
        return new(offset, limit, models, total, hasMore ? checked(offset + models.Count) : null, hasMore);
    }

    /// <summary>
    /// Refresh key measurements and try account credits independently. A 403 on /credits is
    /// expected for a normal API key and becomes an unavailable balance, never a zero balance.
    /// </summary>
    public async Task<OpenRouterCreditSnapshot> ReadCreditsAsync(string apiKey,
        CancellationToken cancellationToken)
    {
        var attemptedAt = _now();
        var keyResponse = await SendAsync("key", apiKey, MaximumKeyOrCreditsBytes, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(keyResponse.Status, "key");
        using var keyDocument = ParseObject(keyResponse.Body, "key");
        var key = ParseKey(Data(keyDocument.RootElement, "key"), _now());
        var measurements = new List<CreditMeasurement>();
        if (key.LimitRemaining is { } remaining)
            measurements.Add(new(CreditScope.KeyLimit, Reported(remaining, "openrouter.key.limit_remaining", key.AsOf),
                "USD", "USD", null));
        var limitation = (string?)null;
        var accountAvailability = MetricAvailability.Unknown;
        var creditsResponse = await SendAsync("credits", apiKey, MaximumKeyOrCreditsBytes, cancellationToken)
            .ConfigureAwait(false);
        if ((creditsResponse.Status.Detail == "HTTP 403" && key.IsManagementKey is not true)
            || creditsResponse.Status.Detail == "HTTP 401")
        {
            accountAvailability = MetricAvailability.NotSupported;
            limitation = "management_key_required";
        }
        else if (creditsResponse.Status.State != ProviderConnectionState.Connected)
        {
            accountAvailability = creditsResponse.Status.State == ProviderConnectionState.Unknown
                ? MetricAvailability.Stale : MetricAvailability.Unknown;
            limitation = creditsResponse.Status.Detail ?? "credits_unavailable";
        }
        else
        {
            using var creditDocument = ParseObject(creditsResponse.Body, "credits");
            var creditData = Data(creditDocument.RootElement, "credits");
            var totalCredits = Decimal(creditData, "total_credits");
            var totalUsage = Decimal(creditData, "total_usage");
            if (totalCredits is { } total && totalUsage is { } usage)
            {
                var balance = total - usage;
                measurements.Add(new(CreditScope.AccountBalance, Reported(balance,
                    "openrouter.account.remaining_credits", _now()), "USD", "USD", null));
                accountAvailability = MetricAvailability.Reported;
            }
            else
            {
                accountAvailability = MetricAvailability.Unknown;
                limitation = "account_balance_fields_missing";
            }
        }
        return new(measurements, accountAvailability, limitation, key.AsOf, attemptedAt, key);
    }

    private async Task<(ProviderTestResult Status, byte[] Body)> SendAsync(string path, string apiKey,
        int maximumBodyBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < Secret.MinimumLength)
            throw new ProviderConnectionException("empty", "OpenRouter API key is missing or too short.");
        SecretRedactorRegistry.Register(apiKey);
        using var http = _httpFactory();
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBaseUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", AnthropicConnectionValidator.UserAgent());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var status = Classify(response.StatusCode);
            if (status.State != ProviderConnectionState.Connected) return (status, []);
            var body = await ReadBoundedAsync(response.Content, maximumBodyBytes, deadline.Token).ConfigureAwait(false);
            return (status, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (new(ProviderConnectionState.Unknown, "timeout", LocalizedText.Of("providers.notice.unreachable")), []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (new(ProviderConnectionState.Unreachable, "network", LocalizedText.Of("providers.notice.unreachable")), []);
        }
    }

    private static ProviderTestResult Classify(HttpStatusCode code) => code switch
    {
        _ when (int)code is >= 200 and < 300 => new(ProviderConnectionState.Connected, "HTTP " + (int)code, null),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(ProviderConnectionState.Invalid,
            "HTTP " + (int)code, LocalizedText.Of("providers.error.invalid")),
        HttpStatusCode.TooManyRequests => new(ProviderConnectionState.Unknown, "HTTP 429",
            LocalizedText.Of("providers.notice.rateLimited")),
        _ when (int)code is >= 300 and < 400 => new(ProviderConnectionState.Unknown, "HTTP " + (int)code,
            LocalizedText.Of("providers.notice.redirectNotFollowed")),
        _ => new(ProviderConnectionState.Unreachable, "HTTP " + (int)code,
            LocalizedText.Of("providers.notice.unreachable")),
    };

    private static void EnsureSuccess(ProviderTestResult status, string operation)
    {
        if (status.State != ProviderConnectionState.Connected)
            throw new ProviderConnectionException(status.State == ProviderConnectionState.Invalid ? "invalid" : "unreachable",
                "OpenRouter " + operation + " request failed (" + (status.Detail ?? "no detail") + ").");
    }

    private static OpenRouterKeySnapshot ParseKey(JsonElement data, DateTimeOffset now)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ProviderConnectionException("keyInvalid", "OpenRouter key response did not contain data.");
        var reset = String(data, "limit_reset");
        return new(Decimal(data, "limit"), Decimal(data, "limit_remaining"), Decimal(data, "usage"),
            reset is "daily" or "weekly" or "monthly" ? reset : null,
            DateTimeOffset.TryParse(String(data, "resets_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null,
            Bool(data, "is_management_key"), now, data.GetRawText());
    }

    private static OpenRouterModel ParseModel(JsonElement item, string id)
    {
        var architecture = Object(item, "architecture");
        var topProvider = Object(item, "top_provider");
        var pricing = Object(item, "pricing");
        return new(id, String(item, "name"), String(item, "description"), Long(item, "context_length"),
            Long(topProvider, "max_completion_tokens"), String(pricing, "prompt"), String(pricing, "completion"),
            Strings(architecture, "input_modalities"), Strings(architecture, "output_modalities"),
            Strings(item, "supported_parameters"), item.GetRawText());
    }

    internal static OpenRouterModel ParseModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || String(item, "id") is not { Length: > 0 } id)
            throw new ProviderConnectionException("catalogInvalid", "OpenRouter catalog entry has no model id.");
        return ParseModel(item, id);
    }

    private static Metric<decimal?> Reported(decimal value, string source, DateTimeOffset at) =>
        new(MetricAvailability.Reported, value, source, at);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maxBytes)
            throw new ProviderConnectionException("responseTooLarge", "OpenRouter response exceeds the configured size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new ProviderConnectionException("responseTooLarge", "OpenRouter response exceeds the configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static JsonDocument ParseObject(byte[] bytes, string operation)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException ex)
        { throw new ProviderConnectionException(operation == "models" ? "catalogInvalid" : "responseInvalid",
            "OpenRouter returned invalid JSON (" + ex.GetType().Name + ")."); }
        throw new ProviderConnectionException(operation == "models" ? "catalogInvalid" : "responseInvalid",
            "OpenRouter returned a non-object JSON response.");
    }

    private static JsonElement Data(JsonElement root, string operation) => root.TryGetProperty("data", out var data)
        ? data : throw new ProviderConnectionException(operation == "models" ? "catalogInvalid" : "responseInvalid",
            "OpenRouter response is missing data.");
    private static JsonElement Object(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;
    private static string? String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal? Decimal(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number : null;
    }
    private static long? Long(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result) && result > 0 ? result : null;
    private static int? Int(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result) && result >= 0 ? result : null;
    private static bool? Bool(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static IReadOnlyList<string> Strings(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Take(64).ToArray() : [];
}
