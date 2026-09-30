namespace OmniCore.Models;

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Conteo exacto con el endpoint <c>POST /tokenize</c> de llama.cpp / ik_llama (ADR-0042 §1).
/// Si el endpoint falla, delega en el contador estimado y pasa a informar <c>Estimated</c>;
/// nunca lanza hacia la materialización del contexto. Cachea por (hash del contenido, TokenizerId).
/// </summary>
public sealed class LlamaCppTokenCounter : ITokenCounter
{
    private readonly string _baseUrl;
    private readonly Func<HttpClient> _httpFactory;
    private readonly ITokenCounter _fallback;
    private readonly Func<string?> _apiKey;
    private readonly TimeSpan _timeout;
    private readonly int _maxCacheEntries;
    private static readonly ConcurrentDictionary<(string Hash, string Tokenizer), int> _cache = new();
    private volatile bool _lastFellBack;

    public LlamaCppTokenCounter(string baseUrl, TokenizerId id, ITokenCounter fallback,
        Func<HttpClient>? httpFactory = null, Func<string?>? apiKey = null,
        TimeSpan? timeout = null, int maxCacheEntries = 4096)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        Id = id;
        _fallback = fallback;
        _httpFactory = httpFactory ?? (static () => new HttpClient());
        _apiKey = apiKey ?? (static () => null);
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _maxCacheEntries = maxCacheEntries;
    }

    public TokenizerId Id { get; }

    /// <summary>Exact mientras el endpoint responda; Estimated tras un fallo (el último conteo manda).</summary>
    public TokenCountAccuracy Accuracy => _lastFellBack ? TokenCountAccuracy.Estimated : TokenCountAccuracy.Exact;

    public async Task<int> CountAsync(ContextItem item, CancellationToken cancellationToken)
    {
        var text = item.Content;
        if (string.IsNullOrEmpty(text)) return 0;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        if (_cache.TryGetValue((hash, Id.Value), out var hit))
        {
            _lastFellBack = false;
            return hit;
        }

        try
        {
            var n = await TokenizeAsync(text, cancellationToken).ConfigureAwait(false);
            _lastFellBack = false;
            Store((hash, Id.Value), n);
            return n;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _lastFellBack = true;
            return await _fallback.CountAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Store((string, string) key, int value)
    {
        if (_cache.Count >= _maxCacheEntries) _cache.Clear();
        _cache[key] = value;
    }

    private async Task<int> TokenizeAsync(string text, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        var body = JsonSerializer.Serialize(new TokenizeRequestDto { Content = text }, OpenAiJsonContext.Default.TokenizeRequestDto);
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/tokenize")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var key = _apiKey();
        if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var client = _httpFactory();
        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize(json, OpenAiJsonContext.Default.TokenizeResponseDto);
        return dto?.Tokens?.Count ?? throw new InvalidOperationException("Respuesta /tokenize sin 'tokens'.");
    }
}

internal sealed class TokenizeRequestDto
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

internal sealed class TokenizeResponseDto
{
    [JsonPropertyName("tokens")] public List<JsonElement>? Tokens { get; set; }
}
