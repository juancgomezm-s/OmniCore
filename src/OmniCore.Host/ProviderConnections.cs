namespace OmniCore.Host;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Estado de conexión de un provider tal y como lo ve el backend de conexiones (ADR-0011 §3).
/// Nunca contiene material de secretos: solo presencia, método, estado y pistas enmascaradas.
/// </summary>
public enum ProviderConnectionMethod
{
    /// <summary>API key guardada en el credential store (este backend).</summary>
    ApiKey,

    /// <summary>Sin credencial (p. ej. servidor local en loopback).</summary>
    None,

    /// <summary>Suscripción del usuario con OAuth (se gestiona por el bloque de cuentas).</summary>
    Subscription,
}

/// <summary>
/// Estado de conexión medido. <see cref="Unknown"/> significa "no medido o no concluyente":
/// nunca se inventa un valor. <see cref="Expired"/> solo lo produce una credencial con expiración
/// propia (OAuth); una API key no informa expiración y nunca se reporta como expirada.
/// </summary>
public enum ProviderConnectionState
{
    /// <summary>La Models API aceptó la credencial (2xx).</summary>
    Connected,

    /// <summary>No hay credencial guardada.</summary>
    NotConfigured,

    /// <summary>Credencial con expiración propia vencida (OAuth; no aplica a API key).</summary>
    Expired,

    /// <summary>El provider rechazó la credencial (401/403).</summary>
    Invalid,

    /// <summary>No se pudo contactar al provider (red, timeout, 5xx).</summary>
    Unreachable,

    /// <summary>Hay credencial pero el estado no está medido o no es concluyente.</summary>
    Unknown,
}

/// <summary>Fila de estado de una conexión (sin secretos; ADR-0018).</summary>
public sealed record ProviderConnectionStatus(
    string ProviderId,
    ProviderConnectionMethod Method,
    ProviderConnectionState State,
    BillingMode BillingMode,
    string? Detail,
    LocalizedText? Notice,
    bool CanConnect,
    bool CanTest,
    bool CanDisconnect,
    bool CanDiscoverModels);

/// <summary>Excepción tipada de los flujos de conexión; el cliente muestra <see cref="UserMessage"/>.</summary>
public sealed class ProviderConnectionException : InvalidOperationException
{
    public string Kind { get; }

    public LocalizedText UserMessage { get; }

    public ProviderConnectionException(string kind, string message) : base(message)
    {
        Kind = kind;
        UserMessage = LocalizedText.Of("providers.error." + kind);
    }
}

/// <summary>Resultado de un test de conexión (solo estado y detalle; nunca la credencial).</summary>
public sealed record ProviderTestResult(ProviderConnectionState State, string? Detail, LocalizedText? Notice);

/// <summary>Resultado de conectar una API key de Anthropic.</summary>
public sealed record ProviderConnectResult(ProviderConnectionState State, string? Detail, LocalizedText? Notice);

/// <summary>Modelo descubierto en la Models API de Anthropic (GET /v1/models). Lo que el API no
/// informa queda null: contexto, límites de salida y capacidades desconocidos no se inventan.</summary>
public sealed record AvailableAnthropicModel(
    string Id, string DisplayName, long? ContextWindow, long? MaxOutputTokens, bool? ThinkingSupported);

/// <summary>Resultado del descubrimiento de modelos de Anthropic.</summary>
/// <param name="ProviderId">Provider al que pertenecen los modelos registrados.</param>
/// <param name="RegisteredModels">Modelos nuevos añadidos a la configuración User.</param>
/// <param name="Truncated">True si el provider afirmó tener más páginas al llegar al tope.</param>
/// <param name="Quota">Ventanas de rate limit informadas en cabeceras; vacía si el provider no
/// informó ninguna (nunca se fabrican ceros ni ventanas de suscripción).</param>
public sealed record AnthropicDiscoveryResult(string ProviderId, int RegisteredModels, bool Truncated,
    IReadOnlyList<RateLimitWindow> Quota);

/// <summary>
/// Validador de la conexión Anthropic contra la Models API pública (GET /v1/models): es una
/// consulta de metadatos sin inferencia, así que probar la conexión nunca gasta dinero.
/// Contrato documentado (platform.claude.com/docs/en/api/models/list, consultado 2026-10-08):
/// cabeceras <c>x-api-key</c> y <c>anthropic-version: 2023-06-01</c>; 401 con credencial
/// inválida, 403 con permiso insuficiente, 429 de rate limit.
/// </summary>
public sealed class AnthropicConnectionValidator
{
    internal const string DefaultBaseUrl = "https://api.anthropic.com";
    internal const string ApiVersion = "2023-06-01";

    /// <summary>Tope de lectura del cuerpo: solo se necesita el código de estado.</summary>
    internal const long MaxBodyBytes = 64 * 1024;

    private readonly Func<HttpClient> _httpFactory;

    public AnthropicConnectionValidator(Func<HttpClient>? httpFactory = null) =>
        _httpFactory = httpFactory ?? CreateHttpClient;

