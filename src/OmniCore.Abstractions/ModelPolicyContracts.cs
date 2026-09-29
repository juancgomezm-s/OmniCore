namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Almacenamiento relacional de políticas de modelo por clave exacta (ADR-0044 §8). La
/// resolución es determinista por identidad, nunca vectorial: ningún índice semántico
/// participa en una decisión de autorización (ADR-0044 §10.11).
/// </summary>
public interface IModelPolicyStore
{
    /// <summary>Resuelve por clave exacta; null si la configuración no tiene política.</summary>
    StoredModelPolicy? Get(ModelPolicyKey key, CancellationToken cancellationToken);

    /// <summary>Todas las políticas guardadas (pantalla de mantenimiento, ADR-0044 §7).</summary>
    IReadOnlyList<StoredModelPolicy> List(CancellationToken cancellationToken);

    /// <summary>
    /// Guarda o reemplaza la política. Concasión optimista por revisión exacta
    /// (ADR-0044 §9): sin fila previa exige expectedRevision=0; si existe, exige la revisión
    /// vigente y produce la siguiente. Un valor obsoleto lanza
    /// <see cref="ModelPolicyRevisionConflictException"/>: nunca pisa cambios ajenos.
    /// </summary>
    StoredModelPolicy Set(ModelPolicyKey key, long expectedRevision, UserModelPolicy policy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Elimina SOLO la preferencia del usuario (ADR-0044 §7): ni cualificación ni historia de
    /// Turns. Exige la revisión vigente; la siguiente selección de la clave reabre el onboarding.
    /// </summary>
    void Delete(ModelPolicyKey key, long expectedRevision, CancellationToken cancellationToken);

    /// <summary>Historial de cambios de una clave (auditoría, ADR-0044 §8).</summary>
    IReadOnlyList<ModelPolicyChange> History(ModelPolicyKey key, CancellationToken cancellationToken);

    /// <summary>Selección vigente de modelo para un workspace; null si nunca se seleccionó.</summary>
    ModelSelectionState? GetSelection(string workspaceId, CancellationToken cancellationToken);

    /// <summary>Persiste la selección (con modo efímero seguro cuando aplica, ADR-0044 §6).</summary>
    void SetSelection(ModelSelectionState selection, CancellationToken cancellationToken);
}

/// <summary>
/// Rutas canónicas de datos por plataforma (ADR-0038 §2). Los datos de runtime viven fuera del
/// repo: `%LOCALAPPDATA%\OmniCore` en Windows y `$XDG_DATA_HOME/omnicore` en Linux (ADR-0039 §2).
/// </summary>
public interface IPlatformPaths
{
    /// <summary>Directorio de datos del usuario para OmniCore.</summary>
    string DataDirectory { get; }

    /// <summary>Base de datos relacional del usuario: (data)/user.db (ADR-0044 §8).</summary>
    string UserDatabasePath { get; }

    /// <summary>
    /// Configuración de usuario (<c>providers.yaml</c>, <c>models.yaml</c>…): <c>%APPDATA%\OmniCore</c>
    /// en Windows y <c>$XDG_CONFIG_HOME/omnicore</c> en Linux (ADR-0038, ADR-0039). Nunca el cwd:
    /// un repo no configura providers ni credenciales (INV-029).
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>Datos de runtime de un workspace: <c>(data)/workspaces/&lt;WorkspaceId&gt;/</c> (ADR-0039 §2).</summary>
    string WorkspaceDirectory(string workspaceId);
}
