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
    IReadOnlyList<PlanMutation> ProposedPlanMutations) { }

/// <summary>Evidencia puntual de un hallazgo (spec §16).</summary>
public record Finding(string Title, string? Detail, ArtifactRef? Evidence) { }

/// <summary>Resultado de una validación de Task (spec §16, Completion Pipelines).</summary>
public record ValidationResult(bool Passed, IReadOnlyList<string> GateResults, IReadOnlyList<string> Issues) { }