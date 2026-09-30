namespace OmniCore.Host;

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>
/// Endpoints OAuth del login con la suscripción de ChatGPT (ADR-0011 §3.4). Son configurables: OpenAI
/// puede cambiarlos y los de device code no tienen documentación pública estable.
/// </summary>
public sealed class ChatGptOAuthEndpoints
{
    public string AuthorizeUrl { get; init; } = "https://auth.openai.com/oauth/authorize";
    public string TokenUrl { get; init; } = "https://auth.openai.com/oauth/token";

    /// <summary>Client público de Codex, el único que OpenAI respalda para harnesses de terceros (ADR-0011 §3.4).</summary>
    public string ClientId { get; init; } = "app_EMoamEEZ73f0CkXaXp7hrann";

    public int CallbackPort { get; init; } = 1455;
    public string CallbackPath { get; init; } = "/auth/callback";
    public string Scope { get; init; } = "openid profile email offline_access";
    public string DeviceUserCodeUrl { get; init; } = "https://auth.openai.com/api/accounts/deviceauth/usercode";
    public string DeviceTokenUrl { get; init; } = "https://auth.openai.com/api/accounts/deviceauth/token";
    public string DeviceVerificationUrl { get; init; } = "https://auth.openai.com/codex/device";

    public string RedirectUri => "http://localhost:" + CallbackPort + CallbackPath;
}

/// <summary>Estado de la sesión de ChatGPT para <c>omni doctor</c> (nunca incluye tokens).</summary>
public sealed record ChatGptSessionStatus(bool LoggedIn, string? AccountIdMasked, DateTimeOffset? ExpiresAt, bool Expired);

/// <summary>Error tipado del login de ChatGPT.</summary>
public sealed class ChatGptAuthException : InvalidOperationException
{
    public string Kind { get; }
    public LocalizedText UserMessage { get; }

    public ChatGptAuthException(string kind, string message)
        : base(message)
    {
        Kind = kind;
        UserMessage = LocalizedText.Of("chatgpt.auth." + kind);
    }
}

/// <summary>Espera el callback OAuth en loopback. Abstracción para poder probar el flujo sin red.</summary>
public interface IOAuthCallbackListener
{
    /// <summary>Devuelve la query del callback (<c>code</c>, <c>state</c> o <c>error</c>).</summary>
    System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(int port, string path, CancellationToken cancellationToken);
}

/// <summary>
/// Login OAuth 2.0 + PKCE (o device code) con la cuenta de ChatGPT del usuario y fuente de credenciales
/// del perfil <c>codex</c> de <see cref="OpenAIResponsesProvider"/> (ADR-0011 §3.4).
/// <list type="bullet">
/// <item>Tokens (<c>access</c>, <c>refresh</c>, <c>expires</c>, <c>account_id</c>) solo en <see cref="ICredentialStore"/>
/// (cifrado), nunca en texto plano ni en el journal; cada valor se registra en el redactor.</item>
/// <item>Refresh preventivo antes de expirar, serializado entre procesos con un mutex con nombre.</item>
/// <item>Identificación honesta: <c>originator=omnicore</c>; nunca se imita a Codex CLI.</item>
/// </list>
/// </summary>
public sealed class ChatGptSubscriptionAuthProvider : ISubscriptionCredentialSource
{
    internal const string CredentialKey = "chatgpt.session";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly ICredentialStore _store;
    private readonly Func<HttpClient> _httpFactory;
    private readonly ChatGptOAuthEndpoints _endpoints;
    private readonly Func<DateTimeOffset> _now;
    private readonly IOAuthCallbackListener _listener;
    private readonly Func<TimeSpan, CancellationToken, System.Threading.Tasks.Task> _delay;
    private readonly string _mutexName;

