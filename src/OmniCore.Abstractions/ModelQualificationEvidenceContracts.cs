namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Durable audit evidence for a profile revision. Stale aliases preserve the
/// revision that actually ran the benchmark; legacy profiles can have no evidence.</summary>
public sealed record ModelQualificationEvidence(long ProfileRevision, long SourceRunRevision,
    ArtifactRef Artifact);

/// <summary>Additive capability: commits profile, traits and verified User CAS reference
/// atomically. Publication precedes this call; a missing blob is an error, never success.</summary>
public interface IModelQualificationEvidenceStore
{
    ModelQualificationProfile UpsertWithTraitsAndEvidence(ModelQualificationKey key,
        long expectedRevision, ModelQualificationState state, string suiteId, string suiteVersion,
        IReadOnlyList<ModelTraitRecord> traits, ArtifactRef artifact,
        CancellationToken cancellationToken);

    ModelQualificationEvidence? Evidence(ModelQualificationKey key, long profileRevision,
        CancellationToken cancellationToken);
}
