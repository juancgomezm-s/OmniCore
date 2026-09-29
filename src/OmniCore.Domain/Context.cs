namespace OmniCore.Domain;

/// <summary>
/// Fingerprint del Turn: qué configuración recibió (ADR-0017, ADR-0007 §1). Se registra en
/// TurnStarted y permite explicar un Turn (`omni turn explain`).
/// </summary>
public sealed class ExecutionFingerprint
{
    public string ModelKey { get; }

    public string HarnessPolicyHash { get; }

    public string ToolkitHash { get; }

    public string ContextPolicyHash { get; }

    public string OverridesHash { get; }

    public string Build { get; }

    /// <summary>
    /// Hash de la política efectiva del modelo (ADR-0044 §8): <c>EffectiveModelPolicy.Fingerprint()</c>
    /// — clave + revisión + categoría + modo de mutación. Vacío cuando no hay política
    /// cableada (p. ej. tests/sim de M1–M2). Nunca contenido: solo identidad de la política.
    /// </summary>
    public string ModelPolicyHash { get; }

    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build)
        : this(modelKey, harnessPolicyHash, toolkitHash, contextPolicyHash, overridesHash, build, "")
    {
    }

    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build, string modelPolicyHash)
    {
        ModelKey = modelKey;
        HarnessPolicyHash = harnessPolicyHash;
        ToolkitHash = toolkitHash;
        ContextPolicyHash = contextPolicyHash;
        OverridesHash = overridesHash;
        Build = build;
        ModelPolicyHash = modelPolicyHash;
    }

    /// <summary>Hash estable del fingerprint para comparar Turns.</summary>
    public string Hash() =>
        ModelKey + "|" + HarnessPolicyHash + "|" + ToolkitHash + "|" + ContextPolicyHash + "|"
        + OverridesHash + "|" + Build + "|" + ModelPolicyHash;
}

/// <summary>Snapshot del contexto exacto enviado en un Turn (spec §29, ADR-0029).</summary>
public sealed class ContextSnapshot
{
    public Guid SnapshotId { get; }

    public SessionId SessionId { get; }

    public RunId RunId { get; }

    public TaskId? TaskId { get; }

    public LaneId? LaneId { get; }

    public TurnId? TurnId { get; }

    public long BasedOnEventSequence { get; }

    public ExecutionFingerprint Fingerprint { get; }

    public IReadOnlyList<ContextItem> Items { get; }

    public int TokenCount { get; }

    /// <summary>True si la política de presupuesto recortó el contexto (ContextOverflow, ADR-0042 §3).</summary>
    public bool Overflowed { get; }

    public ContextSnapshot(Guid snapshotId, SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId,
        TurnId? turnId, long basedOnEventSequence, ExecutionFingerprint fingerprint,
        IReadOnlyList<ContextItem> items, int tokenCount)
        : this(snapshotId, sessionId, runId, taskId, laneId, turnId, basedOnEventSequence, fingerprint, items,
            tokenCount, false)
    {
    }

    public ContextSnapshot(Guid snapshotId, SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId,
        TurnId? turnId, long basedOnEventSequence, ExecutionFingerprint fingerprint,
        IReadOnlyList<ContextItem> items, int tokenCount, bool overflowed)
    {
        SnapshotId = snapshotId;
        SessionId = sessionId;
        RunId = runId;
        TaskId = taskId;
        LaneId = laneId;
        TurnId = turnId;
        BasedOnEventSequence = basedOnEventSequence;
        Fingerprint = fingerprint;
        Items = items;
        TokenCount = tokenCount;
        Overflowed = overflowed;
    }
}

/// <summary>Item de contexto materializable (spec §24, ADR-0042).</summary>
public sealed class ContextItem
{
    public string Id { get; }

    public ContextItemKind Kind { get; }

    public string Content { get; }

    public int EstimatedTokens { get; }

    public ContextPriority Priority { get; }

    public RetentionPolicy Retention { get; }

    public ContextProvenance Provenance { get; }

    public ContextItem(string id, ContextItemKind kind, string content, int estimatedTokens,
        ContextPriority priority, RetentionPolicy retention, ContextProvenance provenance)
    {
        Id = id;
        Kind = kind;
        Content = content;
        EstimatedTokens = estimatedTokens;
        Priority = priority;
        Retention = retention;
        Provenance = provenance;
    }
}

/// <summary>Kinds de ContextItem congelados en M1 (spec §24, ADR-0028 §1).</summary>
public enum ContextItemKind
{
    System,
    Task,
    Decision,
    Constraint,
    UserMessage,
    AssistantMessage,
    ToolResult,
    File,
    Skill,
    Knowledge,
    SubagentResult,
    Summary,
    Checkpoint,
    WorkingState,
    Memory,
}