namespace OmniCore.Domain;

/// <summary>Durable Session memory candidate and policy decisions (ADR-0028 §6).</summary>
public enum MemoryCandidateOrigin { Model, Reconciler, User, Heuristic }

public sealed record MemoryCandidateProposed(string CandidateId, MemoryScope ProposedScope, MemoryKind Kind,
    string Content, IReadOnlyList<string> EvidenceRefs, int Occurrences, MemoryCandidateOrigin Origin,
    string Decision, string? ConflictMemoryId, DateTimeOffset ProposedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.candidate_proposed");
    public int SchemaVersion() => 1;
}

public sealed record MemoryPromoted(string CandidateId, string MemoryId, string? Supersedes,
    DateTimeOffset PromotedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.promoted");
    public int SchemaVersion() => 1;
}

public sealed record MemoryRejected(string CandidateId, string ReasonCode, DateTimeOffset RejectedAt)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.rejected");
    public int SchemaVersion() => 1;
}

/// <summary>Durable user tombstone for an explicitly forgotten promoted Session memory.</summary>
public sealed record MemoryForgotten(string CandidateId, string MemoryId, DateTimeOffset ForgottenAt)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.forgotten");
    public int SchemaVersion() => 1;
}

public sealed record MemoryUsed(TurnId TurnId, int ModelStepIndex, IReadOnlyList<string> MemoryIds,
    DateTimeOffset UsedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("memory.used");
    public int SchemaVersion() => 1;
}

public sealed record MemoryCandidateView(string Id, MemoryKind Kind, string Content, string Origin,
    string Decision, string? Reason, string? ConflictMemoryId, int Occurrences, DateTimeOffset ProposedAt);
