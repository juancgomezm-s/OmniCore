using OmniCore.Models;

namespace OmniCore.Host;

/// <summary>
/// Convierte la sección <c>oauth:</c> de providers.yaml en una identidad de cliente usable.
///
/// Los valores los elige el usuario (ADR-0039 §2): el runtime no tiene identidad de cliente
/// propia ni defaults, y ninguna pieza de Models importa qué client id, user-agent o scopes se
/// declararon (INV-007). Un repo nunca configura esto — solo se lee del directorio de
/// configuración del usuario (INV-029).
///
/// La forma del YAML la valida <see cref="ConfigLoader"/> (claves conocidas, tipos escalares);
/// aquí va el contenido: URLs absolutas https sin userinfo, placeholder de versión presente y
/// scopes sin duplicados. Separar ambas capas es lo que permite que un typo en el client id
/// falle con diagnóstico en vez de degradar en silencio a AuthKind.ApiKey.
/// </summary>
public static class ClaudeOAuthConfiguration
{
    /// <summary>
    /// Construye la identidad desde el DTO ya parseado. Null si el provider no declara OAuth.
    /// </summary>
    /// <exception cref="ConfigValidationException">La sección existe pero su contenido es
    /// inválido: se lanza en vez de ignorarla, porque silenciarla dejaría al usuario con un
    /// login que parece de cuenta y gasta API key.</exception>
    public static ClaudeOAuthClientIdentity? TryBuild(
        string providerId,
        ProviderOAuthYaml? oauth,
        List<ConfigDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (oauth is null)
        {
            return null;
        }

        // El contenido ya lo valido ConfigLoader al cargar providers.yaml: si llegamos aqui con
        // un OAuth declarado, la seccion es sana. Se vuelve a comprobar lo minimo (presencia)
        // porque TryBuild es publico y alguien puede llamarlo con un DTO construido a mano.
        var path = "providers." + providerId + ".oauth";
        var clientId = RequirePresent(oauth.ClientId, path + ".clientId", diagnostics);
        var userAgent = RequirePresent(oauth.UserAgent, path + ".userAgent", diagnostics);
        var authorizeUrl = RequirePresent(oauth.AuthorizeUrl, path + ".authorizeUrl", diagnostics);
        var tokenUrl = RequirePresent(oauth.TokenUrl, path + ".tokenUrl", diagnostics);
        IReadOnlyList<string> scopes = oauth.Scopes is { Count: > 0 } declared
            ? declared.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();
        if (scopes.Count == 0)
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path + ".scopes", "config.emptyCollection");
        }

        if (diagnostics.Count != 0)
        {
            throw new ConfigValidationException(diagnostics);
        }

        return new ClaudeOAuthClientIdentity(clientId, userAgent, scopes, authorizeUrl, tokenUrl)
        {
            ProfileUrl = oauth.ProfileUrl is { Length: > 0 } profile ? profile.Trim() : null,
            RolesUrl = oauth.RolesUrl is { Length: > 0 } roles ? roles.Trim() : null,
        };
    }

    private static string RequirePresent(string? value, string path, List<ConfigDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.missingRequired");
        return string.Empty;
    }

    /// <summary>Clave bajo la que se guarda el credential cifrado del provider.</summary>
    public static string SecretRefFor(string providerId, ProviderOAuthYaml? oauth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return oauth?.SecretRef is { Length: > 0 } declared ? declared.Trim() : DefaultSecretRef(providerId);
    }

    public static string DefaultSecretRef(string providerId) => providerId + "-oauth";

    private static string Require(string? value, string path, List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.missingRequired");
            return string.Empty;
        }

        var trimmed = value.Trim();

        // Una URL declarada aqui acaba en el wire del authorize y del token: se valida la forma
        // antes de que nadie la use, no en el momento del exchange (plan §6).
        if (path.EndsWith("Url", StringComparison.Ordinal))
        {
            RequireHttpsUrl(trimmed, path, diagnostics);
        }

        return trimmed;
    }

    private static IReadOnlyList<string> RequireScopes(
        List<string>? scopes, string path, List<ConfigDiagnostic> diagnostics)
    {
        if (scopes is null || scopes.Count == 0)
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.emptyCollection");
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.emptyScalar");
                continue;
            }

            if (!seen.Add(scope.Trim()))
            {
                ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.duplicateItem");
            }
        }

        return [.. seen];
    }

    private static void RequireHttpsUrl(string value, string path, List<ConfigDiagnostic> diagnostics)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.invalidUrl");
            return;
        }

        // Solo https y sin userinfo: por aqui viajan el authorization code y el refresh token, y
        // un esquema o un host sorpresa los entregaría a otro sitio (plan §6).
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.oauth.httpsRequired");
            return;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            ConfigLoader.Add(diagnostics, "providers.yaml", path, "config.oauth.noUserInfoInUrl");
        }
    }
}
