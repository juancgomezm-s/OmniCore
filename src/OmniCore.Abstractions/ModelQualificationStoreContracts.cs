namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Almacenamiento relacional de perfiles de cualificación empírica por clave exacta
/// (ADR-0007 §6, M5). La resolución es determinista por identidad de
/// <see cref="ModelQualificationKey"/>, nunca vectorial: ningún índice semántico participa en una
/// decisión de cualificación. La cualificación es de la máquina y el modelo, por eso el almacén
/// vive en el scope User.
/// </summary>
public interface IModelQualificationStore
{
    /// <summary>Resuelve por clave exacta; null si la configuración no tiene perfil cualificado.</summary>
    ModelQualificationProfile? Get(ModelQualificationKey key, CancellationToken cancellationToken);

    /// <summary>Todos los perfiles de cualificación guardados (pantalla de mantenimiento).</summary>
    IReadOnlyList<ModelQualificationProfile> List(CancellationToken cancellationToken);

    /// <summary>
    /// Guarda o reemplaza el perfil de cualificación. Concasión optimista por
    /// <c>profile_revision</c> (equivalente de ADR-0044 §9 para M5): sin fila previa exige
    /// expectedRevision=0; si existe, exige la revisión vigente y produce la siguiente. Un valor
    /// obsoleto lanza <see cref="ModelQualificationRevisionConflictException"/>: nunca pisa
    /// cambios ajenos.
    /// </summary>
    ModelQualificationProfile Upsert(ModelQualificationKey key, long expectedRevision,
        ModelQualificationState state, string suiteId, string suiteVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Guarda la siguiente revisión del perfil y todos sus traits en una sola transacción.
    /// Los traits deben identificar esta clave y expectedRevision+1. Un fallo o cancelación
    /// conserva íntegramente el perfil y traits anteriores, sin cualificación parcial.
    /// </summary>
    ModelQualificationProfile UpsertWithTraits(ModelQualificationKey key, long expectedRevision,
        ModelQualificationState state, string suiteId, string suiteVersion,
        IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken);

    /// <summary>
    /// Marca un perfil como Stale sin destruir la evidencia (ADR-0007 §4): se aplica solo cuando
    /// la suite que produjo la cualificación (suite_id + suite_version) tiene una versión mayor
    /// nueva, y solo sobre perfiles Qualified/Calibrated. `suite_version` del perfil nunca se
    /// sobrescribe: identifica la suite que produjo el perfil; la versión que lo volvió Stale se
    /// registra aparte en `StaleBySuiteVersion`. Exige la revisión vigente para no pisar cambios
    /// ajenos. Rechaza (ModelQualificationStaleException) estados no cualificados, otra suite, y
    /// versiones minor o iguales. Conserva los traits históricos y los expone también en la
    /// nueva revisión dentro de la misma transacción; fallo o cancelación no deja un Stale parcial.
    /// </summary>
    ModelQualificationProfile MarkStale(ModelQualificationKey key, long expectedRevision,
        string newSuiteVersion, CancellationToken cancellationToken);

    /// <summary>Traits empíricos de un perfil (por key_hash + profile_revision).</summary>
    IReadOnlyList<ModelTraitRecord> Traits(ModelQualificationKey key, long profileRevision,
        CancellationToken cancellationToken);

    /// <summary>Reemplaza el conjunto de traits de una revisión exacta de un perfil.</summary>
    void SaveTraits(ModelQualificationKey key, long profileRevision,
        IReadOnlyList<ModelTraitRecord> traits, CancellationToken cancellationToken);
}
