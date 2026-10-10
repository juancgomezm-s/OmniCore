using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OmniCore.Models;

/// <summary>
/// Respuesta del token endpoint de Anthropic. Los nombres siguen el wire (snake_case), no el
/// estilo C#: se mapean con atributos explícitos porque el contexto source-gen no deduce nada
/// por reflexión (analizadores AOT activos, ADR-0038 §5).
/// </summary>
public sealed class ClaudeOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Segundos hasta la expiración, no fecha absoluta. La visibilidad importa: una propiedad sin
    /// modificador es privada y el source-gen no la mapea, así que el campo del wire desaparece.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public long? ExpiresInSecondsValue { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("account")]
    public ClaudeOAuthAccountDto? Account { get; set; }

    [JsonPropertyName("organization")]
    public ClaudeOAuthOrganizationDto? Organization { get; set; }

    [JsonIgnore]
    public int? ExpiresInSeconds => ExpiresInSecondsValue is > 0 ? (int)ExpiresInSecondsValue.Value : null;

    /// <summary>Scopes del wire separados por espacios (client.ts:parseScopes).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Scopes => ParseScopes(Scope);

    internal static IReadOnlyList<string> ParseScopes(string? scopeString)
    {
        if (string.IsNullOrWhiteSpace(scopeString))
        {
            return [];
        }

        return scopeString.Split((char[]?)[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
    }
}

public sealed class ClaudeOAuthAccountDto
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("email_address")]
    public string? EmailAddress { get; set; }
}

public sealed class ClaudeOAuthOrganizationDto
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

/// <summary>
/// Tokens ya convertidos a estado de runtime: expiración absoluta en UTC y scopes normalizados.
/// Equivalente al OAuthTokens de client.ts, no al DTO del wire.
/// </summary>
public sealed record ClaudeOAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes,
    string ClientId,
    string? AccountUuid,
    string? OrganizationUuid)
{
    /// <summary>True si el token sirve para inferencia bajo suscripción claude.ai.</summary>
    [JsonIgnore]
    public bool CanDoInference => Scopes.Contains(ClaudeOAuthScopes.Inference, StringComparer.Ordinal);
}

/// <summary>Nombres de scope usados por el protocolo de Claude Code (oauthConstants.ts:41-60).</summary>
public static class ClaudeOAuthScopes
{
    public const string Profile = "user:profile";
    public const string Inference = "user:inference";
    public const string Sessions = "user:sessions:claude_code";
    public const string McpServers = "user:mcp_servers";
    public const string FileUpload = "user:file_upload";
    public const string ConsoleCreateApiKey = "org:create_api_key";
}

/// <summary>
/// Cliente del token endpoint y del perfil de Anthropic. Porta exchangeCodeForTokens,
/// refreshOAuthToken y fetchProfileInfo de packages/provider/src/oauth/client.ts, más
/// getOauthProfileFromOauthToken de oauth/getOauthProfile.ts.
///
/// No sabe nada de persistencia ni de locks: eso es ClaudeOAuthRefreshCoordinator (F5). Aquí solo
/// hay RPC puro, para que los contratos se testeen con un HttpMessageHandler falso.
/// </summary>
public sealed class ClaudeOAuthTokenClient
{
    /// <summary>
    /// Timeout del exchange y del refresh. client.ts:249 y :305 usan 30_000 ms con el comentario
    /// de que 15 s se queda corto bajo picos de /login; se mantiene el valor de referencia.
    /// </summary>
    public static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeout del perfil: getOauthProfile.ts usa 10 s.</summary>
    public static readonly TimeSpan DefaultProfileTimeout = TimeSpan.FromSeconds(10);

    private readonly ClaudeOAuthClientIdentity _identity;
    private readonly Func<HttpClient> _httpFactory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _rpcTimeout;
    private readonly TimeSpan _profileTimeout;
    private readonly string _profileUrl;

