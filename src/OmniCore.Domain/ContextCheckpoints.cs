namespace OmniCore.Domain;

/// <summary>Estado factual resumeable de una región compactada; nunca sustituye al Plan canónico.</summary>
public sealed record ContextCheckpoint(
    string CheckpointId,
    RunId RunId,
    long ThroughEventSequence,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> ModifiedFiles,
    IReadOnlyList<string> FailedAttempts,
    string TestState,
    IReadOnlyList<string> PendingWork,
    IReadOnlyList<string> OpenQuestions,
    int CompactedThroughItemIndex,
    string Summary,
    ArtifactRef Artifact,
    string MetaModelFingerprint);

/// <summary>Compaction registrada; el journal original permanece intacto (INV-008).</summary>
public record ContextCheckpointRecorded(string CheckpointId, RunId RunId, long ThroughEventSequence,
    ArtifactRef CheckpointArtifact, string MetaModelFingerprint) : DomainEventPayload
{
    public EventType Type() => EventType.Of("context.checkpoint_recorded");
    public int SchemaVersion() => 1;
}

/// <summary>Inicio auditable de una invocación lateral del MetaModelService.</summary>
public record MetaModelInvocationStarted(string InvocationId, RunId RunId, string Operation,
    string ModelFingerprint, ArtifactRef InputArtifact) : DomainEventPayload
{
    public EventType Type() => EventType.Of("meta_model.invocation_started");
    public int SchemaVersion() => 1;
}

/// <summary>Resultado auditable del MetaModelService; el texto vive en un artifact inmutable.</summary>
public record MetaModelInvocationCompleted(string InvocationId, RunId RunId, string Operation,
    string ModelFingerprint, ArtifactRef OutputArtifact, TokenUsage? Usage = null, decimal? CostUsd = null,
    TokenUsageFields? ReportedUsageFields = null, GenerationRequestAttemptEvidence? GenerationAttempts = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("meta_model.invocation_completed");
    public int SchemaVersion() => 3;
}

public record MetaModelInvocationFailed(string InvocationId, RunId RunId, string Operation,
    string ModelFingerprint, string ErrorCode, TokenUsage? Usage = null, decimal? CostUsd = null,
    TokenUsageFields? ReportedUsageFields = null, GenerationRequestAttemptEvidence? GenerationAttempts = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("meta_model.invocation_failed");
    public int SchemaVersion() => 3;
}

/// <summary>Known failure before entering the provider; carries no usage or monetary receipt.</summary>
public record MetaModelInvocationNotDispatched(string InvocationId, RunId RunId, string Operation,
    string ModelFingerprint) : DomainEventPayload
{
    public EventType Type() => EventType.Of("meta_model.invocation_not_dispatched");
    public int SchemaVersion() => 1;
}
