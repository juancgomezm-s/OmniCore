namespace OmniCore.Domain;

using System.Text.RegularExpressions;

/// <summary>
/// Redactor de PII para diagnóstico y auditoría (ADR-0018 §4): elimina o enmascara secretos y
/// datos de autenticación antes de tocar logs, journal de diagnóstico, artifacts, contexto,
/// errores, audit o tool results. La redacción es determinista y no reversible.
/// </summary>
public sealed class PiiRedactor
{
    private static readonly string[] StreamingCredentialMarkers =
        ["authorization", "bearer", "api_key", "apikey", "password", "secret", "session", "token", "sid", "sk-"];
    private static Func<string, string>? _additionalRedactor;

    private readonly string[] _patterns = new string[] {
        // "Authorization: Bearer abc…" y cabeceras similares
        "(?i)(authorization\\s*[=: ]+\\s*bearer\\s+)([A-Za-z0-9.~_-]{8,})",
        // "Bearer <token>" simple (cabecera en logs/tool results)
        "(?i)(\\bbearer\\s+)([A-Za-z0-9._~-]{6,})",
        // API keys sk-… / sk-ant-…
        "(?i)(sk(-[A-Za-z0-9_-]+){2,})",
        // JWT: xxx.yyy.zzz (3 segmentos base64url)
        "\\b([A-Za-z0-9_-]{20,}\\.){2}[A-Za-z0-9_-]{20,}\\b",
        // cookies/sesiones/tokens
        "(?i)((?:session|sid|token|api_key|apikey|password|secret|authorization)\\s*[=: ]\\s*)((?:\"[^\"]*\")|[A-Za-z0-9._~-]{6,})",
        // bearer en query (?Bearer=…)
        "(?i)(bearer=)([A-Za-z0-9._~-]{6,})",
    };

    /// <summary>Instala desde Security la redacción de valores conocidos.</summary>
    public static void SetAdditionalRedactor(Func<string, string> redactor) =>
        System.Threading.Interlocked.Exchange(ref _additionalRedactor,
            redactor ?? throw new ArgumentNullException(nameof(redactor)));

    /// <summary>Enmascara el texto; sustituye los secretos por [REDACTED].</summary>
    public string Redact(string input)
    {
        var safe = RedactPatternsOnly(input);
        var additional = System.Threading.Volatile.Read(ref _additionalRedactor);
        return additional is null ? safe : additional(safe);
    }

    /// <summary>Aplica únicamente los patrones locales, sin delegar en valores conocidos.</summary>
    public string RedactPatternsOnly(string input)
    {
        if (input is null || input.Length == 0)
        {
            return input ?? "";
        }

        var result = input;
        for (var i = 0; i < _patterns.Length; i++)
        {
            try
            {
                result = new Regex(_patterns[i]).Replace(result, "[REDACTED]");
            }
            catch (Exception)
            {
                // un patrón inválido nunca rompe la redacción
            }
        }

        return result;
    }

    /// <summary>Retiene sólo el sufijo que aún puede completar un patrón de credencial.</summary>
    public static int PotentialCredentialSuffixStart(string input)
    {
        if (string.IsNullOrEmpty(input)) return input?.Length ?? 0;
        var searchStart = Math.Max(0, input.Length - 512);
        var holdStart = input.Length;
        foreach (var marker in StreamingCredentialMarkers)
        {
            var markerStart = Math.Max(searchStart, input.Length - marker.Length);
            for (var start = markerStart; start < input.Length; start++)
            {
                var suffixLength = input.Length - start;
                if (suffixLength < marker.Length
                    && marker.AsSpan(0, suffixLength).Equals(input.AsSpan(start), StringComparison.OrdinalIgnoreCase))
                {
                    holdStart = Math.Min(holdStart, start);
                    continue;
                }
            }

            var index = input.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < searchStart) continue;
            var afterMarker = index + marker.Length;
            var remainder = input.AsSpan(afterMarker);
            var separator = 0;
            while (separator < remainder.Length && separator < 8
                && (char.IsWhiteSpace(remainder[separator]) || remainder[separator] is ':' or '=' or '"' or '\''))
                separator++;
            var tokenStart = afterMarker + separator;
            var isAuthorizationHeader = marker.Equals("authorization", StringComparison.OrdinalIgnoreCase);
            if (isAuthorizationHeader && tokenStart < input.Length
                && input.AsSpan(tokenStart).StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
            {
                tokenStart += "bearer ".Length;
                while (tokenStart < input.Length && char.IsWhiteSpace(input[tokenStart])) tokenStart++;
            }
            if (tokenStart == input.Length)
            {
                holdStart = Math.Min(holdStart, index);
            }
            else
            {
                var tokenEnd = tokenStart;
                while (tokenEnd < input.Length && IsCredentialTokenCharacter(input[tokenEnd])) tokenEnd++;
                if (tokenEnd == input.Length) holdStart = Math.Min(holdStart, index);
            }
        }
        return holdStart;
    }

    private static bool IsCredentialTokenCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '.' or '_' or '~' or '-';
}

/// <summary>
/// Política de redacción obligatoria (ADR-0018 §4): rutas de archivos que nunca se leen ni se
/// exponen (secretos de entorno, PEM/SSH, credenciales) y contenido redactado antes de
/// persistir en journal, artifacts, contexto, errores, audit o tool results.
/// </summary>
public sealed class RedactionPolicy
{
    private readonly string[] _secretPathPatterns = new string[] {
        ".env", ".npmrc", ".pypirc", ".netrc", ".aws/credentials", "aws/credentials",
        "id_rsa", "id_ecdsa", "id_ed25519", ".ssh/", "*.pem", "*.key", "*.ppk",
        "kubeconfig", "k8s/config", "credentials.json", "service_account.json",
        "secrets.json", "passwords.txt", "appsettings.Production.json",
    };

    private readonly PiiRedactor _redactor = new();

    /// <summary>True si una ruta (normalizada) coincide con un patrón de secreto.</summary>
    public bool IsSecretPath(string path)
    {
        if (path is null || path.Length == 0)
        {
            return false;
        }

        var norm = path.Replace('\\', '/').ToLowerInvariant();
        var filename = norm;
        var slash = norm.LastIndexOf('/');
        if (slash >= 0)
        {
            filename = norm.Substring(slash + 1);
        }

        for (var i = 0; i < _secretPathPatterns.Length; i++)
        {
            // Comparación sin distinguir mayúsculas: la ruta ya está en minúsculas, el patrón
            // también debe estarlo (p. ej. "appsettings.Production.json").
            var pattern = _secretPathPatterns[i].ToLowerInvariant();
            if (pattern.StartsWith("*."))
            {
                var ext = pattern.Substring(1);
                if (filename.EndsWith(ext, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (pattern.EndsWith("/"))
            {
                // Un directorio de secretos protege todo lo que cuelga de él, esté donde esté
                // dentro de la ruta (".ssh/", "home/u/.ssh/…").
                if (norm.StartsWith(pattern, StringComparison.Ordinal)
                    || norm.Contains("/" + pattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (norm.Contains(pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Redacta contenido (keys, bearer, JWT, cookies y valores conocidos).</summary>
    public string Redact(string input) => _redactor.Redact(input);
}