    /// <summary>
    /// Cliente por llamada: TLS con la validación estándar del sistema para hosts públicos
    /// (ADR-0038 §4) y redirecciones desactivadas (ADR-0011 §3.6): la credencial nunca se
    /// reenvía a otro host. Cada llamada dispone su cliente.
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>
    /// Identificación honesta de OmniCore (ADR-0011 §3.3, §10.1): nunca
    /// <c>claude-cli/…</c> ni cabeceras de identidad de otros productos.
    /// </summary>
    internal static string UserAgent()
    {
        var version = typeof(AnthropicConnectionValidator).Assembly.GetName().Version?.ToString() ?? "0";
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        return "omnicore/" + version + " (" + os + "; " + arch + ")";
    }

    /// <summary>
    /// Valida el endpoint antes de transmitir la credencial: URL absoluta http(s), sin
    /// <c>user:pass</c>. La credencial solo viaja a este destino, que proviene de la
    /// configuración User del propio usuario (nunca de un repo ni de la salida del modelo).
    /// </summary>
    internal static Uri EndpointUri(string baseUrl, string pathAndQuery)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ProviderConnectionException("invalidEndpoint",
                "El endpoint del provider debe ser una URL absoluta http(s) sin credenciales incrustadas: "
                + RedactEndpoint(baseUrl));
        }

        return new Uri(baseUri, pathAndQuery);
    }

    /// <summary>El endpoint nunca entra en un error con fragmentos de credencial.</summary>
    private static string RedactEndpoint(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : baseUrl;

    public async Task<ProviderTestResult> ValidateAsync(string apiKey, string baseUrl,
        CancellationToken cancellationToken)
    {
        using var http = _httpFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            EndpointUri(baseUrl, "/v1/models?limit=1"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent());
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ProviderTestResult(ProviderConnectionState.Unreachable, "network",
                LocalizedText.Of("providers.notice.unreachable"));
        }

        using (response)
        {
            return Classify(response.StatusCode);
        }
    }

    /// <summary>
    /// Clasificación por código: 2xx conectado; 401/403 inválida; 429 y redirecciones no
    /// concluyentes; el resto (5xx incluido) inalcanzable. El cuerpo nunca se lee para el
    /// detalle: no hay secretos ni texto del provider en el resultado.
    /// </summary>
    internal static ProviderTestResult Classify(HttpStatusCode statusCode) => statusCode switch
    {
        _ when (int)statusCode is >= 200 and < 300 =>
            new(ProviderConnectionState.Connected, "HTTP " + (int)statusCode, null),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new(ProviderConnectionState.Invalid, "HTTP " + (int)statusCode,
                LocalizedText.Of("providers.error.invalid")),
        HttpStatusCode.TooManyRequests =>
            new(ProviderConnectionState.Unknown, "HTTP 429", LocalizedText.Of("providers.notice.rateLimited")),
        _ when (int)statusCode is >= 300 and < 400 =>
            new(ProviderConnectionState.Unknown, "HTTP " + (int)statusCode,
                LocalizedText.Of("providers.notice.redirectNotFollowed")),
        _ =>
            new(ProviderConnectionState.Unreachable, "HTTP " + (int)statusCode,
                LocalizedText.Of("providers.notice.unreachable")),
    };
}

/// <summary>
/// Descubrimiento de modelos de Anthropic (GET /v1/models con paginación <c>after_id</c>,
/// sin inferencia ni costo). La lista viene del provider; OmniCore no mantiene una lista
/// embebida de modelos Anthropic. Cada página y el total están acotados: nunca se pagina sin
/// límite. La cuota solo se lee de cabeceras reales <c>anthropic-ratelimit-*</c>.
/// </summary>
public sealed class AnthropicModelCatalog
{
    /// <summary>Tope documentado de la Models API por página.</summary>
    internal const int PageSize = 1000;

    /// <summary>Acotación defensiva de páginas: la API real devuelve pocas; un provider
    /// patológico no puede poner el proceso a paginar infinito.</summary>
    internal const int MaxPages = 20;

    /// <summary>Tope de lectura por página.</summary>
    internal const long MaxPageBytes = 4 * 1024 * 1024;

    private readonly Func<HttpClient> _httpFactory;

    public AnthropicModelCatalog(Func<HttpClient>? httpFactory = null) =>
        _httpFactory = httpFactory ?? AnthropicConnectionValidator.CreateHttpClient;

    public async Task<(IReadOnlyList<AvailableAnthropicModel> Models, bool Truncated,
        IReadOnlyList<RateLimitWindow> Quota)> ListAsync(string apiKey, string baseUrl,
        CancellationToken cancellationToken)
    {
        var models = new List<AvailableAnthropicModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        IReadOnlyList<RateLimitWindow> quota = [];
        for (var page = 0; page < MaxPages; page++)
        {
            using var http = _httpFactory();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                AnthropicConnectionValidator.EndpointUri(baseUrl, "/v1/models?limit=" + PageSize
                    + (afterId is null ? "" : "&after_id=" + Uri.EscapeDataString(afterId))));
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicConnectionValidator.ApiVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", AnthropicConnectionValidator.UserAgent());
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ProviderConnectionException("unreachable",
                    "No se pudo contactar a la Models API de Anthropic ("
                    + (ex is TaskCanceledException ? "timeout" : "error de red") + ").");
            }

