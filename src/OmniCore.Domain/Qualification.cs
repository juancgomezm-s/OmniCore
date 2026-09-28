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
    /// <summary>
    /// Calcula el valor de una muestra (0..1). Penaliza severidad: delete+create y roturas
    /// cuestan más que un simple flag desfavorable, y el tamaño del diff reduce el score
    /// proporcionalmente cuando el patch no es preferido.
    /// </summary>
    public static double SampleValue(FileMutationSample sample)
    {
        if (sample.OriginalLines < 0 || sample.ChangedLines < 0 || sample.BreakCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "muestra con valores negativos");

        double value = 1.0;

        // Fallos binarios: cada uno reduce el valor de forma proporcional.
        if (!sample.UnrelatedContentPreserved) value -= 0.20;
        if (!sample.WithinScope) value -= 0.20;
        if (!sample.VersionTokenRespected) value -= 0.20;
        if (!sample.PatchPreferred) value -= 0.10;

        // Tamaño del diff: cuanto más grande, peor. Penalidad proporcional al ratio
        // ChangedLines / OriginalLines, con tope. Solo se aplica si hay líneas originales.
        if (sample.OriginalLines > 0 && sample.ChangedLines > 0)
        {
            double ratio = (double)sample.ChangedLines / sample.OriginalLines;
            value -= Math.Min(0.30, ratio * 0.30);
        }

        // Roturas de parse/build/test: penalidad acumulativa (máx 0.40).
        value -= Math.Min(0.40, sample.BreakCount * 0.20);

        // Delete+create: penalidad severa adicional.
        if (sample.DeleteAndCreateAttempt) value -= 0.30;

        return Math.Clamp(value, 0.0, 1.0);
    }

    public static TraitScore Evaluate(FileMutationSample sample, DateTimeOffset measuredAt)
    {
        double value = SampleValue(sample);
        // Confianza de una única muestra: no depende del tamaño del archivo (OriginalLines),
        // solo del hecho de que hay una muestra válida. Un solo dato nunca da alta confianza.
        double confidence = 0.5;

        return new TraitScore(value, confidence, 1, "empirical", measuredAt);
    }

    public static TraitScore Aggregate(IReadOnlyList<FileMutationAggregate> aggregates,
                                       DateTimeOffset measuredAt)
    {
        long total = 0;
        long favorable = 0;
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
        // La confianza depende del número de muestras, no del tamaño del archivo.
        double confidence = Math.Min(1.0, total / 10.0);
        return new TraitScore(value, confidence, (int)total, "empirical", measuredAt);
    }
}
