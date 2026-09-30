namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// AuditSink que escribe registros redactados a un archivo JSONL del directorio de datos
/// (ADR-0043 §1). El Engine no escribe aquí directamente: es un consumidor de eventos (INV-012).
/// <para>
/// Append-only de verdad: abre con <see cref="FileMode.Append"/> —crea el archivo si no existe
/// y escribe siempre al final, nunca trunca ni reescribe contenido previo—, cada registro es UNA
/// línea JSON (serializador generado en compilación, sin reflexión) y se baja a disco (flush
/// físico) antes de volver. Vive fuera del journal de la sesión, así que sobrevive a
/// <c>omni session purge</c> (ADR-0043 §1); solo la retención (<c>omni audit purge</c>, M4) lo acota.
/// </para>
/// </summary>
public sealed class FileAuditSink : IAuditSink
{
    private readonly string _auditDir;
    private readonly string _auditFile;

    public FileAuditSink(string dataDirectory)
    {
        _auditDir = Path.Combine(dataDirectory, "audit");
        _auditFile = Path.Combine(_auditDir, "audit.jsonl");
    }

    /// <summary>
    /// Ruta del log de auditoría de un directorio de datos. Contrato público: la retención de
    /// ADR-0043 (<c>omni audit purge</c>, track M4) apunta a esta misma ruta.
    /// </summary>
    public static string AuditFilePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "audit", "audit.jsonl");

    public void Record(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_auditDir);

        var line = AuditLineSerializer.ToJsonLine(record);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        // Append-only (ADR-0043 §1): FileMode.Append escribe al final; jamás trunca el contenido
        // previo. Una sola escritura por registro (atómica con O_APPEND) y flush físico antes de
        // cerrar para que el registro sobreviva a un crash del proceso.
        using var file = new FileStream(_auditFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(bytes, 0, bytes.Length);
        file.Flush(flushToDisk: true);
    }
}

/// <summary>AuditSink en memoria para tests deterministas.</summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly List<AuditRecord> _records = new();

    public void Record(AuditRecord record, CancellationToken cancellationToken)
    {
        _records.Add(record);
    }

    public IReadOnlyList<AuditRecord> Records() => _records.ToArray();

    public int Count() => _records.Count;
}