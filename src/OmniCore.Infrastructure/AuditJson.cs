namespace OmniCore.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Línea del log de auditoría (ADR-0043 §1): metadata redactada serializada como UN objeto JSON
/// por línea física. Mantiene el vocabulario del formato previo (event, workspace, session, run,
/// at, eventRef) para que la retención de ADR-0043 (`omni audit purge`, track M4) siga apuntando
/// al mismo archivo y a los mismos campos.
/// </summary>
internal sealed record AuditLine(
    string Event,
    string Workspace,
    string Session,
    string Run,
    DateTimeOffset At,
    string EventRef,
    Dictionary<string, string> Details);

/// <summary>Serializador generado en compilación: sin reflexión (analizadores AOT activos).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(AuditLine))]
internal sealed partial class AuditJsonContext : JsonSerializerContext
{
}

/// <summary>Convierte un <see cref="AuditRecord"/> en una línea JSON redactada.</summary>
internal static class AuditLineSerializer
{
    /// <summary>
    /// Redacta y serializa el registro como una línea JSON única. Los campos de texto libre —el
    /// tipo de evento y los datos de la decisión (capas, causas…)— atraviesan
    /// <see cref="PiiRedactor"/> ANTES de serializar (ADR-0018 §4: los secretos nunca llegan a
    /// logs; ADR-0043 §1: solo metadata redactada). Los campos estructurales (ids, timestamp,
    /// referencia al evento) son valores tipados del runtime —no contenido— y ADR-0043 los
    /// exige resolubles, así que van en claro: redactarlos destruiría la referencia (el patrón
    /// de cookies del redactor confundiría <c>session:&lt;guid&gt;</c> con una sesión con secreto).
    /// El JSON escapa saltos de línea, así que cada registro es exactamente una línea física.
    /// </summary>
    public static string ToJsonLine(AuditRecord record)
    {
        var redactor = new PiiRedactor();
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in record.Details)
        {
            details[redactor.Redact(key)] = redactor.Redact(value ?? "");
        }

        return JsonSerializer.Serialize(
            new AuditLine(
                redactor.Redact(record.EventName ?? ""),
                record.Workspace is null ? "" : record.Workspace.ToString(),
                record.Session is null ? "" : record.Session.ToString(),
                record.Run is null ? "" : record.Run.ToString(),
                record.Timestamp,
                record.EventRef ?? "",
                new(details)),
            AuditJsonContext.Default.AuditLine);
    }
}
