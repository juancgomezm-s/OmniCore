namespace OmniCore.Qualification;

using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text;
using OmniCore.Domain;

/// <summary>
/// Puntuador determinista de probes (ADR-0007 §6, M5). No usa LLM juez ni escribe repos reales:
/// aplica reglas exactas sobre el texto crudo de la respuesta. El score
/// está en 0..1 y el probe pasa si score >= 1.0 (regla exacta binaria para los tres tipos
/// iniciales de la suite quick).
/// </summary>
public static class ProbeScorer
{
    /// <summary>
    /// Puntuación exacta de una respuesta de probe.
    /// </summary>
    public static double Score(ProbeKind kind, string? output, string expected)
    {
        return kind switch
        {
            ProbeKind.Reading => ScoreExactText(output, expected),
            ProbeKind.Reasoning => ScoreExactText(output, expected),
            ProbeKind.StructuredOutput => ScoreExactJson(output, expected),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "tipo de probe sin regla de puntuación exacta"),
        };
    }

    private static double ScoreExactText(string? output, string expected)
    {
        if (output is null)
        {
            return 0.0;
        }
        var actual = output.Trim();
        return actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var ak = a.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var bk = b.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (!ak.SequenceEqual(bk))
                {
                    return false;
                }
                foreach (var name in ak)
                {
                    if (!JsonElementEquals(a.GetProperty(name), b.GetProperty(name)))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                var av = a.EnumerateArray().ToList();
                var bv = b.EnumerateArray().ToList();
                if (av.Count != bv.Count)
                {
                    return false;
                }
                for (var i = 0; i < av.Count; i++)
                {
                    if (!JsonElementEquals(av[i], bv[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            default:
                return false;
        }
    }

    private static double ScoreExactJson(string? output, string expected)
    {
        if (output is null)
        {
            return 0.0;
        }
        try
        {
            using var actualDoc = JsonDocument.Parse(output);
            using var expectedDoc = JsonDocument.Parse(expected);
            return JsonElementEquals(actualDoc.RootElement, expectedDoc.RootElement) ? 1.0 : 0.0;
        }
        catch (JsonException)
        {
            return 0.0;
        }
    }

    /// <summary>
    /// Extrae todos los bloques de texto de respuesta, en orden y sin inventar separadores.
    /// El razonamiento y el contenido anidado de herramientas no son la respuesta visible.
    /// Sin bloques de texto devuelve null; un bloque vacío conserva el texto vacío.
    /// </summary>
    public static string? ExtractText(ModelResponse response)
    {
        StringBuilder? text = null;
        foreach (var block in response.Content)
        {
            if (block is TextBlock answer)
            {
                text ??= new StringBuilder();
                text.Append(answer.Text);
            }
        }
        return text?.ToString();
    }

    /// <summary>
    /// Hash canónico determinista de un conjunto de probes, para la TaskSetHash de
    /// BenchmarkIdentity (ADR-0007 §6). SHA-256 hex minúsculo de una serialización canónica
    /// que incluye id, tipo, prompt, respuesta esperada y tope de costo.
    /// </summary>
    public static string TaskSetHash(IReadOnlyList<Probe> probes)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var p in probes.OrderBy(p => p.Id.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", p.Id.ToString());
                writer.WriteString("kind", p.Kind.ToString());
                writer.WriteString("prompt", p.Prompt);
                writer.WriteString("expected", p.Expected);
                writer.WriteString("maxCostUsd", p.MaxCostUsd.ToString("G29", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Hex(sha.ComputeHash(stream.ToArray()));
    }

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