    public ChatGptSubscriptionAuthProvider(ICredentialStore store, Func<HttpClient> httpFactory,
        ChatGptOAuthEndpoints? endpoints = null, Func<DateTimeOffset>? now = null, IOAuthCallbackListener? listener = null,
        Func<TimeSpan, CancellationToken, System.Threading.Tasks.Task>? delay = null, string? mutexName = null)
    {
        _store = store;
        _httpFactory = httpFactory;
        _endpoints = endpoints ?? new ChatGptOAuthEndpoints();
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _listener = listener ?? new HttpListenerCallback();
        _delay = delay ?? (static (d, ct) => System.Threading.Tasks.Task.Delay(d, ct));
        _mutexName = mutexName ?? "OmniCore.ChatGptRefresh";
    }

    /// <summary>URL de autorización con PKCE S256 (el verificador nunca sale del proceso).</summary>
    public string BuildAuthorizationUrl(string state, string codeChallenge) =>
        _endpoints.AuthorizeUrl + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(_endpoints.ClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(_endpoints.RedirectUri)
        + "&scope=" + Uri.EscapeDataString(_endpoints.Scope)
        + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
        + "&code_challenge_method=S256"
        + "&state=" + Uri.EscapeDataString(state)
        + "&id_token_add_organizations=true"
        + "&codex_cli_simplified_flow=true"
        + "&originator=omnicore";

    /// <summary>Par PKCE: verificador aleatorio de 32 bytes y su challenge SHA-256 en base64url.</summary>
    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>Login con navegador: muestra la URL, espera el callback en loopback y canjea el código.</summary>
    public async System.Threading.Tasks.Task<ChatGptSessionStatus> LoginWithBrowserAsync(Action<string> showUrl,
        CancellationToken cancellationToken)
    {
        var (verifier, challenge) = CreatePkcePair();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var callback = _listener.WaitForCallbackAsync(_endpoints.CallbackPort, _endpoints.CallbackPath, cancellationToken);
        showUrl(BuildAuthorizationUrl(state, challenge));
        var query = await callback.ConfigureAwait(false);
        if (query.TryGetValue("error", out var error))
            throw new ChatGptAuthException("denied", "El login de ChatGPT fue rechazado: " + error);
        if (!query.TryGetValue("state", out var returned) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(returned), Encoding.UTF8.GetBytes(state)))
            throw new ChatGptAuthException("stateMismatch", "El parámetro state del callback no coincide.");
        if (!query.TryGetValue("code", out var code) || code.Length == 0)
            throw new ChatGptAuthException("denied", "El callback no trae código de autorización.");
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _endpoints.RedirectUri,
            ["client_id"] = _endpoints.ClientId,
            ["code_verifier"] = verifier,
        }, cancellationToken).ConfigureAwait(false);
        Save(tokens, cancellationToken);
        return Status(cancellationToken);
    }

    /// <summary>
    /// Login por device code para entornos sin navegador o con el puerto 1455 ocupado. Acepta que el
    /// endpoint de sondeo devuelva los tokens o un código de autorización con su verificador.
    /// </summary>
    public async System.Threading.Tasks.Task<ChatGptSessionStatus> LoginWithDeviceCodeAsync(Action<string, string> showCode,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var start = await PostJsonAsync(_endpoints.DeviceUserCodeUrl,
            "{\"client_id\":" + JsonSerializer.Serialize(_endpoints.ClientId, ChatGptJson.Default.String) + "}", cancellationToken).ConfigureAwait(false);
        var root = start.RootElement;
        var deviceAuthId = Str(root, "device_auth_id") ?? throw new ChatGptAuthException("denied", "Respuesta de device code sin device_auth_id.");
        var userCode = Str(root, "user_code") ?? Str(root, "usercode") ?? throw new ChatGptAuthException("denied", "Respuesta de device code sin user_code.");
        var interval = TimeSpan.FromSeconds(root.TryGetProperty("interval", out var i) && i.TryGetInt32(out var seconds) ? Math.Max(1, seconds) : 5);
        showCode(_endpoints.DeviceVerificationUrl, userCode);
        var deadline = _now() + (timeout ?? TimeSpan.FromMinutes(15));
        while (_now() < deadline)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            using var http = _httpFactory();
            using var content = new StringContent("{\"device_auth_id\":" + JsonSerializer.Serialize(deviceAuthId, ChatGptJson.Default.String)
                + ",\"user_code\":" + JsonSerializer.Serialize(userCode, ChatGptJson.Default.String) + "}", Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(_endpoints.DeviceTokenUrl, content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.PreconditionRequired)
                continue; // pendiente de aprobación
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ChatGptAuthException("denied", "El device code fue rechazado (" + (int)response.StatusCode + ").");
            using var poll = JsonDocument.Parse(body);
            TokenSet tokens;
            if (Str(poll.RootElement, "access_token") is not null) tokens = ParseTokens(poll.RootElement);
            else
            {
                var code = Str(poll.RootElement, "authorization_code") ?? throw new ChatGptAuthException("denied", "Respuesta de sondeo sin código.");
                var verifier = Str(poll.RootElement, "code_verifier") ?? throw new ChatGptAuthException("denied", "Respuesta de sondeo sin verificador.");
                tokens = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = _endpoints.DeviceVerificationUrl.TrimEnd('/') + "/callback",
                    ["client_id"] = _endpoints.ClientId,
                    ["code_verifier"] = verifier,
                }, cancellationToken).ConfigureAwait(false);
            }
            Save(tokens, cancellationToken);
            return Status(cancellationToken);
        }
        throw new ChatGptAuthException("timeout", "El device code expiró sin aprobación.");
    }

    public void Logout(CancellationToken cancellationToken) => _store.Delete(CredentialKey, cancellationToken);

    public ChatGptSessionStatus Status(CancellationToken cancellationToken)
    {
        var session = Load(cancellationToken);
        if (session is null) return new ChatGptSessionStatus(false, null, null, false);
        return new ChatGptSessionStatus(true, Mask(session.AccountId), session.ExpiresAt, session.ExpiresAt <= _now());
    }

    public async ValueTask<SubscriptionCredential> GetAsync(CancellationToken cancellationToken)
    {
        var session = Load(cancellationToken) ?? throw new ChatGptAuthException("notLoggedIn", "No hay sesión de ChatGPT: ejecuta omni login chatgpt.");
        if (session.ExpiresAt - RefreshMargin > _now()) return new SubscriptionCredential(session.Access, session.AccountId);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, _mutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }
            // Otro proceso pudo refrescar mientras esperábamos: se relee antes de pedir otro token.
            var session = Load(cancellationToken) ?? throw new ChatGptAuthException("notLoggedIn", "No hay sesión de ChatGPT: ejecuta omni login chatgpt.");
            if (session.ExpiresAt - RefreshMargin > _now() && session.RefreshedByOther)
                return new SubscriptionCredential(session.Access, session.AccountId);
            TokenSet tokens;
            try
            {
                tokens = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = session.Refresh,
                    ["client_id"] = _endpoints.ClientId,
                    ["scope"] = _endpoints.Scope,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatGptAuthException ex) when (ex.Kind == "denied")
            {
                throw new ModelProviderException("AuthenticationFailed", "No se pudo refrescar la sesión de ChatGPT: vuelve a ejecutar omni login chatgpt.");
            }
            if (tokens.Refresh.Length == 0) tokens = tokens with { Refresh = session.Refresh };
            if (tokens.AccountId.Length == 0) tokens = tokens with { AccountId = session.AccountId };
            Save(tokens, cancellationToken);
            return new SubscriptionCredential(tokens.Access, tokens.AccountId);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private async System.Threading.Tasks.Task<TokenSet> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var http = _httpFactory();
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(_endpoints.TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ChatGptAuthException("denied", "El servidor OAuth rechazó la solicitud (" + (int)response.StatusCode + ").");
        using var doc = JsonDocument.Parse(body);
        return ParseTokens(doc.RootElement);
    }

    private async System.Threading.Tasks.Task<JsonDocument> PostJsonAsync(string url, string json, CancellationToken cancellationToken)
    {
        using var http = _httpFactory();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ChatGptAuthException("denied", "El servidor OAuth rechazó la solicitud (" + (int)response.StatusCode + ").");
        return JsonDocument.Parse(body);
    }

    private TokenSet ParseTokens(JsonElement root)
    {
        var access = Str(root, "access_token") ?? throw new ChatGptAuthException("denied", "Respuesta OAuth sin access_token.");
        var refresh = Str(root, "refresh_token") ?? "";
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600;
        var accountId = AccountIdFromJwt(Str(root, "id_token")) ?? AccountIdFromJwt(access) ?? "";
        return new TokenSet(access, refresh, _now().AddSeconds(expiresIn), accountId);
    }

    /// <summary>Extrae <c>chatgpt_account_id</c> del claim <c>https://api.openai.com/auth</c> (sin validar firma: solo lectura local).</summary>
    public static string? AccountIdFromJwt(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
                ? Str(auth, "chatgpt_account_id") : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private void Save(TokenSet tokens, CancellationToken cancellationToken)
    {
        SecretRedactorRegistry.Register(tokens.Access);
        if (tokens.Refresh.Length > 0) SecretRedactorRegistry.Register(tokens.Refresh);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("access", tokens.Access);
            w.WriteString("refresh", tokens.Refresh);
            w.WriteNumber("expires", tokens.ExpiresAt.ToUnixTimeSeconds());
            w.WriteString("account_id", tokens.AccountId);
            w.WriteNumber("saved", _now().ToUnixTimeSeconds());
            w.WriteEndObject();
        }
        _store.Save(CredentialKey, Encoding.UTF8.GetString(buffer.WrittenSpan), cancellationToken);
    }

    private Session? Load(CancellationToken cancellationToken)
    {
        var raw = _store.Load(CredentialKey, cancellationToken);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var access = Str(root, "access");
            if (access is null) return null;
            var saved = root.TryGetProperty("saved", out var sv) && sv.TryGetInt64(out var savedAt) ? DateTimeOffset.FromUnixTimeSeconds(savedAt) : DateTimeOffset.MinValue;
            return new Session(access, Str(root, "refresh") ?? "",
                DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expires").GetInt64()), Str(root, "account_id") ?? "",
                saved > _now() - TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Mask(string accountId) =>
        accountId.Length <= 4 ? "****" : "****" + accountId[^4..];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenSet(string Access, string Refresh, DateTimeOffset ExpiresAt, string AccountId);

    private sealed record Session(string Access, string Refresh, DateTimeOffset ExpiresAt, string AccountId, bool RefreshedByOther);

    /// <summary>Callback en loopback con HttpListener (solo localhost).</summary>
    private sealed class HttpListenerCallback : IOAuthCallbackListener
    {
        public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(int port, string path,
            CancellationToken cancellationToken)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:" + port + path.TrimEnd('/') + "/");
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                throw new ChatGptAuthException("portInUse", "El puerto " + port + " está ocupado (" + ex.Message + "): usa omni login chatgpt --device.");
            }
            using var registration = cancellationToken.Register(listener.Stop);
            var context = await listener.GetContextAsync().ConfigureAwait(false);
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            var raw = context.Request.Url?.Query.TrimStart('?') ?? "";
            foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
            var html = Encoding.UTF8.GetBytes("<html><body><p>OmniCore: login completado. Ya puedes cerrar esta ventana.</p></body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(html, cancellationToken).ConfigureAwait(false);
            context.Response.Close();
            return query;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class ChatGptJson : System.Text.Json.Serialization.JsonSerializerContext;
