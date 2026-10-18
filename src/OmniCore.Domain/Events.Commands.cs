namespace OmniCore.Domain;

/// <summary>
/// Durable admission for a native human command. Sensitive invocation arguments and expanded
/// output are held in a protected artifact; the journal stores only identity and CAS facts.
/// </summary>
public sealed record NativeCommandInvocationAdmitted(
    CommandId InvocationId,
    SessionId SessionId,
    RunId? RunId,
    string CanonicalCommandId,
    string Kind,
    string CatalogRevision,
    string SourceFingerprint,
    long ExpectedPolicyRevision,
    long? ExpectedModeRevision,
    int ExpectedSelectionRevision,
    long? ExpectedRoutingPolicyRevision,
    ArtifactRef ProtectedPayload) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("command.invocation_admitted");
    public int SchemaVersion() => 1;

    public void Validate()
    {
        if (InvocationId.Value == Guid.Empty || SessionId.Value == Guid.Empty
            || RunId is { Value: var run } && run == Guid.Empty
            || string.IsNullOrWhiteSpace(CanonicalCommandId) || CanonicalCommandId.Length > 256
            || !CanonicalCommandId.Contains(':', StringComparison.Ordinal)
            || Kind is not ("prompt" or "forcedExtension" or "compiledWorkflow")
            || !IsSha256(CatalogRevision) || !IsSha256(SourceFingerprint)
            || ExpectedPolicyRevision < 1 || ExpectedModeRevision is < 1
            || ExpectedSelectionRevision < 0 || ExpectedRoutingPolicyRevision is < 0
            || ProtectedPayload.Kind != ArtifactKind.Other || ProtectedPayload.Sensitivity != Sensitivity.Sensitive
            || ProtectedPayload.Redacted || ProtectedPayload.Size is <= 0 or > 131_072)
            throw new InvalidDataException("Native command admission is invalid.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

/// <summary>Marks the one durable native-command continuation as potentially externally active.</summary>
public sealed record NativeCommandInvocationStarted(CommandId InvocationId, string Kind, RunId? RunId = null) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("command.invocation_started");
    public int SchemaVersion() => 1;

    public void Validate()
    {
        if (InvocationId.Value == Guid.Empty || Kind is not ("prompt" or "forcedExtension" or "compiledWorkflow")
            || RunId is { Value: var run } && run == Guid.Empty)
            throw new InvalidDataException("Native command execution start is invalid.");
    }
}

/// <summary>Closes a native command invocation only after its real Core pipeline returns.</summary>
public sealed record NativeCommandInvocationFinished(CommandId InvocationId, string OutcomeCode, RunId? RunId = null)
    : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("command.invocation_finished");
    public int SchemaVersion() => 1;

    public void Validate()
    {
        if (InvocationId.Value == Guid.Empty || OutcomeCode is not ("completed" or "deferred" or "failed" or "cancelled")
            || RunId is { Value: var run } && run == Guid.Empty)
            throw new InvalidDataException("Native command completion is invalid.");
    }
}
