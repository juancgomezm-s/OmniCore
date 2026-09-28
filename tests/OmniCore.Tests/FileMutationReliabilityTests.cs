using OmniCore.Domain;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la base pura de FileMutationReliability (ADR-0007 §2, ADR-0044 §4): muestras
/// observables tipadas, evaluador determinista, agregación y muestra vacía.
/// </summary>
public sealed class FileMutationReliabilityTests
{
    private static readonly DateTimeOffset MeasuredAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static FileMutationSample GoodSample() => new("src/a.cs", 120, 0, true, true, true, true, 0, false);

    private static FileMutationSample BadSample() => new("src/a.cs", 40, 40, false, false, false, false, 2, true);

    [Fact]
    public void Evaluate_good_sample_scores_one()
    {
        var score = FileMutationEvaluator.Evaluate(GoodSample(), MeasuredAt);

        Assert.Equal(1.0, score.Value);
        Assert.Equal(1, score.SampleSize);
        Assert.Equal("empirical", score.Source);
        Assert.Equal(MeasuredAt, score.MeasuredAt);
        Assert.True(score.Confidence > 0 && score.Confidence <= 1);
    }

    [Fact]
    public void Evaluate_bad_sample_scores_zero()
    {
        var score = FileMutationEvaluator.Evaluate(BadSample(), MeasuredAt);

        Assert.Equal(0.0, score.Value);
        Assert.Equal(1, score.SampleSize);
    }

    [Fact]
    public void Evaluate_single_failure_dimension_scores_below_one()
    {
        // Un solo aspecto no favorable (p. ej. overwrite en vez de patch) reduce el valor pero no a cero.
        var sample = new FileMutationSample("src/a.cs", 120, 3, false, true, true, true, 0, false);

        var value = FileMutationEvaluator.Evaluate(sample, MeasuredAt).Value;
        Assert.True(value < 1.0, $"esperaba < 1.0, obtuvo {value}");
        Assert.True(value > 0.0, $"esperaba > 0.0, obtuvo {value}");
    }

    [Fact]
    public void Evaluate_large_diff_scores_worse_than_local_patch()
    {
        // Mismo archivo, mismo estado de flags: la única diferencia es ChangedLines.
        // Un diff grande (99 líneas) debe puntuar peor que un patch local (3 líneas).
        var smallDiff = new FileMutationSample("src/a.cs", 120, 3, false, true, true, true, 0, false);
        var largeDiff = new FileMutationSample("src/a.cs", 120, 99, false, true, true, true, 0, false);

        var smallValue = FileMutationEvaluator.SampleValue(smallDiff);
        var largeValue = FileMutationEvaluator.SampleValue(largeDiff);

        Assert.True(largeValue < smallValue,
            $"diff grande ({largeValue}) debe puntuar peor que diff local ({smallValue})");
    }

    [Fact]
    public void Evaluate_single_sample_never_gets_high_confidence_on_long_file()
    {
        // Un archivo de 10000 líneas con una sola muestra no debe dar confianza alta.
        var longFile = new FileMutationSample("src/big.cs", 10000, 3, true, true, true, true, 0, false);
        var score = FileMutationEvaluator.Evaluate(longFile, MeasuredAt);

        Assert.True(score.Confidence < 1.0,
            $"una sola muestra no debe dar confianza 1.0, obtuvo {score.Confidence}");
    }

