namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// AuditSink que escribe registros redactados a un directorio de datos del usuario (ADR-0043 §1).
/// El Engine no escribe aquí directamente: es un consumidor de eventos (INV-012).
/// </summary>
public sealed class FileAuditSink : IAuditSink
{
    private readonly string _auditDir;

    public FileAuditSink(string dataDirectory)
    {
        _auditDir = Path.Combine(dataDirectory, "audit");
    }

    public void Record(AuditRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_auditDir);
        var line = JsonLine(record);
        var file = Path.Combine(_auditDir, "audit.jsonl");
        var prior = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
        File.WriteAllText(file, line + "\n" + prior);
    }

    private static string JsonLine(AuditRecord r)
    {
        var parts = new string[] {
            "event=" + r.EventName,
            "workspace=" + (r.Workspace is null ? "" : r.Workspace.ToString()),
            "session=" + (r.Session is null ? "" : r.Session.ToString()),
            "run=" + (r.Run is null ? "" : r.Run.ToString()),
            "at=" + r.Timestamp.ToString(),
        };
        return "{" + string.Join(",", parts) + "}";
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