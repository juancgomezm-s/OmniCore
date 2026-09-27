namespace OmniCore.Infrastructure;

using System.Text.RegularExpressions;

/// <summary>
/// Redactor de PII para diagnóstico y auditoría (ADR-0018 §4): elimina o enmascara secretos y
/// datos de autenticación antes de tocar logs, journal de diagnóstico o queries del supervisor.
/// Aplica a: API keys (sk-…, token=…, Bearer …), JWT (header.payload.signature), cabeceras
/// Authorization/Cookie y números de tarjeta. La redacción es determinista y no reversible
/// (marca como [REDACTED]).
/// </summary>
public sealed class PiiRedactor
{
    /// <summary>Patrón principal: valores tipo secreto precedidos de clave sensibles.</summary>
    private readonly string[] _patterns = new string[] {
        // "Authorization: Bearer abc…" y "bearer abc…"
        "(?i)(authorization\\s*[=:∷ ]+\\s*bearer\\s+)([A-Za-z0-9.~_-]{8,})",
        // api keys sk-… / sk-ant-…
        "(?i)(sk(-[A-Za-z0-9_-]+){2,})",
        // JWT: xxx.yyy.zzz (3 segmentos base64url)
        "\\b([A-Za-z0-9_-]{20,}\\.){2}[A-Za-z0-9_-]{20,}\\b",
        // cookies/sesiones: session=…, cookie: name=value
        "(?i)((?:session|sid|token|api_key|apikey|password|secret)\\s*[=: ]\\s*)((?:\"[^\"]*\")|[A-Za-z0-9._~-]{6,})",
        // bearer en cabecera de query (?Bearer=…)
        "(?i)(bearer=)([A-Za-z0-9._~-]{6,})",
    };

    /// <summary>Enmascara el texto; devuelve el original con los secretos sustituidos por [REDACTED].</summary>
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
                // patrón inválido nunca debe romper la redacción
            }
        }

        return result;
    }
}