            using (response)
            {
                if (page == 0)
                {
                    quota = RateLimitQuotaParser.Parse(response.Headers, DateTimeOffset.UtcNow);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ProviderConnectionException("invalid",
                        "La API de Anthropic rechazó la credencial (HTTP " + (int)response.StatusCode + ").");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new ProviderConnectionException("rateLimited",
                        "La Models API de Anthropic respondió HTTP 429 (rate limit).");
                }

                if ((int)response.StatusCode is < 200 or >= 300)
                {
                    throw new ProviderConnectionException("unreachable",
                        "La Models API de Anthropic respondió HTTP " + (int)response.StatusCode + ".");
                }

                var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                using var json = ParseCatalogBody(body);
                var added = Parse(json.RootElement);
                foreach (var model in added)
                {
                    if (seen.Add(model.Id)) models.Add(model);
                }

                if (json.RootElement.TryGetProperty("has_more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True
                    && json.RootElement.TryGetProperty("last_id", out var lastId) && lastId.ValueKind == JsonValueKind.String
                    && lastId.GetString() is { Length: > 0 } cursor)
                {
                    afterId = cursor;
                    continue;
                }

                return (models, false, quota);
            }
        }

        // El provider afirma que hay más páginas: se entrega lo acotado y se marca truncado.
        return (models, true, quota);
    }

    /// <summary>Lectura acotada del cuerpo: un provider patológico no puede agotar la memoria.</summary>
    internal static JsonDocument ParseCatalogBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ProviderConnectionException("catalogInvalid",
                "La Models API devolvió un cuerpo que no es JSON válido.");
        }
    }

    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxPageBytes)
            {
                throw new ProviderConnectionException("catalogTooLarge",
                    "La página de la Models API supera el tope de lectura de "
                    + MaxPageBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parseo tolerante del contrato documentado: <c>data[].id/display_name</c> son los
    /// campos garantizados; cualquier otro (límites, thinking) es opcional y queda null/unknown
    /// cuando no llega.</summary>
    internal static IReadOnlyList<AvailableAnthropicModel> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ProviderConnectionException("catalogInvalid",
                "Respuesta de la Models API sin lista de modelos (data).");
        var result = new List<AvailableAnthropicModel>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Str(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw new ProviderConnectionException("catalogInvalid",
                    "Respuesta de la Models API con un modelo sin id.");
            if (id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
                throw new ProviderConnectionException("catalogInvalid",
                    "Respuesta de la Models API con un id de modelo fuera de contrato.");
            var thinkingSupported = (bool?)null;
            if (item.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.Object
                && thinking.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Object
                && types.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.Object
                && enabled.TryGetProperty("supported", out var supported))
            {
                thinkingSupported = supported.ValueKind == JsonValueKind.True;
            }

            result.Add(new(id,
                new string((Str(item, "display_name") ?? id).Where(c => !char.IsControl(c)).ToArray()),
                NullableLong(item, "max_input_tokens"), NullableLong(item, "max_tokens"), thinkingSupported));
        }

        return result;
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? NullableLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0
            ? n : null;
}

/// <summary>Estado de conexión guardado de un provider (solo enmascarado y marcas de tiempo; ADR-0018).</summary>
public sealed record ProviderConnectionMetadata(string? MaskedKey, string? ValidationState,
    DateTimeOffset? ValidatedAt, DateTimeOffset? DiscoveredAt);

/// <summary>
/// Metadatos no secretos de las conexiones, por provider id, en
/// <c>(data)/provider-connections.json</c>: pistas enmascaradas y estado de la última
/// validación. Escritura atómica (temporal + rename); un archivo corrupto se trata como vacío.
/// </summary>
public static class ProviderConnectionMetadataStore
{
    private static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, "provider-connections.json");

    /// <summary>Estados persistentes permitidos; cualquier otra cosa no se guarda.</summary>
    internal static string? Normalize(string? validationState) => validationState switch
    {
        "valid" or "invalid" or "unreachable" => validationState,
        _ => null,
    };

    public static IReadOnlyDictionary<string, ProviderConnectionMetadata> ReadAll(string dataDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = PathFor(dataDirectory);
        if (!File.Exists(file)) return new Dictionary<string, ProviderConnectionMetadata>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var result = new Dictionary<string, ProviderConnectionMetadata>(StringComparer.Ordinal);
            foreach (var pair in document.RootElement.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.Object) continue;
                var node = pair.Value;
                DateTimeOffset? Stamp(string name) => node.TryGetProperty(name, out var stamp)
                    && stamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                result[pair.Name] = new(
                    node.TryGetProperty("maskedKey", out var masked) && masked.ValueKind == JsonValueKind.String
                        ? masked.GetString() : null,
                    node.TryGetProperty("validationState", out var state) && state.ValueKind == JsonValueKind.String
                        ? Normalize(state.GetString()) : null,
                    Stamp("validatedAt"), Stamp("discoveredAt"));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ProviderConnectionMetadata>(StringComparer.Ordinal);
        }
    }

    public static ProviderConnectionMetadata Read(string dataDirectory, string providerId,
        CancellationToken cancellationToken) =>
        ReadAll(dataDirectory, cancellationToken).TryGetValue(providerId, out var metadata)
            ? metadata : new(null, null, null, null);

    public static void Write(string dataDirectory, string providerId, ProviderConnectionMetadata metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var all = new Dictionary<string, ProviderConnectionMetadata>(ReadAll(dataDirectory, cancellationToken), StringComparer.Ordinal);
        all[providerId] = metadata;
        Directory.CreateDirectory(dataDirectory);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in all.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(pair.Key);
                writer.WriteString("maskedKey", pair.Value.MaskedKey);
                writer.WriteString("validationState", pair.Value.ValidationState);
                writer.WriteString("validatedAt", pair.Value.ValidatedAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("discoveredAt", pair.Value.DiscoveredAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        var temporary = PathFor(dataDirectory) + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, Encoding.UTF8.GetString(buffer.ToArray()));
        File.Move(temporary, PathFor(dataDirectory), overwrite: true);
    }

    /// <summary>Quita la entrada de un provider (desconexión); las demás se preservan.</summary>
    public static void Clear(string dataDirectory, string providerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(PathFor(dataDirectory))) return;
        var all = new Dictionary<string, ProviderConnectionMetadata>(ReadAll(dataDirectory, cancellationToken), StringComparer.Ordinal);
        if (!all.Remove(providerId)) return;
        Directory.CreateDirectory(dataDirectory);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in all.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(pair.Key);
                writer.WriteString("maskedKey", pair.Value.MaskedKey);
                writer.WriteString("validationState", pair.Value.ValidationState);
                writer.WriteString("validatedAt", pair.Value.ValidatedAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("discoveredAt", pair.Value.DiscoveredAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        var temporary = PathFor(dataDirectory) + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, Encoding.UTF8.GetString(buffer.ToArray()));
        File.Move(temporary, PathFor(dataDirectory), overwrite: true);
    }
}

/// <summary>
/// Backend de conexión de providers por API key (ADR-0011 §3, ADR-0018, ADR-0039 §4). El
/// renderer del menú solo presenta: el estado y todas las operaciones viven aquí, sobre el
/// credential store cifrado y la configuración User. Este bloque cubre la conexión API de
/// Anthropic (validación y descubrimiento contra la Models API pública, sin inferencia ni
/// costo); la sesión de suscripción se gestiona por el canal de cuentas.
/// </summary>
public sealed class ProviderConnectionService
{
    /// <summary>authRef por defecto del provider Anthropic; es la clave en el credential store.</summary>
    public const string AnthropicAuthRef = "anthropic";

    /// <summary>Id del provider Anthropic cuando el usuario aún no declaró uno.</summary>
    public const string DefaultAnthropicProviderId = "anthropic";

    private readonly ICredentialStore _store;
    private readonly IPlatformPaths _paths;
    private readonly AnthropicConnectionValidator _validator;
    private readonly AnthropicModelCatalog _catalog;
    private readonly Func<DateTimeOffset> _now;

    public ProviderConnectionService(ICredentialStore? store = null, IPlatformPaths? paths = null,
        Func<HttpClient>? httpFactory = null, Func<DateTimeOffset>? now = null)
    {
        _paths = paths ?? OmniHost.CreatePlatformPaths();
        _store = store ?? OmniHost.CreateUserCredentialStore(_paths);
        _validator = new AnthropicConnectionValidator(httpFactory);
        _catalog = new AnthropicModelCatalog(httpFactory);
        _now = now ?? DefaultNow;
    }

    private static DateTimeOffset DefaultNow() => DateTimeOffset.UtcNow;

    internal static bool IsAnthropicApiKeyProvider(ProviderDescriptor provider) =>
        provider.Family == ProviderFamily.AnthropicMessages
        && provider.Auth.Kind == AuthKind.ApiKey
        && !string.IsNullOrWhiteSpace(provider.Auth.SecretRef);

    /// <summary>Instancia de producción: rutas de plataforma reales del usuario.</summary>
    public static ProviderConnectionService Create() => new(paths: OmniHost.CreatePlatformPaths());

    /// <summary>
    /// Estado de todas las conexiones que la configuración User declara (más una fila sintética
    /// de Anthropic cuando todavía no está registrada). Sin secretos: solo presencia,
    /// método, estado medido y pista enmascarada.
    /// </summary>
    public IReadOnlyList<ProviderConnectionStatus> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var metadata = ProviderConnectionMetadataStore.ReadAll(_paths.DataDirectory, cancellationToken);
        var result = new List<ProviderConnectionStatus>();
        var providers = loaded.Registry.Providers();
        var anthropicConfigured = providers.Any(IsAnthropicApiKeyProvider);
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(provider.Profile, "codex", StringComparison.Ordinal))
            {
                // Suscripción ChatGPT: la mide el canal de cuentas, no este backend (ADR-0011 §3.4).
                result.Add(new(provider.Id, ProviderConnectionMethod.Subscription,
                    ProviderConnectionState.Unknown, provider.BillingMode, null, null,
                    CanConnect: false, CanTest: false, CanDisconnect: false, CanDiscoverModels: false));
                continue;
            }

            switch (provider.Auth.Kind)
            {
                case AuthKind.ApiKey:
                {
                    var present = CredentialPresent(provider.Auth.SecretRef!, cancellationToken);
                    ProviderConnectionState state;
                    LocalizedText? notice = null;
                    string? detail = null;
                    if (present)
                    {
                        var entry = metadata.TryGetValue(provider.Id, out var found)
                            ? found : new ProviderConnectionMetadata(null, null, null, null);
                        (state, notice) = entry.ValidationState switch
                        {
                            "valid" => (ProviderConnectionState.Connected, (LocalizedText?)null),
                            "invalid" => (ProviderConnectionState.Invalid,
                                LocalizedText.Of("providers.notice.storedKeyRejected")),
                            "unreachable" => (ProviderConnectionState.Unreachable,
                                LocalizedText.Of("providers.notice.storedKeyUnreachable")),
                            _ => (ProviderConnectionState.Unknown,
                                LocalizedText.Of("providers.notice.storedKeyUnverified")),
                        };
                        detail = entry.MaskedKey;
                    }
                    else
                    {
                        state = ProviderConnectionState.NotConfigured;
                        notice = LocalizedText.Of("providers.notice.missingKey", "ref", provider.Auth.SecretRef!);
                    }

                    var anthropic = IsAnthropicApiKeyProvider(provider);
                    result.Add(new(provider.Id, ProviderConnectionMethod.ApiKey, state, provider.BillingMode,
                        detail, notice,
                        CanConnect: anthropic,
                        CanTest: anthropic && present,
                        CanDisconnect: present,
                        CanDiscoverModels: anthropic && present));
                    break;
                }
                default:
                    // auth: none (p. ej. servidor local): no hay credencial que medir aquí.
                    result.Add(new(provider.Id, ProviderConnectionMethod.None, ProviderConnectionState.Unknown,
                        provider.BillingMode, null, null,
                        CanConnect: false, CanTest: false, CanDisconnect: false, CanDiscoverModels: false));
                    break;
            }
        }

        if (!anthropicConfigured)
        {
            var syntheticId = ProviderConnectionRegistration.FreeAnthropicProviderId(
                providers.Select(provider => provider.Id));
            result.Add(new(syntheticId, ProviderConnectionMethod.ApiKey,
                ProviderConnectionState.NotConfigured, BillingMode.Unknown, null, null,
                CanConnect: true, CanTest: false, CanDisconnect: false, CanDiscoverModels: false));
        }

        return result;
    }

    private bool CredentialPresent(string secretRef, CancellationToken cancellationToken)
    {
        try
        {
            return _store.Load(secretRef, cancellationToken) is not null;
        }
        catch (SecretValueTooShortException)
        {
            return true; // presente pero inválido: se puede reparar reemplazándola.
        }
    }

    /// <summary>
    /// Guarda o reemplaza la API key de Anthropic. La credencial entra por este canal (entrada
    /// privada del Host), se registra en el redactor (ADR-0018) y se persiste solo en el
    /// credential store cifrado: nunca en YAML, journal, logs ni estado. Con
    /// <paramref name="validate"/> la Models API debe aceptarla antes de guardar; una
    /// credencial rechazada no reemplaza la existente.
    /// </summary>
    public async Task<ProviderConnectResult> ConnectAnthropicAsync(string apiKey, bool validate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var provider = loaded.Registry.Providers().FirstOrDefault(IsAnthropicApiKeyProvider);
        var providerId = provider?.Id ?? ProviderConnectionRegistration.FreeAnthropicProviderId(
            loaded.Registry.Providers().Select(candidate => candidate.Id));
        return await ConnectAnthropicAsync(providerId, apiKey, validate, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Guarda la credencial en la referencia declarada por el provider Anthropic seleccionado.
    /// El id se resuelve y valida antes de cualquier escritura; una fila sintética solo puede
    /// materializarse si sigue siendo el id libre anunciado por <see cref="List"/>.
    /// </summary>
    public async Task<ProviderConnectResult> ConnectAnthropicAsync(string providerId, string apiKey,
        bool validate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = apiKey?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ProviderConnectionException("empty", "La API key está vacía.");
        }

        if (trimmed.Length < Secret.MinimumLength)
        {
            throw new ProviderConnectionException("tooShort",
                "La API key es demasiado corta (mínimo " + Secret.MinimumLength + " caracteres).");
        }

        var target = ResolveAnthropicTarget(providerId, cancellationToken);
        SecretRedactorRegistry.Register(trimmed);
        if (validate)
        {
            var result = await _validator.ValidateAsync(trimmed, target.BaseUrl, cancellationToken).ConfigureAwait(false);
            if (result.State == ProviderConnectionState.Invalid)
            {
                throw new ProviderConnectionException("invalid",
                    "La API de Anthropic rechazó la credencial (" + (result.Detail ?? "sin detalle")
                    + "). Nada se guardó.");
            }

            target = RevalidateAnthropicTarget(target, cancellationToken);
            SaveCredential(providerId, target.SecretRef, trimmed, result.State, cancellationToken);
            return new(result.State, Mask(trimmed), result.State == ProviderConnectionState.Connected
                ? LocalizedText.Of("providers.notice.connectedValidated")
                : LocalizedText.Of("providers.notice.savedUnverified"));
        }

        target = RevalidateAnthropicTarget(target, cancellationToken);
        SaveCredential(providerId, target.SecretRef, trimmed, null, cancellationToken);
        return new(ProviderConnectionState.Unknown, Mask(trimmed),
            LocalizedText.Of("providers.notice.savedUnverified"));
    }

    private sealed record AnthropicConnectTarget(string ProviderId, string BaseUrl, string SecretRef,
        bool RequiresRegistration);

    private AnthropicConnectTarget ResolveAnthropicTarget(string providerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ProviderConnectionException("unknownProvider", "Provider desconocido.");

        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var provider = loaded.Registry.Provider(providerId);
        if (provider is not null)
        {
            if (!IsAnthropicApiKeyProvider(provider))
            {
                throw new ProviderConnectionException("unsupported",
                    "El provider seleccionado no admite conexión Anthropic por API key.");
            }

            return new(provider.Id, provider.BaseUrl, provider.Auth.SecretRef!, RequiresRegistration: false);
        }

        var providers = loaded.Registry.Providers();
        if (providers.Any(IsAnthropicApiKeyProvider))
            throw new ProviderConnectionException("unknownProvider", "Provider desconocido: " + providerId);

        var syntheticId = ProviderConnectionRegistration.FreeAnthropicProviderId(
            providers.Select(candidate => candidate.Id));
        if (!string.Equals(providerId, syntheticId, StringComparison.Ordinal))
            throw new ProviderConnectionException("unknownProvider", "Provider desconocido: " + providerId);

        return new(providerId, AnthropicConnectionValidator.DefaultBaseUrl,
            ProviderConnectionRegistration.FreeAnthropicAuthRef(
                providers.Select(candidate => candidate.Auth.SecretRef)), RequiresRegistration: true);
    }

    private AnthropicConnectTarget RevalidateAnthropicTarget(AnthropicConnectTarget previous,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (previous.RequiresRegistration)
            ProviderConnectionRegistration.EnsureAnthropicProvider(_paths.ConfigDirectory,
                previous.ProviderId, previous.SecretRef);

        cancellationToken.ThrowIfCancellationRequested();
        var current = ResolveAnthropicTarget(previous.ProviderId, cancellationToken);
        if (!string.Equals(previous.BaseUrl, current.BaseUrl, StringComparison.Ordinal)
            || !string.Equals(previous.SecretRef, current.SecretRef, StringComparison.Ordinal))
        {
            throw new ProviderConnectionException("configurationChanged",
                "La configuración del provider cambió durante la conexión; no se guardó la credencial.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return current;
    }

    private void SaveCredential(string providerId, string secretRef, string apiKey,
        ProviderConnectionState? state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Save(secretRef, apiKey, cancellationToken);
        var previous = ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);
        ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId,
            new ProviderConnectionMetadata(Mask(apiKey), ProviderConnectionMetadataStore.Normalize(
                    state switch
                    {
                        ProviderConnectionState.Connected => "valid",
                        ProviderConnectionState.Invalid => "invalid",
                        ProviderConnectionState.Unreachable => "unreachable",
                        _ => null,
                    }),
                state == ProviderConnectionState.Connected ? _now() : previous.ValidatedAt,
                previous.DiscoveredAt),
            cancellationToken);
    }

    /// <summary>
    /// Prueba la conexión con la credencial guardada del provider (nunca con otra); para
    /// Anthropic usa la Models API pública. El estado solo se afirma con la medición: sin
    /// 2xx no se reporta Connected.
    /// </summary>
    public async Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken)
    {
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var provider = loaded.Registry.Provider(providerId);
        if (provider is null && string.Equals(providerId, DefaultAnthropicProviderId, StringComparison.Ordinal))
        {
            // Sin registro previo del usuario no hay credencial ni endpoint que probar.
            throw new ProviderConnectionException("notConfigured",
                "No hay provider Anthropic configurado todavía.");
        }

        provider = provider ?? throw new ProviderConnectionException("unknownProvider",
            "Provider desconocido: " + providerId);
        if (provider.Family != ProviderFamily.AnthropicMessages
            || provider.Auth.Kind != AuthKind.ApiKey)
        {
            throw new ProviderConnectionException("unsupported",
                "Este provider no se prueba con el backend de API key (suscripción o sin credencial).");
        }

        var apiKey = _store.Load(provider.Auth.SecretRef!, cancellationToken)
            ?? throw new ProviderConnectionException("notConfigured",
                "No hay credencial guardada para " + providerId + ".");
        var result = await _validator.ValidateAsync(apiKey, provider.BaseUrl, cancellationToken)
            .ConfigureAwait(false);
        var previous = Metadata(providerId, cancellationToken);
        ProviderConnectionMetadataStore.Write(_paths.DataDirectory, providerId,
            new ProviderConnectionMetadata(Mask(apiKey),
                ProviderConnectionMetadataStore.Normalize(result.State switch
                {
                    ProviderConnectionState.Connected => "valid",
                    ProviderConnectionState.Invalid => "invalid",
                    ProviderConnectionState.Unreachable => "unreachable",
                    _ => null,
                }),
                result.State == ProviderConnectionState.Connected ? _now() : previous.ValidatedAt,
                previous.DiscoveredAt),
            cancellationToken);
        return result;
    }

    /// <summary>
    /// Descubre los modelos actuales de Anthropic contra la Models API y los registra en la
    /// configuración User. Los límites que el API no informa quedan desconocidos: no se
    /// escriben medidas inventadas en el registro.
    /// </summary>
    public async Task<AnthropicDiscoveryResult> DiscoverAnthropicModelsAsync(string? providerId,
        CancellationToken cancellationToken)
    {
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var id = providerId ?? loaded.Providers?.Providers?.FirstOrDefault(pair =>
            pair.Value.Family == "AnthropicMessages").Key;
        var provider = id is null ? null : loaded.Registry.Provider(id);
        if (provider is null || provider.Family != ProviderFamily.AnthropicMessages
            || provider.Auth.Kind != AuthKind.ApiKey)
        {
            throw new ProviderConnectionException("notConfigured",
                "No hay provider Anthropic configurado con API key.");
        }

        var apiKey = _store.Load(provider.Auth.SecretRef!, cancellationToken)
            ?? throw new ProviderConnectionException("notConfigured",
                "Conecta primero la API key de Anthropic.");
        var (models, truncated, quota) = await _catalog.ListAsync(apiKey, provider.BaseUrl, cancellationToken)
            .ConfigureAwait(false);
        var registered = ProviderConnectionRegistration.RegisterAnthropicModels(
            _paths.ConfigDirectory, provider.Id, models);
        ProviderConnectionMetadataStore.Write(_paths.DataDirectory, provider.Id,
            Metadata(provider.Id, cancellationToken) with { DiscoveredAt = _now() }, cancellationToken);
        return new(provider.Id, registered, truncated, quota);
    }

    /// <summary>
    /// Desconecta una credencial: operación local que borra la entrada del credential store de
    /// ese provider y sus metadatos, preservando las credenciales de los demás providers y la
    /// configuración de modelos. Nunca toca credenciales de otros productos ni sesiones de
    /// suscripción.
    /// </summary>
    public void Disconnect(string providerId, CancellationToken cancellationToken)
    {
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        var provider = loaded.Registry.Provider(providerId)
            ?? throw new ProviderConnectionException("unknownProvider", "Provider desconocido: " + providerId);
        if (provider.Auth.Kind != AuthKind.ApiKey)
        {
            throw new ProviderConnectionException("unsupported",
                "Este provider no tiene credencial de API key que desconectar.");
        }

        _store.Delete(provider.Auth.SecretRef!, cancellationToken);
        ProviderConnectionMetadataStore.Clear(_paths.DataDirectory, providerId, cancellationToken);
    }

    private ProviderConnectionMetadata Metadata(string providerId, CancellationToken cancellationToken) =>
        ProviderConnectionMetadataStore.Read(_paths.DataDirectory, providerId, cancellationToken);

    /// <summary>Pista enmascarada para el usuario: nunca el valor ni un prefijo largo.</summary>
    internal static string Mask(string secret) =>
        secret.Length <= 4 ? "****" : "****" + secret[^4..];
}

