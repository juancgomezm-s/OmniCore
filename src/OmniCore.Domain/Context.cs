namespace OmniCore.Domain;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

/// <summary>
/// Fingerprint del Turn: qué configuración recibió (ADR-0017, ADR-0007 §1). Se registra en
/// TurnStarted y permite explicar un Turn (`omni turn explain`).
/// </summary>
public sealed class ExecutionFingerprint
{
    public string ModelKey { get; }

    public string HarnessPolicyHash { get; }

    public string ToolkitHash { get; }

    public string TokenizerHash { get; }

    public string ContextPolicyHash { get; }

    public string OverridesHash { get; }

    public string Build { get; }

    /// <summary>Hash de la política efectiva del modelo (ADR-0044 §8).</summary>
    public string ModelPolicyHash { get; }

    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build)
        : this(modelKey, harnessPolicyHash, toolkitHash, contextPolicyHash, overridesHash, build, "", "")
    {
    }

    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build, string modelPolicyHash)
        : this(modelKey, harnessPolicyHash, toolkitHash, contextPolicyHash, overridesHash, build,
            modelPolicyHash, "")
    {
    }

    [JsonConstructor]
    public ExecutionFingerprint(string modelKey, string harnessPolicyHash, string toolkitHash,
        string contextPolicyHash, string overridesHash, string build, string modelPolicyHash,
        string tokenizerHash)
    {
        ModelKey = modelKey ?? throw new ArgumentNullException(nameof(modelKey));
        HarnessPolicyHash = harnessPolicyHash ?? throw new ArgumentNullException(nameof(harnessPolicyHash));
        ToolkitHash = toolkitHash ?? throw new ArgumentNullException(nameof(toolkitHash));
        TokenizerHash = tokenizerHash ?? throw new ArgumentNullException(nameof(tokenizerHash));
        ContextPolicyHash = contextPolicyHash ?? throw new ArgumentNullException(nameof(contextPolicyHash));
        OverridesHash = overridesHash ?? throw new ArgumentNullException(nameof(overridesHash));
        Build = build ?? throw new ArgumentNullException(nameof(build));
        ModelPolicyHash = modelPolicyHash ?? throw new ArgumentNullException(nameof(modelPolicyHash));
    }

    /// <summary>SHA-256 estable de todos los componentes, codificados con longitud para evitar colisiones.</summary>
    public string Hash()
    {
        var canonical = new StringBuilder();
        Append(canonical, ModelKey);
        Append(canonical, HarnessPolicyHash);
        Append(canonical, ToolkitHash);
        Append(canonical, TokenizerHash);
        Append(canonical, ContextPolicyHash);
        Append(canonical, OverridesHash);
        Append(canonical, Build);
        Append(canonical, ModelPolicyHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

/// <summary>Decisión del materializer para un item de contexto (ADR-0029).</summary>
public enum ContextDecision
{
    Included,
    OmittedByBudget,
    TruncatedByBudget,
    Pruned,
    Externalized,
    Compressed,
    Compacted,
}

/// <summary>Diagnóstico por item, con procedencia para explicar inclusión y omisiones.</summary>
public sealed record ContextDiagnostic(
    string ItemId,
    ContextProvenance Provenance,
    ContextDecision Decision,
    int Tokens);

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

    /// <summary>True si la política de presupuesto no pudo respetar el límite sin cortar contenido protegido.</summary>
    public bool Overflowed { get; }

    /// <summary>Decisiones del presupuesto, incluidas las omisiones y su procedencia (ADR-0029).</summary>
    public IReadOnlyList<ContextDiagnostic> Diagnostics { get; }

    /// <summary>Fingerprint determinista del contenido y decisiones del snapshot (sin SnapshotId).</summary>
    public string SnapshotFingerprint { get; }

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
        : this(snapshotId, sessionId, runId, taskId, laneId, turnId, basedOnEventSequence, fingerprint,
            items, tokenCount, overflowed, Array.Empty<ContextDiagnostic>())
    {
    }

    public ContextSnapshot(Guid snapshotId, SessionId sessionId, RunId runId, TaskId? taskId, LaneId? laneId,
        TurnId? turnId, long basedOnEventSequence, ExecutionFingerprint fingerprint,
        IReadOnlyList<ContextItem> items, int tokenCount, bool overflowed,
        IReadOnlyList<ContextDiagnostic> diagnostics)
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
        Diagnostics = diagnostics;
        SnapshotFingerprint = ComputeSnapshotFingerprint(items, diagnostics, fingerprint.Hash(), tokenCount);
    }

    private static string ComputeSnapshotFingerprint(IReadOnlyList<ContextItem> items,
        IReadOnlyList<ContextDiagnostic> diagnostics, string policyFingerprint, int tokenCount)
    {
        var canonical = new StringBuilder(policyFingerprint).Append('|')
            .Append(tokenCount.ToString(CultureInfo.InvariantCulture));
        foreach (var item in items)
        {
            canonical.Append('|').Append(item.Id).Append(':').Append(item.Kind).Append(':')
                .Append(item.EstimatedTokens.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item.Priority)
                .Append(':').Append(item.Retention).Append(':').Append(item.PreserveWhenTrimming).Append(':')
                .Append(item.Provenance.ContributorId).Append(':').Append(item.Provenance.Category).Append(':')
                .Append(item.Provenance.ComponentSource).Append(':')
                .Append(item.Provenance.Sensitive).Append(':')
                .Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.Content))));
            foreach (var reference in item.Provenance.Refs ?? Array.Empty<string>()) canonical.Append(':').Append(reference);
        }
        foreach (var diagnostic in diagnostics)
            canonical.Append('|').Append(diagnostic.ItemId).Append(':').Append(diagnostic.Decision).Append(':')
                .Append(diagnostic.Tokens.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(diagnostic.Provenance.ContributorId)
                .Append(':').AppendJoin(',', diagnostic.Provenance.Refs ?? Array.Empty<string>());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
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

    /// <summary>El primer mensaje del Run se conserva durante el recorte de conversación (ADR-0042).</summary>
    public bool PreserveWhenTrimming { get; }

    public ContextItem(string id, ContextItemKind kind, string content, int estimatedTokens,
        ContextPriority priority, RetentionPolicy retention, ContextProvenance provenance,
        bool preserveWhenTrimming = false)
    {
        Id = id;
        Kind = kind;
        Content = content;
        EstimatedTokens = estimatedTokens;
        Priority = priority;
        Retention = retention;
        Provenance = provenance;
        PreserveWhenTrimming = preserveWhenTrimming;
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
