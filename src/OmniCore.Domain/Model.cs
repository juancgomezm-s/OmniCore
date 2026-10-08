namespace OmniCore.Domain;

/// <summary>Workspace local: carpeta raíz abierta (ADR-0022). El id deriva de la ruta canónica.</summary>
public sealed class WorkspaceId
{
    private readonly string _value;

    private WorkspaceId(string value) => _value = value;

    /// <summary>Deriva el WorkspaceId de una ruta ya canonizada por la capa Host/Identity (ADR-0038 §4).</summary>
    public static WorkspaceId Of(string canonicalRootPath) =>
        new(_Derive(canonicalRootPath));

    /// <summary>Reconstruye un WorkspaceId desde su forma persistida.</summary>
    public static WorkspaceId Parse(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => _value;

    /// <inheritdoc />
    public override bool Equals(object? other) => other is WorkspaceId w && w._value.Equals(_value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// SHA-256 (16 hex) de la ruta que ya canonizó la capa Host/Identity. El Domain no aplica
    /// reglas dependientes de plataforma (ADR-0038 §4).
    /// </summary>
    private static string _Derive(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalPath));
        return Convert.ToHexStringLower(hash, 0, 8);
    }
}

/// <summary>Identidad del repositorio, compartida por clones y worktrees (ADR-0022 §3).</summary>
public sealed class ProjectId
{
    private readonly string _value;

    private ProjectId(string value) => _value = value;

    /// <summary>Deriva una identidad estable del origen normalizado o del git-common-dir (ADR-0022 §3).</summary>
    public static ProjectId Derive(string normalizedOriginOrGitCommonDir)
    {
        ArgumentNullException.ThrowIfNull(normalizedOriginOrGitCommonDir);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalizedOriginOrGitCommonDir.Trim()));
        return new(Convert.ToHexStringLower(hash, 0, 8));
    }

    public static ProjectId Parse(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => _value;

    /// <inheritdoc />
    public override bool Equals(object? other) => other is ProjectId p && p._value.Equals(_value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();
}

/// <summary>Referencia a un workspace desde una sesión (spec §6).</summary>
public record WorkspaceRef(WorkspaceId Id, string DisplayPath) { }

/// <summary>Presupuesto de un Run o Task (ADR-0037 §7).</summary>
public record TaskBudget(
    decimal? MaxCostUsd,
    long? MaxTokens,
    int? MaxTurns,
    int? MaxToolCalls) { }

/// <summary>Sesión durable: espacio lógico de interacción (spec §6).</summary>
public record Session(
    SessionId Id,
    WorkspaceRef Workspace,
    ProfileId Profile,
    DateTimeOffset CreatedAt) { }

/// <summary>Run: intención explícita del usuario (spec §7, ADR-0035 §2).</summary>
public record Run(
    RunId Id,
    SessionId Session,
    string Objective,
    RunMode Mode,
    ExecutionStrategy Strategy,
    FailurePolicy FailurePolicy,
    TaskBudget Budget,
    TaskId? RootTask,
    RunState State) { }

/// <summary>Task: unidad lógica de trabajo delegable (spec §8–§9).</summary>
public record Task(
    TaskId Id,
    string Objective,
    TaskState State,
    IReadOnlyList<TaskDependency> Dependencies,
    TaskBudget Budget) { }

/// <summary>Dependencia de una Task sobre otra (ADR-0036 §2). Required = true por defecto.</summary>
public record TaskDependency(TaskId TaskId, bool Required) { }

/// <summary>Lane: ejecución concreta de una Task (spec §10, ADR-0036 §3).</summary>
public record Lane(
    LaneId Id,
    TaskId TaskId,
    LaneState State,
    ProfileId AgentProfile,
    DateTimeOffset? LastHeartbeatAt) { }

/// <summary>Turn: interacción individual con un modelo (spec §12).</summary>
public record Turn(
    TurnId Id,
    LaneId LaneId,
    TurnState State,
    DateTimeOffset StartedAt) { }

/// <summary>Heartbeat de una Lane (spec §11, ADR-0036 §3). Telemetría, no canónico.</summary>
public record LaneHeartbeat(
    DateTimeOffset Timestamp,
    LaneActivity Activity,
    int CurrentTurn,
    string? CurrentOperation,
    DateTimeOffset LastProgressAt,
    long TokensUsed,
    int ToolCalls) { }

/// <summary>
/// Resultado normalizado de una ejecución de tool (spec §40). `IsError` distingue explícitamente
/// los fallos (Acceso denegado, no encontrado, falta path) de los éxitos: el runtime NUNCA emite
/// toolcall.succeeded para un resultado con IsError=true (P0-2). Todo resultado de error lleva
/// además un código tipado (spec §71): sin código explícito se clasifica TOOL_FAILURE; un
/// resultado exitivo nunca lleva código. El texto visible al modelo (<see cref="Summary"/>) viaja
/// aparte y es estable.
/// </summary>
public sealed class ToolResult
{
    public string Summary { get; }

    public string? Preview { get; }

    public ArtifactRef? Artifact { get; }

    public long OriginalSize { get; }

    public bool WasExternalized { get; }

    public EffectOutcome EffectOutcome { get; }

    public bool IsError { get; }

    /// <summary>Transient exact bytes returned by a filesystem executor, never serialized or model-visible.
    /// The synchronous runtime publishes them under its existing CAS lease before the success event.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? AfterStateBytes { get; init; }

    /// <summary>Código tipado del error (spec §71); null si el resultado es exitivo.</summary>
    public ToolErrorCode? ErrorCode { get; }

    public ToolResult(string summary, string? preview, ArtifactRef? artifact, long originalSize,
        bool wasExternalized, EffectOutcome effectOutcome)
        : this(summary, preview, artifact, originalSize, wasExternalized, effectOutcome, false)
    {
    }

    public ToolResult(string summary, string? preview, ArtifactRef? artifact, long originalSize,
        bool wasExternalized, EffectOutcome effectOutcome, bool isError, ToolErrorCode? errorCode = null)
    {
        Summary = summary;
        Preview = preview;
        Artifact = artifact;
        OriginalSize = originalSize;
        WasExternalized = wasExternalized;
        EffectOutcome = effectOutcome;
        IsError = isError;
        // Invariante (spec §71): todo error lleva código; el éxito nunca. Sin código explícito
        // el fallo se clasifica como TOOL_FAILURE, la categoría mínima de spec §71.
        ErrorCode = isError ? errorCode ?? ToolErrorCode.ToolFailure : null;
    }

    /// <summary>Resultado de error explícito (no-exitoso), clasificado TOOL_FAILURE: el runtime lo
    /// marca Failed/Rejected.</summary>
    public static ToolResult Error(string summary) =>
        new(summary, null, null, 0, false, EffectOutcome.None, true);

    /// <summary>Resultado de error con código tipado (spec §71). El summary (texto visible al
    /// modelo) viaja aparte del código y mantiene su formato estable.</summary>
    public static ToolResult Error(ToolErrorCode errorCode, string summary) =>
        new(summary, null, null, 0, false, EffectOutcome.None, true, errorCode);

    /// <summary>Resultado de éxito.</summary>
    public static ToolResult Ok(string summary, string? preview, long originalSize, bool externalized,
        EffectOutcome effect) => new(summary, preview, null, originalSize, externalized, effect, false);
}

/// <summary>Estado completo de un Run para pasar por el PlanCompletionGate (ADR-0016 §10).</summary>
public record RunSnapshot(
    RunId RunId,
    RunState State,
    Plan? Plan,
    IReadOnlyList<Task> Tasks,
    IReadOnlyList<Lane> Lanes) { }