    public ClaudeOAuthTokenClient(
        ClaudeOAuthClientIdentity identity,
        Func<HttpClient>? httpFactory = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? rpcTimeout = null,
        TimeSpan? profileTimeout = null,
        string? profileUrl = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        _httpFactory = httpFactory ?? DefaultHttpClient;
        _utcNow = utcNow ?? DefaultUtcNow;
        _rpcTimeout = rpcTimeout ?? DefaultRpcTimeout;
        _profileTimeout = profileTimeout ?? DefaultProfileTimeout;
        // Prioridad: parametro explicito > identidad (providers.yaml) > derivacion del host de
        // token. La derivacion es el ultimo recurso, no la norma: quien declara una identidad
        // deberia poder decir donde esta su perfil.
        _profileUrl = profileUrl ?? identity.ProfileUrl ?? DeriveProfileUrl(identity.TokenUrl);
        if (_rpcTimeout <= TimeSpan.Zero || _profileTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rpcTimeout), "Los timeouts deben ser mayores que cero.");
        }
    }

    /// <summary>
    /// Intercambia el authorization code por tokens. Equivalente a exchangeCodeForTokens
    /// (client.ts:211-260).
    /// </summary>
    /// <param name="code">Code capturado por el callback.</param>
    /// <param name="state">State enviado en el authorize: el servidor lo devuelve y se valida.</param>
    /// <param name="codeVerifier">Verifier PKCE correspondiente al challenge del authorize.</param>
    /// <param name="redirectUri">redirect_uri exacto que se mandó al authorize. Es obligatorio:
    /// si difiere aunque sea en el número de puerto, Anthropic rechaza el exchange
    /// (client.ts:219 usa el mismo valor que construyó el authorize).</param>
    /// <param name="expiresInSeconds">TTL pedido (token largo). Null = el del servidor.</param>
    /// <param name="ct">Cancelación de la operación. Un cancelamiento del usuario no se confunde
    /// con el timeout del RPC: son fallos distintos.</param>
    public Task<ClaudeOAuthTokens> ExchangeCodeAsync(
        string code,
        string state,
        string codeVerifier,
        string redirectUri,
        int? expiresInSeconds = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);

        var body = new ClaudeOAuthTokenRequest
        {
            GrantType = "authorization_code",
            Code = code,
            RedirectUri = redirectUri,
            ClientId = _identity.ClientId,
            CodeVerifier = codeVerifier,
            State = state,
            ExpiresInSeconds = expiresInSeconds is > 0 ? expiresInSeconds.Value : null,
        };

        return SendTokenRequestAsync(body, TokenFailureKind.Exchange, retainRefreshTokenFrom: null, ct);
    }

    /// <summary>
    /// Refresca un access token. Equivalente a refreshOAuthToken (client.ts:266-354).
    ///
    /// El client id es pegajoso: si <paramref name="clientId"/> viene de un credential emitido por
    /// otro cliente, se respeta en vez del de la identidad. Sin esto, un token obtenido con un
    /// client id concreto dejaría de refrescar (client.ts:281-286).
    /// </summary>
    public Task<ClaudeOAuthTokens> RefreshAsync(
        string refreshToken,
        IReadOnlyList<string>? scopes = null,
        string? clientId = null,
        int? expiresInSeconds = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var body = new ClaudeOAuthTokenRequest
        {
            GrantType = "refresh_token",
            RefreshToken = refreshToken,
            // Sticky client id (client.ts:281-286): si el credential vino de otro cliente se
            // respeta el suyo en vez del de la identidad.
            ClientId = string.IsNullOrEmpty(clientId) ? _identity.ClientId : clientId,
            // Por defecto pide el conjunto completo de claude.ai: el backend admite expansión de
            // scopes sobre un refresh token existente (client.ts:288-295).
            Scope = string.Join(' ', scopes is { Count: > 0 } ? scopes : _identity.Scopes),
            ExpiresInSeconds = expiresInSeconds is > 0 ? expiresInSeconds.Value : null,
        };

        // Si el servidor no devuelve refresh token nuevo, conserva el que teníamos
        // (client.ts:311: `refresh_token: newRefreshToken = refreshToken`).
        return SendTokenRequestAsync(body, TokenFailureKind.Refresh, retainRefreshTokenFrom: refreshToken, ct);
    }

    private async Task<ClaudeOAuthTokens> SendTokenRequestAsync(
        ClaudeOAuthTokenRequest body,
        TokenFailureKind kind,
        string? retainRefreshTokenFrom,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, ClaudeOAuthJsonContext.Default.ClaudeOAuthTokenRequest);
        ClaudeOAuthWireErrorResponse? error = null;
        ClaudeOAuthTokenResponse? payload = null;
        var status = 0;
        Exception? transport = null;

        try
        {
            using var http = _httpFactory();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_rpcTimeout);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http
                .PostAsync(new Uri(_identity.TokenUrl), content, timeout.Token)
                .ConfigureAwait(false);

            status = (int)response.StatusCode;
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                error = TryParseError(responseBody);
                throw BuildFailure(kind, status, error, responseBody);
            }

            payload = JsonSerializer.Deserialize(responseBody, ClaudeOAuthJsonContext.Default.ClaudeOAuthTokenResponse)
                ?? throw BuildFailure(kind, status, error: null, responseBody);
        }
        catch (ClaudeOAuthException)
        {
            // Fallo del wire ya tipado: no se reenvuelve como red ni como protocolo.
            throw;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout propio del RPC, no cancelación del usuario: son fallos distintos.
            throw new ClaudeOAuthNetworkException("El token endpoint de Anthropic no respondió a tiempo.", ex);
        }
        catch (HttpRequestException ex)
        {
            transport = ex;
            throw new ClaudeOAuthNetworkException("No se pudo alcanzar el token endpoint de Anthropic.", ex);
        }
        catch (JsonException ex)
        {
            throw new ClaudeOAuthProtocolException(
                "La respuesta del token endpoint de Anthropic no es el JSON esperado.", ex, status);
        }

        // Fuera del try: un fallo al armar el estado no debe confundirse con un fallo del wire.
        var accessToken = payload.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new ClaudeOAuthProtocolException(
                "La respuesta del token endpoint no trae access_token.", inner: null, status);
        }

        var expiresIn = payload.ExpiresInSeconds ?? 0;
        if (expiresIn <= 0)
        {
            throw new ClaudeOAuthProtocolException(
                "La respuesta del token endpoint no trae un expires_in válido.", inner: null, status);
        }

        var refreshToken = payload.RefreshToken ?? retainRefreshTokenFrom;
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new ClaudeOAuthProtocolException(
                "La respuesta del token endpoint no trae refresh_token.", inner: null, status);
        }

        return new ClaudeOAuthTokens(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: _utcNow().AddSeconds(expiresIn),
            Scopes: payload.Scopes,
            ClientId: body.ClientId,
            AccountUuid: payload.Account?.Uuid,
            OrganizationUuid: payload.Organization?.Uuid);
    }

    private ClaudeOAuthException BuildFailure(
        TokenFailureKind kind,
        int status,
        ClaudeOAuthWireErrorResponse? error,
        string rawBody)
    {
        // RFC 6749 deja el error como string o como objeto con `type`: ambos se miran
        // (client.ts:isInvalidGrantError).
        var errorType = error?.ErrorType;
        var invalidGrant = status is 400 or 401 && string.Equals(errorType, "invalid_grant", StringComparison.Ordinal);

        if (kind == TokenFailureKind.Exchange && status == (int)HttpStatusCode.Unauthorized)
        {
            return new ClaudeOAuthExchangeInvalidCodeException();
        }

        if (invalidGrant)
        {
            return new ClaudeOAuthInvalidGrantException();
        }

        return kind == TokenFailureKind.Exchange
            ? new ClaudeOAuthExchangeHttpException(status, Sanitize(errorType, rawBody))
            : new ClaudeOAuthRefreshHttpException(status, Sanitize(errorType, rawBody));
    }

    /// <summary>
    /// Detalle del fallo: solo el error-type del servidor si tiene pinta de token RFC 6749. El
    /// cuerpo crudo puede llevar material sensible y no se propaga (INV-016, ADR-0018).
    /// </summary>
    private static string? Sanitize(string? errorType, string rawBody) =>
        ErrorTypePattern.IsMatch(errorType ?? string.Empty) ? errorType : null;

    /// <summary>
    /// Forma de un error-type RFC 6749: minúsculas y guion bajo, hasta 40 chars. Es el filtro que
    /// usa client.ts antes de loguear el type del servidor, para no propagar payloads libres.
    /// </summary>
    private static readonly Regex ErrorTypePattern = new(
        "^[a-z][a-z_]{0,39}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static ClaudeOAuthWireErrorResponse? TryParseError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, ClaudeOAuthJsonContext.Default.ClaudeOAuthWireErrorResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Obtiene el perfil de la cuenta. Equivalente a getOauthProfileFromOauthToken
    /// (getOauthProfile.ts:57-84). Devuelve null ante cualquier fallo: el perfil es enrichment, no
    /// crítico — perderlo no debe tumbar un login ni un refresh (client.ts:330-341 cae al valor
    /// almacenado).
    /// </summary>
    public async Task<ClaudeOAuthProfile?> FetchProfileAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        try
        {
            using var http = _httpFactory();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_profileTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_profileUrl));
            request.Headers.Authorization = new("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(body, ClaudeOAuthJsonContext.Default.ClaudeOAuthProfileDto);
            return dto is null ? null : ClaudeOAuthProfile.FromWire(dto);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// El perfil vive en el host de API, no en el del token. Se deriva del TokenUrl en vez de
    /// añadir otra constante: así un usuario que apunte a un endpoint propio no tiene que
    /// configurar dos URLs coherentes entre sí.
    /// </summary>
    private static string DeriveProfileUrl(string tokenUrl)
    {
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var token))
        {
            throw new ArgumentException("El TokenUrl de la identidad no es una URL absoluta válida.", nameof(tokenUrl));
        }

        // https://platform.claude.com/v1/oauth/token -> https://api.anthropic.com/api/oauth/profile
        // Solo se hereda el esquema; el host de API es el de la propia API de Anthropic.
        var apiHost = string.Equals(token.Host, "platform.claude.com", StringComparison.OrdinalIgnoreCase)
            ? "api.anthropic.com"
            : token.Host;
        return $"{token.Scheme}://{apiHost}/api/oauth/profile";
    }

    private static HttpClient DefaultHttpClient() => new();

    private static DateTimeOffset DefaultUtcNow() => DateTimeOffset.UtcNow;

    private enum TokenFailureKind
    {
        Exchange,
        Refresh,
    }
}
