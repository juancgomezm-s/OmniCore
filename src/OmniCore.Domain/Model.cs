namespace OmniCore.Domain;

/// <summary>Workspace local: carpeta raíz abierta (ADR-0022). El id deriva de la ruta canónica.</summary>
public sealed class WorkspaceId
{
    private readonly string _value;

    private WorkspaceId(string value) => _value = value;

    /// <summary>Deriva el WorkspaceId de la ruta canónica de la raíz (ADR-0022 §3, OAQ-11).</summary>
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

    private static string _Derive(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var canonical = path.Trim().ToLowerInvariant().Replace('\\', '/').TrimEnd('/');
        return canonical.Length == 0 ? "root-empty" : canonical;
    }
}

/// <summary>Identidad del repositorio, compartida por clones y worktrees (ADR-0022 §3).</summary>
public sealed class ProjectId
{
    private readonly string _value;

    private ProjectId(string value) => _value = value;

    public static ProjectId Derive(string normalizedOriginOrGitCommonDir) =>
        new(normalizedOriginOrGitCommonDir.Trim().ToLowerInvariant());

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

/// <summary>Resultado normalizado de una ejecución de tool (spec §40).</summary>
public record ToolResult(
    string Summary,
    string? Preview,
    ArtifactRef? Artifact,
    long OriginalSize,
    bool WasExternalized,
    EffectOutcome EffectOutcome) { }

/// <summary>Estado completo de un Run para pasar por el PlanCompletionGate (ADR-0016 §10).</summary>
public record RunSnapshot(
    RunId RunId,
    RunState State,
    Plan? Plan,
    IReadOnlyList<Task> Tasks,
    IReadOnlyList<Lane> Lanes) { }