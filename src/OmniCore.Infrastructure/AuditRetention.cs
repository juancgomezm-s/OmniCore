namespace OmniCore.Infrastructure;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using System.Text.Json;

/// <summary>
/// Retención del log de auditoría (ADR-0043 §1): purga los registros anteriores a un corte y
/// <b>deja constancia de sí misma</b> escribiendo un registro <c>audit.purge</c> en el mismo log.
/// Trabaja sobre el JSONL que escribe <see cref="FileAuditSink"/> (objeto JSON por línea).
/// <para>
/// Reglas de seguridad: una línea cuyo campo <c>at</c> no se pueda parsear se CONSERVA (nunca se
/// borra lo que no se entiende); el registro de la propia purga se escribe solo cuando la purga
/// se ejecuta de verdad (dry-run no tiene efectos).
/// </para>
/// </summary>
public sealed class AuditRetention
{
    /// <summary>Retención por defecto en días (ADR-0043 §1).</summary>
    public const int DefaultRetentionDays = 180;

    private readonly string _auditFile;

    private readonly IAuditSink _sink;

    public AuditRetention(string dataDirectory, IAuditSink sink)
    {
        _auditFile = FileAuditSink.AuditFilePath(dataDirectory);
        _sink = sink;
    }

    /// <summary>Resultado de una purga de auditoría (o de su simulación).</summary>
    public sealed class PurgeResult
    {
        public long Examined { get; init; }

        public long Removed { get; init; }

        public long Kept { get; init; }

        public long UnparseableKept { get; init; }

        public bool DryRun { get; init; }

        public DateTimeOffset Cutoff { get; init; }

        public string SummaryLine() =>
            "audit purge: " + Examined + " registro(s) examinado(s), " + Removed + " eliminado(s), "
                + Kept + " conservado(s)"
                + (UnparseableKept > 0 ? " (" + UnparseableKept + " sin fecha legible, conservados)" : "")
                + (DryRun ? " — dry-run (sin cambios)" : "");
    }

    /// <summary>
    /// Purga los registros con <c>at</c> anterior a <paramref name="cutoff"/>. Con
    /// <paramref name="dryRun"/> solo informa. Al ejecutar de verdad, escribe el registro
    /// <c>audit.purge</c> con los conteos y el corte aplicado (la purga deja traza de sí misma).
    /// </summary>
    public PurgeResult PurgeBefore(DateTimeOffset cutoff, bool dryRun, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lines = File.Exists(_auditFile)
            ? File.ReadAllLines(_auditFile)
            : Array.Empty<string>();

        var removed = 0L;
        var kept = new List<string>();
        var unparseable = 0L;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (TryParseTimestamp(line, out var at))
            {
                if (at < cutoff)
                {
                    removed += 1;
                    continue;
                }
            }
            else
            {
                // Fecha ilegible: conservar (nunca borrar lo que no se entiende).
                unparseable += 1;
            }

            kept.Add(line);
        }

        if (!dryRun && (removed > 0 || !File.Exists(_auditFile)))
        {
            File.WriteAllLines(_auditFile, kept);
        }

        if (!dryRun)
        {
            _sink.Record(new AuditRecord("audit.purge", null, null, null, now,
                null, new Dictionary<string, string> {
                    ["removed"] = removed.ToString(CultureInfo.InvariantCulture),
                    ["kept"] = kept.Count.ToString(CultureInfo.InvariantCulture),
                    ["cutoff"] = cutoff.ToString("O", CultureInfo.InvariantCulture),
                }), cancellationToken);
        }

        return new PurgeResult {
            Examined = lines.LongLength,
            Removed = removed,
            Kept = kept.Count,
            UnparseableKept = unparseable,
            DryRun = dryRun,
            Cutoff = cutoff,
        };
    }

    /// <summary>Parsea el campo JSON <c>at</c>; acepta también el antiguo formato key=value
    /// para no descartar por error registros creados antes del cambio a JSONL.</summary>
    public static bool TryParseTimestamp(string line, out DateTimeOffset at)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("at", out var timestamp)
                || timestamp.ValueKind != JsonValueKind.String)
            {
                at = default;
                return false;
            }
            return DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
        }
        catch (JsonException)
        {
            // Compatibilidad con el formato histórico clave=valor; JSON malformado sigue siendo
            // fail-safe si tampoco se encuentra una fecha heredada interpretable.
        }

        var atIdx = line.IndexOf("at=", StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            var start = atIdx + 3;
            var end = line.IndexOfAny([',', '}'], start);
            if (end < 0) end = line.Length;
            var raw = line[start..end];
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)
                || DateTimeOffset.TryParse(raw, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
                return true;
        }

        at = default;
        return false;
    }
}
