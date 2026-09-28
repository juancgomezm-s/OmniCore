namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Resultado de reconciliar un efecto de filesystem tras un crash (ADR-0004 §4). El Engine
/// emite un evento <c>ToolCallReconciled</c> con este resultado; nunca re-ejecuta la tool.
/// </summary>
public sealed record FilesystemReconciliation(ReconciliationOutcome Outcome, string Detail)
{
    public static FilesystemReconciliation Applied(string detail) =>
        new FilesystemReconciliation(ReconciliationOutcome.Applied, detail);

    public static FilesystemReconciliation NotApplied(string detail) =>
        new FilesystemReconciliation(ReconciliationOutcome.NotApplied, detail);

    public static FilesystemReconciliation Conflict(string detail) =>
        new FilesystemReconciliation(ReconciliationOutcome.Conflict, detail);

    public static FilesystemReconciliation Unresolvable(string detail) =>
        new FilesystemReconciliation(ReconciliationOutcome.Unresolvable, detail);
}

/// <summary>
/// Puerta del Engine hacia la reconciliación segura de efectos de filesystem (ADR-0004 §4).
/// El Engine solo conoce Abstractions+Domain (grafo ADR-0009); la implementación real vive en
/// OmniCore.Tools (OmniCore.Tools.FilesystemReconciler) y se compone por el Host o los tests.
///
/// Contrato de seguridad: la implementación debe FALLAR CERRADO (nunca devolver Applied a menos
/// que el estado real lo confirme) ante rutas ausentes/sospechosas, metadatos malformados o
/// hash fuera de pre/post. <c>reconciliationJson</c> es la serialización canónica que viajó en el
/// evento durable <c>ToolCallStarted</c> (commit Barrier, ADR-0002 §2), por lo que sobrevive al crash.
/// </summary>
public interface IFilesystemReconciler
{
    /// <summary>
    /// Clasifica el efecto de una ToolCall Started-sin-outcome comparando el hash actual del archivo
    /// con los hashes pre/post esperados persistidos en el journal. SOLO observa (lee el archivo);
    /// nunca muta ni re-ejecuta.
    /// </summary>
    FilesystemReconciliation Reconcile(string workspaceRoot, string reconciliationJson,
        CancellationToken cancellationToken);
}