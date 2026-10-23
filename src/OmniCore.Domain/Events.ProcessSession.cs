namespace OmniCore.Domain;

/// <summary>Durable process lifecycle evidence. Stream bytes remain in the bounded live buffer;</summary>
/// <summary>this event records only identity, cursor, terminal state and a protected proposal reference.</summary>
public sealed record ProcessSessionStateRecorded(string HandleId, string State, string RunId, string LaneId,
    string LeaseId, long LastOutputSequence, int? ExitCode, string? ProposalId, string? Diagnostic,
    string? ProposalArtifactHash = null, long? ProposalArtifactSize = null, string? SourceCallId = null,
    string? SourceToolId = null, string? TurnId = null, string? WorkspaceRoot = null)
    : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("process.session_state.recorded");
    public int SchemaVersion() => 2;

    public void Validate()
    {
        if (!Guid.TryParseExact(HandleId, "N", out _) || !Guid.TryParseExact(RunId, "D", out _)
            || !Guid.TryParseExact(LaneId, "D", out _) || !Guid.TryParseExact(LeaseId, "N", out _)
            || LastOutputSequence is < 0 or > 32 * 1_048_576
            || State is not ("starting" or "running" or "stopping" or "exited" or "stopped" or "cancelled"
                or "failed" or "timed_out" or "output_limit" or "unknown")
            || ProposalArtifactSize is < 1 or > 16 * 1024 * 1024
            || (ProposalArtifactHash is null) != (ProposalArtifactSize is null)
            || (ProposalId is null) != (ProposalArtifactHash is null)
            || ProposalArtifactHash is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            || SourceCallId is { } sourceCall && !Guid.TryParse(sourceCall, out _)
            || SourceToolId is { Length: > 128 } || TurnId is { Length: > 128 }
            || WorkspaceRoot is { Length: > 4096 } || Diagnostic is { Length: > 512 }
            || (SourceCallId is null) != (SourceToolId is null)
            || (SourceCallId is null) != (TurnId is null)
            || (SourceCallId is null) != (WorkspaceRoot is null))
            throw new ArgumentException("Invalid durable process session state.");
        if (ProposalId is not null && (!Guid.TryParseExact(ProposalId, "N", out _)
            || ProposalArtifactHash is null || SourceCallId is null
            || SourceToolId is not ("process.start" or "process.exec" or "shell.exec")
            || WorkspaceRoot is null || TurnId is null || State is not ("exited" or "stopped")))
            throw new ArgumentException("Invalid durable process proposal reference.");
    }
}
