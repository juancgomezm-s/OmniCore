namespace OmniCore.Infrastructure;

using System.Buffers;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;

/// <summary>
/// Redacción de secretos conocidos sobre el payload de un evento ANTES de persistirlo (ADR-0018 §3),
/// recorriendo solo los valores de texto del JSON. Nunca se sustituye sobre el JSON crudo: un secreto
/// corto que coincidiera con un fragmento de un id (GUID, hash) corrompería el evento y lo dejaría
/// ilegible. Por eso los valores estructurales (ids, hashes) solo se redactan si el valor ENTERO es un
/// secreto registrado; el resto de textos se redactan completos. Los nombres de propiedad no se tocan.
/// </summary>
public static class JournalRedaction
{
    public static string RedactStringValues(string json, ISecretRedactor redactor)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>(json.Length + 64);
        using (var writer = new Utf8JsonWriter(buffer))
            Write(document.RootElement, writer, redactor);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, ISecretRedactor redactor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer, redactor);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, writer, redactor);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                writer.WriteStringValue(LooksStructural(value)
                    ? (redactor.IsKnownSecret(value) ? redactor.Redact(value) : value)
                    : redactor.Redact(value));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Ids y hashes: GUID, o un valor (con prefijo opcional <c>tipo:</c>) formado solo por dígitos
    /// hexadecimales y guiones de al menos 16 caracteres.
    /// </summary>
    internal static bool LooksStructural(string value)
    {
        if (value.Length < 16) return false;
        if (Guid.TryParse(value, out _)) return true;
        var start = value.IndexOf(':');
        var body = start >= 0 && start < 16 ? value.AsSpan(start + 1) : value.AsSpan();
        if (body.Length < 16) return false;
        foreach (var c in body)
            if (!(char.IsAsciiHexDigit(c) || c == '-')) return false;
        return true;
    }
}