    [Fact]
    public void Evaluate_rejects_negative_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileMutationEvaluator.SampleValue(new FileMutationSample("a.cs", -1, 10, true, true, true, true, 0, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileMutationEvaluator.SampleValue(new FileMutationSample("a.cs", 100, -10, true, true, true, true, 0, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileMutationEvaluator.SampleValue(new FileMutationSample("a.cs", 100, 10, true, true, true, true, -1, false)));
    }

    [Fact]
    public void Evaluate_delete_and_create_scores_worse_than_overwrite()
    {
        var overwrite = new FileMutationSample("src/a.cs", 100, 10, false, true, true, true, 0, false);
        var deleteCreate = new FileMutationSample("src/a.cs", 100, 10, false, true, true, true, 0, true);

        Assert.True(FileMutationEvaluator.SampleValue(deleteCreate) <
                    FileMutationEvaluator.SampleValue(overwrite));
    }

    [Fact]
    public void Evaluate_breaks_score_worse_than_no_breaks()
    {
        var noBreaks = new FileMutationSample("src/a.cs", 100, 10, false, true, true, true, 0, false);
        var twoBreaks = new FileMutationSample("src/a.cs", 100, 10, false, true, true, true, 2, false);

        Assert.True(FileMutationEvaluator.SampleValue(twoBreaks) <
                    FileMutationEvaluator.SampleValue(noBreaks));
    }

    [Fact]
    public void Aggregate_is_deterministic_and_accurate()
    {
        var samples = new[] { GoodSample(), BadSample(), GoodSample(), GoodSample() };
        var aggregates = samples.Select(FileMutationAggregate.Aggregate).ToList();

        var first = FileMutationEvaluator.Aggregate(aggregates, MeasuredAt);
        var second = FileMutationEvaluator.Aggregate(aggregates.ToList(), MeasuredAt);

        // Media de los valores de muestra: Good=1.0, Bad=0.0, Good=1.0, Good=1.0 -> 0.75.
        Assert.Equal(0.75, first.Value);
        Assert.Equal(4, first.SampleSize);
        Assert.Equal("empirical", first.Source);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.SampleSize, second.SampleSize);
    }

    [Fact]
    public void Aggregate_reflects_sample_values_not_just_favorable_count()
    {
        // Dos series con el mismo conteo de "favorables" (1 de 2) pero valores de muestra
        // distintos: una con diff local (3 líneas) y otra con diff grande (99 líneas).
        // El agregado debe diferir porque la media de SampleValue captura el tamaño del diff.
        var localDiff = new FileMutationSample("src/a.cs", 120, 3, false, true, true, true, 0, false);
        var largeDiff = new FileMutationSample("src/a.cs", 120, 99, false, true, true, true, 0, false);

        var localAggregates = new[] { GoodSample(), localDiff }.Select(FileMutationAggregate.Aggregate).ToList();
        var largeAggregates = new[] { GoodSample(), largeDiff }.Select(FileMutationAggregate.Aggregate).ToList();

        var localValue = FileMutationEvaluator.Aggregate(localAggregates, MeasuredAt).Value;
        var largeValue = FileMutationEvaluator.Aggregate(largeAggregates, MeasuredAt).Value;

        Assert.True(largeValue < localValue,
            $"agregado con diff grande ({largeValue}) debe ser menor que con diff local ({localValue})");
    }

    [Fact]
    public void Aggregate_single_sample_matches_Evaluate()
    {
        // Una única muestra: Evaluate y Aggregate deben coincidir en Value, Confidence y
        // SampleSize, porque la confianza depende de N (una muestra), no de la longitud.
        var sample = GoodSample();
        var single = FileMutationEvaluator.Evaluate(sample, MeasuredAt);
        var aggregated = FileMutationEvaluator.Aggregate(new[] { FileMutationAggregate.Aggregate(sample) }, MeasuredAt);

        Assert.Equal(single.Value, aggregated.Value);
        Assert.Equal(single.Confidence, aggregated.Confidence);
        Assert.Equal(single.SampleSize, aggregated.SampleSize);
    }

    [Fact]
    public void Aggregate_rejects_invalid_mean()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(1, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(1, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(1, double.NaN));
    }

    [Fact]
    public void Aggregate_rejects_overflowing_total()
    {
        var aggregate = new FileMutationAggregate(int.MaxValue + 1L, 0.5);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileMutationEvaluator.Aggregate(new[] { aggregate }, MeasuredAt));
    }

    [Fact]
    public void Aggregate_empty_sample_returns_empty_source()
    {
        var score = FileMutationEvaluator.Aggregate(Array.Empty<FileMutationAggregate>(), MeasuredAt);

        Assert.Equal(0, score.SampleSize);
        Assert.Equal(0.0, score.Value);
        Assert.Equal(0.0, score.Confidence);
        Assert.Equal("empty", score.Source);
    }

    [Fact]
    public void Aggregate_rejects_negative_total()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(-1, 0.5));
    }

    [Fact]
    public void TraitScore_rejects_out_of_range_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraitScore(1.5, 0.5, 1, "empirical", MeasuredAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraitScore(-0.1, 0.5, 1, "empirical", MeasuredAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraitScore(double.NaN, 0.5, 1, "empirical", MeasuredAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraitScore(0.5, 1.5, 1, "empirical", MeasuredAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraitScore(0.5, 0.5, -1, "empirical", MeasuredAt));
    }
}
