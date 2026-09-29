namespace OmniCore.Infrastructure;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Retención del log de auditoría (ADR-0043 §1): purga los registros anteriores a un corte y
/// <b>deja constancia de sí misma</b> escribiendo un registro <c>audit.purge</c> en el mismo log.
/// Trabaja sobre el JSONL que escribe <see cref="FileAuditSink"/> (formato clave=valor).
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

    /// <summary>Parsea el campo at= de una línea (ISO invariante primero; formato previo por si acaso).
    /// Contrato público: lo usan la retención y los tests del formato heredado.</summary>
    public static bool TryParseTimestamp(string line, out DateTimeOffset at)
    {
        var atIdx = line.IndexOf("at=", StringComparison.Ordinal);
        while (atIdx >= 0)
        {
            var start = atIdx + 3;
            var end = line.IndexOf(',', start);
            if (end < 0)
            {
                end = line.IndexOf('}', start);
            }

            if (end < 0)
            {
                end = line.Length;
            }

            var raw = line.Substring(start, end - start);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
            {
                return true;
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
            {
                return true;
            }

            // 'at=' dentro de otro valor: seguir buscando la siguiente aparición.
            atIdx = line.IndexOf("at=", start, StringComparison.Ordinal);
        }

        at = default;
        return false;
    }
}
