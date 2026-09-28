using OmniCore.Domain;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la base pura de FileMutationReliability (ADR-0007 §2, ADR-0044 §4): muestras
/// observables tipadas, evaluador determinista, agregación y muestra vacía.
/// </summary>
public sealed class FileMutationReliabilityTests
{
    private static readonly DateTimeOffset MeasuredAt = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static FileMutationSample GoodSample() => new("src/a.cs", 120, 3, true, true, true, true, 0, false);

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
    public void Evaluate_single_failure_dimension_scores_zero()
    {
        // Un solo aspecto no favorable (p. ej. overwrite en vez de patch) basta para 0 en la muestra.
        var sample = new FileMutationSample("src/a.cs", 120, 3, false, true, true, true, 0, false);

        Assert.Equal(0.0, FileMutationEvaluator.Evaluate(sample, MeasuredAt).Value);
    }

    [Fact]
    public void Aggregate_is_deterministic_and_accurate()
    {
        var samples = new[] { GoodSample(), BadSample(), GoodSample(), GoodSample() };
        var aggregates = samples.Select(FileMutationAggregate.Aggregate).ToList();

        var first = FileMutationEvaluator.Aggregate(aggregates, MeasuredAt);
        var second = FileMutationEvaluator.Aggregate(aggregates.ToList(), MeasuredAt);

        Assert.Equal(3 / 4.0, first.Value);
        Assert.Equal(4, first.SampleSize);
        Assert.Equal("empirical", first.Source);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.SampleSize, second.SampleSize);
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
    public void Aggregate_rejects_invalid_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMutationAggregate(-1, 0));
    }
}
