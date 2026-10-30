namespace OmniCore.Domain;

/// <summary>Authorized memory mutation intent, persisted before touching the shared scope database.
/// A promotion intent may only follow the exact durable human approval (ADR-0028).</summary>
public sealed record ScopedMemoryMutationPrepared(string OperationId, MemoryScope Scope, string Operation,
    string CandidateId, string MemoryId, MemoryRecord? Record, string ExpectedRevision,
    DateTimeOffset PreparedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.scoped_mutation_prepared");
    public int SchemaVersion() => 1;
}

/// <summary>Journal acknowledgement of an atomically recorded scope database operation.</summary>
public sealed record ScopedMemoryMutationCompleted(string OperationId, string MemoryId, bool Removed,
    DateTimeOffset CompletedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.scoped_mutation_completed");
    public int SchemaVersion() => 1;
}

public sealed record ScopedMemoryMutationResult(string MemoryId, bool Removed);

/// <summary>Terminal precondition failure for an unapplied memory intent; no mutation was committed.</summary>
public sealed record ScopedMemoryMutationRejected(string OperationId, string ReasonCode,
    DateTimeOffset RejectedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.scoped_mutation_rejected");
    public int SchemaVersion() => 1;
}
