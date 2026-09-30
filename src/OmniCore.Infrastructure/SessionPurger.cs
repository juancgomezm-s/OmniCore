namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Orquesta la purga de sesión (ADR-0001 §8, <c>omni session purge</c>): escribe el registro de
/// auditoría ANTES de borrar y luego elimina los eventos en una transacción atómica. La
/// auditoría sobrevive a la purga (ADR-0043 §1) porque vive en otro archivo.
/// <para>
/// Ante un fallo entre auditoría y borrado, la historia queda INTACTA (la dirección conservadora
/// es preservar datos): reintentar la purga es seguro y simplemente genera otro registro.
/// Los artifacts de la sesión quedan sin referencias y los recoge el GC (ver ArtifactGc).
/// </para>
/// </summary>
public sealed class SessionPurger
{
    private readonly SqliteEventStore _store;

    private readonly IAuditSink _audit;

    public SessionPurger(SqliteEventStore store, IAuditSink audit)
    {
        _store = store;
        _audit = audit;
    }

    /// <summary>Resultado de la purga de una sesión.</summary>
    public sealed class PurgeResult
    {
        public required SessionId Session { get; init; }

        public long EventsDeleted { get; init; }

        public DateTimeOffset PurgedAt { get; init; }

        public string SummaryLine() =>
            "session purge: sesión " + Session + " purgada, " + EventsDeleted + " evento(s) eliminados "
                + "(auditoría conservada)";
    }

    /// <summary>Purga la sesión: cuenta, audita y borra (en ese orden, ADR-0001 §8 + ADR-0043 §1).
    /// Devuelve null si la sesión no existe en el journal (fallo tipado, sin excepción de dominio).</summary>
    public PurgeResult? Purge(SessionId sessionId, WorkspaceId? workspace, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var events = _store.CountEvents(sessionId);
        if (events == 0)
        {
            return null;
        }

        // 1) Auditoría previa: si algo falla después, queda el rastro y la historia intacta.
        _audit.Record(new AuditRecord("session.purged", workspace, sessionId, null, now,
            null, new Dictionary<string, string> {
                ["events"] = events.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }), cancellationToken);

        // 2) Borrado atómico.
        var deleted = _store.PurgeSession(sessionId, cancellationToken);
        return new PurgeResult {
            Session = sessionId,
            EventsDeleted = deleted,
            PurgedAt = now,
        };
    }
}
