using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;

namespace OmniCore.Models;

/// <summary>
/// Persistencia de la credencial OAuth de Claude. Dos destinos separados a propósito:
///
/// <list type="bullet">
/// <item>Los tokens van al <see cref="ICredentialStore"/> cifrado (ADR-0018), bajo una clave que
/// el usuario puede tener en <c>providers.yaml</c>.</item>
/// <item>La identidad no secreta (plan, tier, email, fechas) va a un JSON aparte, para que /doctor
/// y la status line puedan leerse sin descifrar nada (ADR-0031, ADR-0046).</item>
/// </list>
///
/// Un fallo entre los dos writes deja la metadata desactualizada pero nunca revela un secreto:
/// la metadata se sobreescribe siempre desde lo último descifrado, así que convergen.
/// </summary>
public sealed class ClaudeOAuthCredentialStore
{
    /// <summary>Prefijo de la clave del almacén cifrado. Evita chocar con secret refs de API key.</summary>
    public const string CredentialKeyPrefix = "oauth:claude:";

    private readonly ICredentialStore _store;
    private readonly string _metadataPath;
    public ClaudeOAuthCredentialStore(ICredentialStore store, string metadataPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _metadataPath = string.IsNullOrWhiteSpace(metadataPath)
            ? throw new ArgumentException("Falta la ruta de metadata.", nameof(metadataPath))
            : metadataPath;
    }

    public static string CredentialKey(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        return CredentialKeyPrefix + secretRef;
    }