/// <summary>
/// Registro User de la conexión Anthropic y de sus modelos descubiertos (ADR-0039 §4:
/// providers, baseUrl y authRef solo en scope User). Ambas escrituras siguen el patrón de
/// <see cref="ModelCatalogRegistration"/>: backup, escritura atómica, validación previa de
/// ambas YAML con <see cref="ConfigLoader"/> y detección de cambio concurrente. Los secretos
/// jamás tocan los YAML, y los modelos ya registrados del usuario se preservan tal cual.
/// </summary>
internal static class ProviderConnectionRegistration
{
    /// <summary>
    /// Garantiza que exista un provider AnthropicMessages en providers.yaml y devuelve
    /// (providerId, baseUrl). Si el usuario ya registró uno, se respeta su baseUrl y authRef.
    /// </summary>
    internal static (string ProviderId, string BaseUrl) EnsureAnthropicProvider(string directory,
        string? expectedProviderId = null, string? expectedSecretRef = null)
    {
        Directory.CreateDirectory(directory);
        var providersPath = Path.Combine(directory, "providers.yaml");
        var modelsPath = Path.Combine(directory, "models.yaml");
        var oldProviders = File.Exists(providersPath) ? File.ReadAllText(providersPath) : null;
        var oldModels = File.Exists(modelsPath) ? File.ReadAllText(modelsPath) : null;
        var loaded = new ConfigLoader().Load(oldProviders, oldModels);
        var existing = loaded.Registry.Providers()
            .FirstOrDefault(ProviderConnectionService.IsAnthropicApiKeyProvider)?.Id;
        if (existing is not null)
        {
            if (expectedProviderId is not null
                && !string.Equals(existing, expectedProviderId, StringComparison.Ordinal))
            {
                throw new ProviderConnectionException("configurationChanged",
                    "El provider Anthropic seleccionado cambió; no se guardó la credencial.");
            }

            var existingProvider = loaded.Registry.Provider(existing)!;
            if (expectedSecretRef is not null
                && !string.Equals(existingProvider.Auth.SecretRef, expectedSecretRef, StringComparison.Ordinal))
            {
                throw new ProviderConnectionException("configurationChanged",
                    "La referencia de credencial del provider Anthropic cambió; no se guardó la clave.");
            }

            return (existing, existingProvider.BaseUrl);
        }

        var providers = Parse(oldProviders ?? "providers:\n  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n");
        var providerMap = (YamlMappingNode)providers.Children[new YamlScalarNode("providers")];
        var providerId = FreeAnthropicProviderId(providerMap.Children.Keys
            .OfType<YamlScalarNode>().Select(key => key.Value ?? ""));
        if (expectedProviderId is not null
            && !string.Equals(providerId, expectedProviderId, StringComparison.Ordinal))
        {
            throw new ProviderConnectionException("configurationChanged",
                "El id disponible para Anthropic cambió; no se guardó la credencial.");
        }

        var secretRef = FreeAnthropicAuthRef(loaded.Registry.Providers()
            .Select(provider => provider.Auth.SecretRef));
        if (expectedSecretRef is not null
            && !string.Equals(secretRef, expectedSecretRef, StringComparison.Ordinal))
        {
            throw new ProviderConnectionException("configurationChanged",
                "La referencia libre para Anthropic cambió; no se guardó la credencial.");
        }

        providerMap.Add(providerId, new YamlMappingNode
        {
            { "family", "AnthropicMessages" },
            { "baseUrl", AnthropicConnectionValidator.DefaultBaseUrl },
            { "authRef", secretRef },
            { "billingMode", "MeteredCurrency" },
        });
        var newProviders = Save(providers);
        _ = new ConfigLoader().Load(newProviders, oldModels); // Validar antes de tocar nada.
        var suffix = ".backup-" + Guid.NewGuid().ToString("N");
        if (oldProviders is not null) File.Copy(providersPath, providersPath + suffix);
        if (Read(providersPath) != oldProviders) throw new IOException("Configuration changed concurrently.");
        try
        {
            WriteAtomic(providersPath, newProviders);
        }
        catch
        {
            if (Read(providersPath) == newProviders) Restore(providersPath, oldProviders);
            throw;
        }

        return (providerId, AnthropicConnectionValidator.DefaultBaseUrl);
    }

