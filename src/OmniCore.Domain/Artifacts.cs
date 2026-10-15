namespace OmniCore.Domain;

/// <summary>Identidad física de un blob en el Artifact Store (ADR-0001 §4).</summary>
public record ContentHash(string Algorithm, string Value)
{
    public static ContentHash Sha256(string hexLower) => new("sha256", hexLower);

    public override string ToString() => Algorithm + ":" + Value;
}

/// <summary>Sensibilidad de un artifact (ADR-0001 §9).</summary>
public enum Sensitivity
{
    Normal,
    Sensitive,
}

/// <summary>Clasificación lógica del contenido de un artifact.</summary>
public enum ArtifactKind
{
    ModelResponse,
    ContextSnapshot,
    ToolOutput,
    ProcessOutput,
    Patch,
    ProviderOpaqueState,
    Transcript,
    Other,
}

/// <summary>Referencia durable a un artifact, inmutable salvo <c>state</c> (ADR-0001 §4).</summary>
public record ArtifactRef(
    ArtifactId Id,
    ContentHash Hash,
    long Size,
    string MediaType,
    ArtifactKind Kind,
    Sensitivity Sensitivity,
    bool Redacted = false)
{
    /// <summary>Safe basename for user-supplied immutable attachments; never a source path.</summary>
    public string? FileName { get; init; }

    /// <summary>Immutable extracted-text snapshot linked to its original document attachment.</summary>
    public ArtifactRef? ExtractedTextSnapshot { get; init; }

    /// <summary>Stable extractor implementation/version identity for replay across upgrades.</summary>
    public string? ExtractorId { get; init; }
}

/// <summary>Tamaño de contexto usable y presupuesto de tokens de una política (spec §26, ADR-0042).</summary>
public record ContextBudget(
    long UsableTokens,
    long ReservedWorkingState,
    long ReservedSystemAndTask,
    int SlackPercent) { }