    /// <summary>
    /// Guarda la credencial y actualiza la metadata no secreta.
    ///
    /// La confidencialidad la pone el <see cref="ICredentialStore"/> que se inyecte — en
    /// producción <c>FileCredentialStore</c>, que cifra con DPAPI/AES-GCM antes de tocar disco
    /// (ADR-0018). Esta capa añade una segunda barrera: los tokens viajan bajo una clave propia
    /// (<see cref="CredentialKey"/>) separada de las secret refs de API key, para que un provider
    /// mal configurado no pueda leer accidentalmente un credential OAuth.
    /// </summary>
    public void Save(string secretRef, ClaudeOAuthCredential credential, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentNullException.ThrowIfNull(credential);

        var series = JsonSerializer.Serialize(ToWire(credential), ClaudeOAuthStoreJsonContext.Default.ClaudeOAuthCredentialWire);
        // Se registran las piezas ya aqui: un login que termine bien no puede depender de que
        // alguien llame a Load mas tarde para que los tokens sean redactables.
        RegisterForRedaction(series);
        var key = CredentialKey(secretRef);
        var previous = _store.Load(key, ct);
        try
        {
            _store.Save(key, series, ct);
            WriteMetadata(secretRef, credential, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Compensation must finish even when the caller's token is cancelled.
            if (previous is null) _store.Delete(key, CancellationToken.None);
            else _store.Save(key, previous, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Lee la credencial descifrándola. Devuelve null si no hay o si no se puede interpretar —
    /// nunca lanza por un credential ausente: el llamador decide si pide login.
    /// Todo valor devuelto pasa por SecretRedactorRegistry antes de salir del almacén (ADR-0018).
    /// </summary>
    public ClaudeOAuthCredential? Load(string secretRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        var stored = _store.Load(CredentialKey(secretRef), ct);
        if (stored is null)
        {
            return null;
        }

        // Tambien al leer: otro proceso pudo escribir el credential despues de nuestro ultimo
        // Save, y sus tokens aun no estan registrados en este proceso.
        RegisterForRedaction(stored);

        ClaudeOAuthCredentialWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(stored, ClaudeOAuthStoreJsonContext.Default.ClaudeOAuthCredentialWire);
        }
        catch (JsonException)
        {
            // Almacén manipulado o serie de otra versión: se trata como ausente, igual que hace
            // FileCredentialStore con un blob que no puede descifrar.
            return null;
        }

        if (wire is null || wire.SchemaVersion != ClaudeOAuthCredential.CurrentSchemaVersion)
        {
            return null;
        }

        if (string.IsNullOrEmpty(wire.AccessToken) || string.IsNullOrEmpty(wire.RefreshToken)
            || wire.ExpiresAtUnix <= 0 || string.IsNullOrEmpty(wire.ClientId))
        {
            return null;
        }

        var scopes = wire.Scopes?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];
        var metadata = ReadAccountInfo(secretRef, ct);
        return new ClaudeOAuthCredential(
            wire.AccessToken,
            wire.RefreshToken,
            DateTimeOffset.FromUnixTimeSeconds(wire.ExpiresAtUnix),
            scopes,
            wire.ClientId)
        {
            AccountUuid = wire.AccountUuid,
            OrganizationUuid = wire.OrganizationUuid,
            EmailAddress = wire.EmailAddress ?? metadata?.EmailAddress, DisplayName = wire.DisplayName ?? metadata?.DisplayName,
            SubscriptionType = wire.SubscriptionType ?? metadata?.SubscriptionType, RateLimitTier = wire.RateLimitTier ?? metadata?.RateLimitTier,
            Roles = wire.Roles,
            AuthenticatedAt = wire.AuthenticatedAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(wire.AuthenticatedAtUnix)
                : default,
        };
    }

    /// <summary>Borra la credencial y su metadata (logout).</summary>
    public void Delete(string secretRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        _store.Delete(CredentialKey(secretRef), ct);
        DeleteMetadata(secretRef, ct);
    }

    public bool Exists(string secretRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        return _store.Load(CredentialKey(secretRef), ct) is not null;
    }

    /// <summary>
    /// Marca de tiempo del archivo de metadata. El coordinador de refresh la usa para detectar
    /// escrituras de otro proceso sin descifrar nada (plan §5, refreshTokenDeadSet.ts).
    /// Null si el archivo no existe.
    /// </summary>
    public DateTimeOffset? MetadataWrittenAt(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(_metadataPath))
            {
                return null;
            }

            var universal = File.GetLastWriteTimeUtc(_metadataPath);
            return new DateTimeOffset(universal, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Metadata no secreta -------------------------------------------------------------

    /// <summary>Estado de cuenta legible sin descifrar el credential (para /doctor y la UI).</summary>
    public ClaudeOAuthAccountInfo? ReadAccountInfo(string secretRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        var all = ReadAllMetadata(ct);
        return all.TryGetValue(secretRef, out var info) ? info : null;
    }

    private Dictionary<string, ClaudeOAuthAccountInfo> ReadAllMetadata(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(_metadataPath))
            {
                return new Dictionary<string, ClaudeOAuthAccountInfo>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(_metadataPath);
            var map = JsonSerializer.Deserialize(json,
                ClaudeOAuthStoreJsonContext.Default.DictionaryStringClaudeOAuthAccountInfoWire);
            if (map is null)
            {
                return new Dictionary<string, ClaudeOAuthAccountInfo>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, ClaudeOAuthAccountInfo>(StringComparer.Ordinal);
            foreach (var (key, value) in map)
            {
                if (value is not null)
                {
                    result[key] = value.ToInfo();
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Archivo corrupto o parcialmente escrito: se trata como vacío, igual que
            // ProviderConnectionMetadataStore. Nunca se propaga un fallo de metadata sobre uno
            // de credenciales.
            return new Dictionary<string, ClaudeOAuthAccountInfo>(StringComparer.Ordinal);
        }
    }

    private void WriteMetadata(string secretRef, ClaudeOAuthCredential credential, CancellationToken ct)
    {
        var all = ReadAllMetadata(ct);
        all[secretRef] = new ClaudeOAuthAccountInfo(
            EmailAddress: credential.EmailAddress,
            DisplayName: credential.DisplayName,
            SubscriptionType: credential.SubscriptionType,
            RateLimitTier: credential.RateLimitTier,
            Scopes: credential.Scopes,
            AuthenticatedAt: credential.AuthenticatedAt,
            ExpiresAt: credential.ExpiresAt,
            AccountUuid: credential.AccountUuid);
        AtomicWrite(SerializeMetadata(all, ct), ct);
    }

    private void DeleteMetadata(string secretRef, CancellationToken ct)
    {
        var all = ReadAllMetadata(ct);
        if (all.Remove(secretRef))
        {
            AtomicWrite(SerializeMetadata(all, ct), ct);
        }
    }

    /// <summary>
    /// Escritura atómica (temporal + rename), como ProviderConnectionMetadataStore. Sin esto, un
    /// crash a mitad dejaba metadata ilegible y /doctor mentía.
    /// </summary>
    private string SerializeMetadata(Dictionary<string, ClaudeOAuthAccountInfo> all, CancellationToken ct)
    {
        // La metadata se reescribe siempre desde lo ultimo descifrado: si un proceso externo
        // escribio entre nuestro load y nuestro write, pierde su fila pero no revela nada.
        ct.ThrowIfCancellationRequested();
        var wires = new Dictionary<string, ClaudeOAuthAccountInfoWire>(StringComparer.Ordinal);
        foreach (var (key, info) in all)
        {
            wires[key] = ClaudeOAuthAccountInfoWire.From(info);
        }

        return JsonSerializer.Serialize(wires,
            ClaudeOAuthStoreJsonContext.Default.DictionaryStringClaudeOAuthAccountInfoWire);
    }

    private static readonly Encoding MetadataEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private void AtomicWrite(string json, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(Path.GetFullPath(_metadataPath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
#if NET10_0_OR_GREATER
            {
                stream.Write(MetadataEncoding.GetBytes(json));
                stream.Flush(flushToDisk: true);
#else
            {
                var bytes = MetadataEncoding.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
#endif
            }

            File.Move(temp, _metadataPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ClaudeOAuthCredentialWire ToWire(ClaudeOAuthCredential credential) => new()
    {
        SchemaVersion = ClaudeOAuthCredential.CurrentSchemaVersion,
        AccessToken = credential.AccessToken,
        RefreshToken = credential.RefreshToken,
        ExpiresAtUnix = credential.ExpiresAt.ToUnixTimeSeconds(),
        ClientId = credential.ClientId,
        Scopes = credential.Scopes.ToArray(),
        AccountUuid = credential.AccountUuid,
        OrganizationUuid = credential.OrganizationUuid,
        EmailAddress = credential.EmailAddress, DisplayName = credential.DisplayName,
        SubscriptionType = credential.SubscriptionType, RateLimitTier = credential.RateLimitTier,
        Roles = credential.Roles,
        AuthenticatedAtUnix = credential.AuthenticatedAt == default ? 0 : credential.AuthenticatedAt.ToUnixTimeSeconds(),
    };

    /// <summary>
    /// Registra cada secreto del credential en el redactor del proceso. ICredentialStore registra
    /// el valor opaco que guarda —aqui una serie con ambos tokens— y Redact busca ese valor
    /// completo como substring: sin este paso, un log que imprima solo el access token lo filtriaria.
    /// El registry acumula, asi que registrar antes de que el host instale su redactor no pierde
    /// el valor (INV-016, ADR-0018).
    /// </summary>
    private static void RegisterForRedaction(string series)
    {
        ClaudeOAuthCredentialWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(series, ClaudeOAuthStoreJsonContext.Default.ClaudeOAuthCredentialWire);
        }
        catch (JsonException)
        {
            return;
        }

        if (wire is null)
        {
            return;
        }

        foreach (var secret in new[] { wire.AccessToken, wire.RefreshToken })
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= Secret.MinimumLength)
            {
                SecretRedactorRegistry.Register(secret);
            }
        }
    }
}

/// <summary>Forma persistida del credential. Solo lo necesario para reconstruirlo.</summary>
internal sealed class ClaudeOAuthCredentialWire
{
    public string? EmailAddress { get; set; }
    public string? DisplayName { get; set; }
    public string? SubscriptionType { get; set; }
    public string? RateLimitTier { get; set; }
    public ClaudeOAuthRoles? Roles { get; set; }
    public int SchemaVersion { get; set; }

    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    /// <summary>Epoch seconds UTC. Una fecha absoluta serializada en texto es frágil entre culturas.</summary>
    public long ExpiresAtUnix { get; set; }

    public string ClientId { get; set; } = "";

    public string[]? Scopes { get; set; }

    public string? AccountUuid { get; set; }

    public string? OrganizationUuid { get; set; }

    public long AuthenticatedAtUnix { get; set; }
}



internal sealed class ClaudeOAuthAccountInfoWire
{
    public string? EmailAddress { get; set; }

    public string? DisplayName { get; set; }

    public string? SubscriptionType { get; set; }

    public string? RateLimitTier { get; set; }

    public string[]? Scopes { get; set; }

    public long AuthenticatedAtUnix { get; set; }

    public long ExpiresAtUnix { get; set; }

    public string? AccountUuid { get; set; }

    public static ClaudeOAuthAccountInfoWire From(ClaudeOAuthAccountInfo info) => new()
    {
        EmailAddress = info.EmailAddress,
        DisplayName = info.DisplayName,
        SubscriptionType = info.SubscriptionType,
        RateLimitTier = info.RateLimitTier,
        Scopes = info.Scopes.ToArray(),
        AuthenticatedAtUnix = info.AuthenticatedAt == default ? 0 : info.AuthenticatedAt.ToUnixTimeSeconds(),
        ExpiresAtUnix = info.ExpiresAt == default ? 0 : info.ExpiresAt.ToUnixTimeSeconds(),
        AccountUuid = info.AccountUuid,
    };

    public ClaudeOAuthAccountInfo ToInfo() => new(
        EmailAddress,
        DisplayName,
        SubscriptionType,
        RateLimitTier,
        Scopes ?? [],
        AuthenticatedAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(AuthenticatedAtUnix) : default,
        ExpiresAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix) : default,
        AccountUuid);
}
/// <summary>
/// Estado de cuenta de Claude legible sin secretos ni descifrado. Es lo que consume /doctor y la
/// status line; todo campo nullable significa "no informado", nunca "estimado" (ADR-0031).
/// </summary>
public sealed record ClaudeOAuthAccountInfo(
    string? EmailAddress,
    string? DisplayName,
    string? SubscriptionType,
    string? RateLimitTier,
    IReadOnlyList<string> Scopes,
    DateTimeOffset AuthenticatedAt,
    DateTimeOffset ExpiresAt,
    string? AccountUuid)
{
    /// <summary>Máscara presentable del access token, para diagnosticar sin revelarlo.</summary>
    public string? MaskedAccessTokenHint(string? accessToken) => ClaudeOAuthMask.Mask(accessToken);

    public bool HasInferenceScope =>
        Scopes.Contains(ClaudeOAuthScopes.Inference, StringComparer.Ordinal);
}

/// <summary>Enmascarado de secretos para presentación (nunca para persistencia).</summary>
public static class ClaudeOAuthMask
{
    /// <summary>
    /// Deja suficientemente visible para distinguir dos credenciales y nada más. Mismo criterio
    /// que ProviderConnectionService.Mask para las API keys.
    /// </summary>
    public static string? Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        if (secret.Length <= 8)
        {
            return new string('*', secret.Length);
        }

        return secret[..4] + "…" + secret[^4..];
    }
}

/// <summary>
/// Contexto source-gen de la persistencia de OAuth. Bajo los analizadores AOT del proyecto
/// (ADR-0038 §5) nada se serializa por reflexión: cada forma persistida va declarada aquí.
/// Los nombres salen snake_case con el policy explícito, no por convención de C#.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaudeOAuthCredentialWire))]
[JsonSerializable(typeof(ClaudeOAuthAccountInfoWire))]
[JsonSerializable(typeof(Dictionary<string, ClaudeOAuthAccountInfoWire>))]
internal partial class ClaudeOAuthStoreJsonContext : JsonSerializerContext;
