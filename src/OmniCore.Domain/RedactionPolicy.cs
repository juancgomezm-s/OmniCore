namespace OmniCore.Domain;

using System.Text.RegularExpressions;

/// <summary>
/// Redactor de PII para diagnóstico y auditoría (ADR-0018 §4): elimina o enmascara secretos y
/// datos de autenticación antes de tocar logs, journal de diagnóstico, artifacts, contexto,
/// errores, audit o tool results. La redacción es determinista y no reversible.
/// </summary>
public sealed class PiiRedactor
{
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

    /// <summary>Enmascara el texto; sustituye los secretos por [REDACTED].</summary>
    public string Redact(string input)
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

    /// <summary>Redacta contenido (keys, bearer, JWT, cookies) de forma determinista.</summary>
    public string Redact(string input) => _redactor.Redact(input);
}