namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Evidencia de <c>FileMutationReliability</c> medida en uso real (ADR-0044 §4, ADR-0007 §2).
/// El <c>MutationLedger</c> observa las mutaciones de un Run ACT; este agregador las suma con
/// <see cref="FileMutationEvaluator"/> al trait del perfil de cualificación de esa configuración
/// exacta. Solo se registra sobre un perfil existente y utilizable (Qualified, Calibrated o Stale):
/// sin cualificación previa no hay dónde anclar la evidencia, y la recomendación necesita la suite
/// quick de todos modos. Nunca amplía la política del usuario: solo alimenta una recomendación.
/// </summary>
public static class MutationEvidenceRecorder
{
    public const string TraitName = "FileMutationReliability";

    /// <summary>Fuente del trait: medido en uso real, no por una suite.</summary>
    public const string Source = "observed";

    /// <summary>Agrega <paramref name="samples"/> al trait del perfil; null si no hay dónde anclarlas.</summary>
    public static TraitScore? Record(IModelQualificationStore store, ModelQualificationKey key,
        IReadOnlyList<FileMutationSample> samples, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return null;
        var profile = store.Get(key, cancellationToken);
        if (profile is null || profile.State is not (ModelQualificationState.Qualified
            or ModelQualificationState.Calibrated or ModelQualificationState.Stale))
            return null;

        var traits = store.Traits(key, profile.ProfileRevision, cancellationToken);
        var aggregates = new List<FileMutationAggregate>();
        if (traits.FirstOrDefault(trait => trait.Trait == TraitName) is { } previous)
            aggregates.Add(new FileMutationAggregate(previous.Samples, previous.Value));
        aggregates.AddRange(samples.Select(FileMutationAggregate.Aggregate));

        var score = FileMutationEvaluator.Aggregate(aggregates, now);
        var hash = key.QualificationKeyHash();
        var merged = traits.Where(trait => trait.Trait != TraitName)
            .Append(new ModelTraitRecord(hash, profile.ProfileRevision, TraitName, score.Value, score.Confidence,
                score.SampleSize, Source))
            .ToArray();
        store.SaveTraits(key, profile.ProfileRevision, merged, cancellationToken);
        return score;
    }
}
