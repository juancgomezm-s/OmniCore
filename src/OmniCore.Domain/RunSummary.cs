namespace OmniCore.Domain;

/// <summary>Historical facts from a terminal Run, never execution authority or WorkingState.</summary>
public sealed record RunSummary(SessionId SessionId, RunId RunId, long ThroughEventSequence,
    string Outcome, string Objective, IReadOnlyList<string> UserMessages, string FinalResponse,
    IReadOnlyList<string> PlanItems, string Checkpoint, bool Truncated);

/// <summary>Durable root for the structured, redacted fallback required by ADR-0035.</summary>
public record RunSummaryRecorded(RunId RunId, long ThroughEventSequence, ArtifactRef SummaryArtifact)
    : DomainEventPayload
{
    public EventType Type() => EventType.Of("run.summary_recorded");
    public int SchemaVersion() => 1;
}
