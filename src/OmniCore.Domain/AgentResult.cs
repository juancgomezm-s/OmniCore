namespace OmniCore.Domain;

/// <summary>Outcome de un agente (subagente o Lane raíz) (ADR-0041 §3, spec §16).</summary>
public enum AgentOutcome
{
    Succeeded,
    Failed,
    Blocked,
    Cancelled,
}

/// <summary>Confianza en un resultado declarado.</summary>
public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Resultado declarado por un agente al terminar (spec §16, ADR-0041 §3).</summary>
public record AgentResult(
    AgentOutcome Outcome,
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<ArtifactRef> ArtifactRefs,
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> RemainingIssues,
    ConfidenceLevel Confidence,
    IReadOnlyList<PlanMutation> ProposedPlanMutations)
{
    private IReadOnlyList<string> _findings = Freeze(Findings);
    private IReadOnlyList<ArtifactRef> _artifactRefs = Freeze(ArtifactRefs);
    private IReadOnlyList<string> _filesChanged = Freeze(FilesChanged);
    private IReadOnlyList<string> _remainingIssues = Freeze(RemainingIssues);
    private IReadOnlyList<PlanMutation> _proposedPlanMutations = Freeze(ProposedPlanMutations);

    public IReadOnlyList<string> Findings { get => _findings; init => _findings = Freeze(value); }
    public IReadOnlyList<ArtifactRef> ArtifactRefs { get => _artifactRefs; init => _artifactRefs = Freeze(value); }
    public IReadOnlyList<string> FilesChanged { get => _filesChanged; init => _filesChanged = Freeze(value); }
    public IReadOnlyList<string> RemainingIssues { get => _remainingIssues; init => _remainingIssues = Freeze(value); }
    public IReadOnlyList<PlanMutation> ProposedPlanMutations { get => _proposedPlanMutations; init => _proposedPlanMutations = Freeze(value); }

    // Preserve legacy null, rather than silently turning unknown data into an empty list.
    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values) =>
        values is null ? null! : Array.AsReadOnly(values.ToArray());
}

/// <summary>Evidencia puntual de un hallazgo (spec §16).</summary>
public record Finding(string Title, string? Detail, ArtifactRef? Evidence) { }

/// <summary>Resultado de una validación de Task (spec §16, Completion Pipelines).</summary>
public record ValidationResult(bool Passed, IReadOnlyList<string> GateResults, IReadOnlyList<string> Issues) { }