    /// <summary>Id libre para el provider Anthropic (respeta los ya declarados).</summary>
    internal static string FreeAnthropicProviderId(IEnumerable<string> existingProviderIds)
    {
        var existing = existingProviderIds.ToHashSet(StringComparer.Ordinal);
        var candidate = ProviderConnectionService.DefaultAnthropicProviderId;
        for (var suffix = 2; existing.Contains(candidate); suffix++)
        {
            candidate = ProviderConnectionService.DefaultAnthropicProviderId + "-" + suffix;
        }

        return candidate;
    }

    /// <summary>authRef libre para una nueva credencial; existentes nunca se reasignan.</summary>
    internal static string FreeAnthropicAuthRef(IEnumerable<string?> existingSecretRefs)
    {
        var existing = existingSecretRefs.Where(reference => !string.IsNullOrWhiteSpace(reference))
            .ToHashSet(StringComparer.Ordinal);
        var candidate = ProviderConnectionService.AnthropicAuthRef;
        for (var suffix = 2; existing.Contains(candidate); suffix++)
            candidate = ProviderConnectionService.AnthropicAuthRef + "-" + suffix;
        return candidate;
    }

    /// <summary>
    /// Registra los modelos descubiertos bajo el provider dado. Solo se escriben hechos
    /// reportados por la Models API; el contexto y el límite de salida que el API no informa
    /// NO se rellenan con defaults (quedan al default conservador del loader, sin afirmarse
    /// como medida). Los modelos ya registrados se preservan intactos.
    /// </summary>
    internal static int RegisterAnthropicModels(string directory, string providerId,
        IReadOnlyList<AvailableAnthropicModel> catalog)
    {
        Directory.CreateDirectory(directory);
        var providersPath = Path.Combine(directory, "providers.yaml");
        var modelPath = Path.Combine(directory, "models.yaml");
        var oldProviders = File.Exists(providersPath) ? File.ReadAllText(providersPath) : null;
        var oldModels = File.Exists(modelPath) ? File.ReadAllText(modelPath) : null;
        var loaded = new ConfigLoader().Load(oldProviders, oldModels);
        if (loaded.Registry.Provider(providerId) is null)
        {
            EnsureAnthropicProvider(directory);
            oldProviders = File.ReadAllText(providersPath);
            loaded = new ConfigLoader().Load(oldProviders, oldModels);
        }

        var models = Parse(oldModels
            ?? (oldProviders is null
                ? "models:\n  local-worker: { provider: local, context: 8192, maxOutput: 2048 }\n"
                : "models: {}\n"));
        var modelMap = (YamlMappingNode)models.Children[new YamlScalarNode("models")];
        var added = 0;
        foreach (var model in catalog)
        {
            var registered = loaded.Registry.Model(model.Id);
            if (registered is not null)
            {
                if (registered.ProviderId != providerId)
                {
                    throw new ProviderConnectionException("modelConflict",
                        "El modelo " + model.Id + " ya está registrado con otro provider.");
                }

                continue; // Preserva los límites, alias, routing y cualificación del usuario.
            }

            var entry = new YamlMappingNode { { "provider", providerId } };
            if (model.ThinkingSupported is true)
            {
                entry.Add("reasoning", new YamlMappingNode
                {
                    { "supported", new YamlScalarNode("true") },
                    { "effortLevels", new YamlSequenceNode(new[] { new YamlScalarNode("budget") }) },
                });
            }

            modelMap.Add(model.Id, entry);
            added++;
        }

        if (added == 0) return 0;
        var newModels = Save(models);
        _ = new ConfigLoader().Load(oldProviders, newModels); // Validar antes de tocar nada.
        var suffix = ".backup-" + Guid.NewGuid().ToString("N");
        if (oldModels is not null) File.Copy(modelPath, modelPath + suffix);
        if (Read(modelPath) != oldModels) throw new IOException("Configuration changed concurrently.");
        try
        {
            WriteAtomic(modelPath, newModels);
        }
        catch
        {
            if (Read(modelPath) == newModels) Restore(modelPath, oldModels);
            throw;
        }

        return added;
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static void Restore(string path, string? text)
    {
        if (text is null) File.Delete(path);
        else WriteAtomic(path, text);
    }

    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static YamlMappingNode Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Save(YamlMappingNode root)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }
}
