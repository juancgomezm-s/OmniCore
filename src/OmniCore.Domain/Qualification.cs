namespace OmniCore.Domain;

/// <summary>
/// Rasgo empírico de cualificación (ADR-0007 §2). Value 0..1, confianza, tamaño de muestra,
/// fuente y momento de medición.
/// </summary>
public sealed class TraitScore
{
    public double Value { get; }
    public double Confidence { get; }
    public int SampleSize { get; }
    public string Source { get; }
    public DateTimeOffset MeasuredAt { get; }

    public TraitScore(double value, double confidence, int sampleSize, string source, DateTimeOffset measuredAt)
    {
        if (value < 0.0 || value > 1.0 || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "TraitScore.Value debe estar en 0..1 y ser finito");
        }
        if (confidence < 0.0 || confidence > 1.0 || !double.IsFinite(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "TraitScore.Confidence debe estar en 0..1 y ser finito");
        }
        if (sampleSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize), "TraitScore.SampleSize no puede ser negativo");
        }
        Value = value;
        Confidence = confidence;
        SampleSize = sampleSize;
        Source = source;
        MeasuredAt = measuredAt;
    }
}

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
/// Conserva el total de muestras y la media de sus valores de muestra (0..1); esa media es el
/// valor agregado, no un conteo binario de casos favorables, por lo que el tamaño del diff y la
/// severidad de cada muestra siguen reflejándose en el resultado. El total es long para no
/// desbordar int en acumulaciones grandes; el constructor valida que el valor agregado sea
/// finito y esté en 0..1.
/// </summary>
public sealed class FileMutationAggregate
{
    public long Total { get; }
    public double Mean { get; }

    public FileMutationAggregate(long total, double mean)
    {
        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "agregado con total negativo");
        }
        if (!double.IsFinite(mean) || mean < 0.0 || mean > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(mean), "agregado con media inválida");
        }
        Total = total;
        Mean = mean;
    }

    public static FileMutationAggregate Aggregate(FileMutationSample s)
        => new(1, FileMutationEvaluator.SampleValue(s));
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
        // solo del número de muestras (N). Un solo dato nunca da alta confianza.
        double confidence = Confidence(1);

        return new TraitScore(value, confidence, 1, "empirical", measuredAt);
    }

    /// <summary>
    /// Confianza de un TraitScore en función del número de muestras N, no de la longitud del
    /// archivo ni del tamaño del diff. Crece linealmente hasta 1.0 con 10 o más muestras.
    /// </summary>
    private static double Confidence(long n)
    {
        if (n <= 0) return 0.0;
        return Math.Min(1.0, (double)n / 10.0);
    }

    public static TraitScore Aggregate(IReadOnlyList<FileMutationAggregate> aggregates,
                                       DateTimeOffset measuredAt)
    {
        long total = 0;
        double sum = 0.0;
        foreach (var a in aggregates)
        {
            total += a.Total;
            sum += a.Mean * (double)a.Total;
        }
        if (total == 0)
        {
            return new TraitScore(0.0, 0.0, 0, "empty", measuredAt);
        }
        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregates), "total de muestras excede int.MaxValue");
        }
        double value = sum / total;
        return new TraitScore(value, Confidence(total), (int)total, "empirical", measuredAt);
    }
}
