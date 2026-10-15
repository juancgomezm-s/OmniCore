namespace OmniCore.Infrastructure;

using OmniCore.Domain;

/// <summary>Durable command receipt state. Unknown means the reservation survived but no ack did.</summary>
public enum CommandReceiptState
{
    Reserved,
    Unknown,
    Completed,
    IdConflict,
}

/// <summary>Redacted acknowledgement facts; request bodies and free-form error/reason text are excluded.</summary>
public sealed record CommandReceipt(
    string CommandId,
    string RequestHash,
    CommandReceiptState State,
    string? SessionId,
    string? AckStatus,
    string? OutcomeKind,
    string? OutcomeReason,
    long? FirstSequence,
    long? LastSequence);

public sealed record CommandReceiptReservation(CommandReceiptState State, CommandReceipt Receipt);

public sealed record EventReplayPage(long RequestedSequence, long NextSequence, long HighWater,
    bool HasGap, bool Complete, IReadOnlyList<DomainEvent> Events);

/// <summary>Raised before a persistent mutation when another live process owns this Session.</summary>
public sealed class SessionWriterLeaseUnavailableException(SessionId sessionId)
    : IOException("Another process currently owns the writer lock for this Session.")
{
    public SessionId SessionId { get; } = sessionId;
}
