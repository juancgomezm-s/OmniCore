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

    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build)
    {
        ModelKey = modelKey;
        HarnessPolicyHash = harnessPolicyHash;
        ToolkitHash = toolkitHash;
        ContextPolicyHash = contextPolicyHash;
        OverridesHash = overridesHash;
        Build = build;
    }

    /// <summary>Hash estable del fingerprint para comparar Turns.</summary>
    public string Hash() =>
        ModelKey + "|" + HarnessPolicyHash + "|" + ToolkitHash + "|" + ContextPolicyHash + "|"
        + OverridesHash + "|" + Build;
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

    public ContextSnapshot(Guid snapshotId, SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId,
        TurnId? turnId, long basedOnEventSequence, ExecutionFingerprint fingerprint,
        IReadOnlyList<ContextItem> items, int tokenCount)
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