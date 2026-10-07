namespace OmniCore.Models;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// Reintentos con backoff + jitter y circuit breaker por provider, comunes a todos los adaptadores
/// HTTP. Solo se reintenta antes de recibir la respuesta: una vez que el stream empezó, un fallo
/// se propaga (nunca se duplica una generación ya emitida).
/// </summary>
internal sealed class ProviderResilience
{
    internal long MaximumGenerationRequestAttempts => (long)_options.MaxRetries + 1;
    private readonly OpenAiProviderOptions _options;
    private readonly string _providerKey;
    private readonly object _breakerLock = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private long? _activeProbe;
    private long _nextProbe;

    public ProviderResilience(OpenAiProviderOptions options, string providerKey)
    {
        if (options.CircuitFailureThreshold < 1 || options.MaxRetries < 0 || options.CircuitCooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Los límites de resiliencia no pueden ser negativos y el umbral debe ser mayor que cero.");
        _options = options;
        _providerKey = providerKey;
    }

    public long? EnterCircuit()
    {
        lock (_breakerLock)
        {
            if (_openUntil is null) return null;
            if (_options.UtcNow() < _openUntil.Value)
                throw new ModelProviderException("ProviderUnavailable", "Circuit breaker abierto para " + _providerKey + ".");
            if (_activeProbe is not null)
                throw new ModelProviderException("ProviderUnavailable", "Circuit breaker en prueba para " + _providerKey + ".");
            var probe = ++_nextProbe;
            _activeProbe = probe;
            return probe;
        }
    }

    public ProviderCircuitSnapshot Snapshot()
    {
        lock (_breakerLock)
        {
            var measuredAt = _options.UtcNow();
            var probeInFlight = _activeProbe is not null;
            var canAttempt = _openUntil is null
                || measuredAt >= _openUntil.Value && !probeInFlight;
            return new ProviderCircuitSnapshot(measuredAt, _openUntil, probeInFlight, canAttempt);
        }
    }

    /// <summary>Releases a reserved half-open probe that was canceled before a result was observed.</summary>
    public void AbandonProbe(long? probe)
    {
        if (probe is null) return;
        lock (_breakerLock)
            if (_activeProbe == probe) _activeProbe = null;
    }

    public void MarkSuccess()
    {
        lock (_breakerLock)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _activeProbe = null;
        }
    }

    public void MarkFailure()
    {
        lock (_breakerLock)
        {
            _activeProbe = null;
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitFailureThreshold)
                _openUntil = _options.UtcNow() + _options.CircuitCooldown;
        }
    }

    /// <summary>Envía la request (reconstruida en cada intento) y devuelve la respuesta con cabeceras leídas.</summary>
    public async System.Threading.Tasks.Task<(HttpResponseMessage Response, HttpClient Client)> SendWithRetryAsync(
        Func<HttpClient> httpFactory, Func<HttpRequestMessage> buildRequest, Func<int, string, ModelProviderException> errorFromBody,
        Func<int, bool> isRetryableStatus, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var http = httpFactory();
            try
            {
                using var httpRequest = buildRequest();
                OmniCore.Abstractions.GenerationRequestAttemptScope.RecordGenerationSend();
                var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return (response, http);
                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (isRetryableStatus(status) && attempt < _options.MaxRetries)
                {
                    var delay = ComputeDelay(attempt, RetryAfter(response));
                    response.Dispose();
                    http.Dispose();
                    await _options.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                response.Dispose();
                http.Dispose();
                MarkFailure();
                throw errorFromBody(status, body);
            }
            catch (HttpRequestException ex)
            {
                http.Dispose();
                if (attempt < _options.MaxRetries)
                {
                    await _options.DelayAsync(ComputeDelay(attempt, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                MarkFailure();
                throw new ModelProviderException("ProviderUnavailable", ex.Message, null, ex);
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is not null) return retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value;
        var multiplier = Math.Pow(2, attempt);
        var raw = TimeSpan.FromMilliseconds(_options.BaseRetryDelay.TotalMilliseconds * multiplier);
        var jitter = Math.Clamp(_options.Jitter(), 0, 1);
        var jittered = raw.TotalMilliseconds * (0.75 + jitter * 0.5);
        return TimeSpan.FromMilliseconds(Math.Min(_options.MaxRetryDelay.TotalMilliseconds, jittered));
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        if (delta is not null) return delta;
        var date = response.Headers.RetryAfter?.Date;
        return date is null ? null : date.Value - _options.UtcNow();
    }

    /// <summary>Error tipado a partir del body de error (formato <c>{"error":{"message":…}}</c>, común a las familias).</summary>
    public static ModelProviderException ErrorFromBody(int status, string body)
    {
        var kind = status switch
        {
            401 or 403 => "AuthenticationFailed",
            408 or 429 => "RateLimited",
            >= 500 => "ProviderUnavailable",
            _ => "ProviderError",
        };
        var message = string.IsNullOrWhiteSpace(body) ? "(sin body)" : body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail))
                message = "#" + status.ToString(CultureInfo.InvariantCulture) + ": " + detail.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return new ModelProviderException(kind, message, status);
    }
}
