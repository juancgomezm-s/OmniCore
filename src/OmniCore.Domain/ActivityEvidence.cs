namespace OmniCore.Domain;

/// <summary>Core executor evidence, separate from model-visible output. Absence means unknown.
/// Test counts come from a fresh machine-readable runner report; commits from verified Git objects.</summary>
public sealed record ActivityEvidence(string Kind, bool Verified, bool Completed, int ExitCode,
    int Passed = 0, int Failed = 0, string? BeforeRevision = null, string? AfterRevision = null);

public sealed record GitActivitySnapshot(string Revision, string Tree, IReadOnlyList<string> Parents);
