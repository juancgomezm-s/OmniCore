namespace OmniCore.Domain;

/// <summary>
/// Rasgo empírico de cualificación (ADR-0007 §2). Value 0..1, confianza, tamaño de muestra,
/// fuente y momento de medición.
/// </summary>
public sealed record TraitScore(
    double Value,
    double Confidence,
    int SampleSize,
    string Source,
    DateTimeOffset MeasuredAt);

/// <summary>
/// Muestra observable de una métrica de mutación (ADR-0044 §4). Es el dato crudo, tipado y
/// determinista: el evaluador lo convierte en un TraitScore (ADR-0007 §2).
/// </summary>
public sealed record FileMutationSample(
    string Path,
    int OriginalLines,
    int ChangedLines,
    bool PatchPreferred,
    bool UnrelatedContentPreserved,
    bool VersionTokenRespected,
    bool WithinScope,
    int BreakCount,
    bool DeleteAndCreateAttempt);

/// <summary>
/// Agregación determinista de muestras de una métrica de mutación (ADR-0044 §4).
/// El total de muestras y el conteo de casos favorables bastan para recomponer el score
/// sin recomputar el historial.
/// </summary>
public sealed class FileMutationAggregate
{
    public int Total { get; }
    public int Favorable { get; }

    public FileMutationAggregate(int total, int favorable)
    {
        if (total < 0 || favorable < 0 || favorable > total)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "agregado inválido");
        }
        Total = total;
        Favorable = favorable;
    }

    public static FileMutationAggregate Aggregate(FileMutationSample s)
        => new(1, s.UnrelatedContentPreserved && s.WithinScope && s.VersionTokenRespected
                    && s.PatchPreferred && s.BreakCount == 0 && !s.DeleteAndCreateAttempt ? 1 : 0);
}

/// <summary>
/// Evaluador puro y determinista: convierte una muestra de FileMutationReliability en un
/// TraitScore (ADR-0007 §2). No usa LLM, no escribe repos reales, no ejecuta probes pagos.
/// </summary>
public static class FileMutationEvaluator
{
    public static TraitScore Evaluate(FileMutationSample sample, DateTimeOffset measuredAt)
    {
        var favorable = sample.UnrelatedContentPreserved
                       && sample.WithinScope
                       && sample.VersionTokenRespected
                       && sample.PatchPreferred
                       && sample.BreakCount == 0
                       && !sample.DeleteAndCreateAttempt;

        var value = favorable ? 1.0 : 0.0;
        var confidence = Math.Min(1.0, (double)sample.OriginalLines / 100.0);

        return new TraitScore(value, confidence, 1, "empirical", measuredAt);
    }

    public static TraitScore Aggregate(IReadOnlyList<FileMutationAggregate> aggregates,
                                       DateTimeOffset measuredAt)
    {
        int total = 0;
        int favorable = 0;
        foreach (var a in aggregates)
        {
            total += a.Total;
            favorable += a.Favorable;
        }
        if (total == 0)
        {
            return new TraitScore(0.0, 0.0, 0, "empty", measuredAt);
        }
        double value = (double)favorable / total;
        double confidence = Math.Min(1.0, total / 10.0);
        return new TraitScore(value, confidence, total, "empirical", measuredAt);
    }
}
