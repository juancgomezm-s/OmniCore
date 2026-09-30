using System;
using System.Threading.Tasks;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

public sealed class TokenEstimateCalibratorTests
{
    [Fact]
    public void Default_ratio_before_enough_samples()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 3.5, minimumSamples: 10);
        for (int i = 0; i < 9; i++)
            cal.AddSample("tok1", 100, 25); // 4.0 chars/token
        Assert.Equal(3.5, cal.CharsPerToken("tok1"));
        Assert.Equal(9, cal.SampleCount("tok1"));
    }

    [Fact]
    public void Calibrated_ratio_after_minimum_samples()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 5);
        // 5 samples: 100 chars / 25 tokens = 4.0 each
        for (int i = 0; i < 5; i++)
            cal.AddSample("tok1", 100, 25);
        Assert.Equal(4.0, cal.CharsPerToken("tok1"));
        Assert.Equal(5, cal.SampleCount("tok1"));

        // Different ratio: 200 chars / 50 tokens = 4.0
        cal.AddSample("tok1", 200, 50);
        Assert.Equal(4.0, cal.CharsPerToken("tok1"));
    }

    [Fact]
    public void Calibrated_ratio_different_from_default()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 3);
        // 3 samples at 3.0 chars/token
        cal.AddSample("tok1", 90, 30);  // 3.0
        cal.AddSample("tok1", 60, 20);  // 3.0
        cal.AddSample("tok1", 30, 10);  // 3.0
        Assert.Equal(3.0, cal.CharsPerToken("tok1"));
    }

    [Fact]
    public void Clamping_low_ratio()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 2.0, maxRatio: 8.0);
        // Very low ratio (1.0) should be clamped to minRatio (2.0)
        cal.AddSample("tok1", 10, 10); // 1.0
        cal.AddSample("tok1", 20, 20); // 1.0
        Assert.Equal(2.0, cal.CharsPerToken("tok1"));
    }

    [Fact]
    public void Clamping_high_ratio()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 1.5, maxRatio: 6.0);
        // Very high ratio (10.0) should be clamped to maxRatio (6.0)
        cal.AddSample("tok1", 100, 10); // 10.0
        cal.AddSample("tok1", 200, 20); // 10.0
        Assert.Equal(6.0, cal.CharsPerToken("tok1"));
    }

    [Fact]
    public void Tokenizers_are_independent()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 3);
        // tok1: 3 samples at 3.0
        cal.AddSample("tok1", 90, 30);
        cal.AddSample("tok1", 60, 20);
        cal.AddSample("tok1", 30, 10);
        // tok2: 3 samples at 5.0
        cal.AddSample("tok2", 100, 20);
        cal.AddSample("tok2", 150, 30);
        cal.AddSample("tok2", 50, 10);

        Assert.Equal(3.0, cal.CharsPerToken("tok1"));
        Assert.Equal(5.0, cal.CharsPerToken("tok2"));
        Assert.Equal(3, cal.SampleCount("tok1"));
        Assert.Equal(3, cal.SampleCount("tok2"));
    }

    [Fact]
    public void Invalid_samples_ignored()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 3);
        cal.AddSample("tok1", 100, 25); // valid
        cal.AddSample("tok1", 0, 25);    // ignored (chars <= 0)
        cal.AddSample("tok1", 100, 0);   // ignored (tokens <= 0)
        cal.AddSample("tok1", -10, 5);   // ignored
        cal.AddSample("tok1", 50, -1);   // ignored
        cal.AddSample("tok1", 50, 12);   // valid (4.166...)
        cal.AddSample("tok1", 50, 12);   // valid

        // Only 3 valid samples counted
        Assert.Equal(3, cal.SampleCount("tok1"));
        // Ratio from valid samples: (100+50+50) / (25+12+12) = 200/49 ≈ 4.08
        Assert.Equal(200.0 / 49.0, cal.CharsPerToken("tok1"));
    }

    [Fact]
    public void MinimumSamples_changes_behaviour()
    {
        var calLow = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2);
        var calHigh = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 5);

        calLow.AddSample("tok", 90, 30); // 3.0
        calLow.AddSample("tok", 60, 20); // 3.0
        calHigh.AddSample("tok", 90, 30);
        calHigh.AddSample("tok", 60, 20);

        // calLow has enough samples, uses calibrated 3.0
        Assert.Equal(3.0, calLow.CharsPerToken("tok"));
        // calHigh doesn't have enough, uses default 4.0
        Assert.Equal(4.0, calHigh.CharsPerToken("tok"));
    }

    [Fact]
    public void MinRatio_changes_behaviour()
    {
        var calLow = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 1.0, maxRatio: 8.0);
        var calHigh = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 3.0, maxRatio: 8.0);

        calLow.AddSample("tok", 10, 10);  // 1.0
        calLow.AddSample("tok", 20, 20);  // 1.0
        calHigh.AddSample("tok", 10, 10);
        calHigh.AddSample("tok", 20, 20);

        Assert.Equal(1.0, calLow.CharsPerToken("tok"));  // not clamped
        Assert.Equal(3.0, calHigh.CharsPerToken("tok")); // clamped to minRatio
    }

    [Fact]
    public void MaxRatio_changes_behaviour()
    {
        var calLow = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 1.5, maxRatio: 5.0);
        var calHigh = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 2, minRatio: 1.5, maxRatio: 10.0);

        calLow.AddSample("tok", 100, 10); // 10.0
        calLow.AddSample("tok", 200, 20); // 10.0
        calHigh.AddSample("tok", 100, 10);
        calHigh.AddSample("tok", 200, 20);

        Assert.Equal(5.0, calLow.CharsPerToken("tok"));  // clamped to maxRatio
        Assert.Equal(10.0, calHigh.CharsPerToken("tok")); // not clamped
    }

    [Fact]
    public void DefaultCharsPerToken_changes_behaviour()
    {
        var cal1 = new TokenEstimateCalibrator(defaultCharsPerToken: 3.0, minimumSamples: 5);
        var cal2 = new TokenEstimateCalibrator(defaultCharsPerToken: 5.0, minimumSamples: 5);

        // Only 3 samples, not enough for calibration
        cal1.AddSample("tok", 100, 25);
        cal1.AddSample("tok", 100, 25);
        cal1.AddSample("tok", 100, 25);
        cal2.AddSample("tok", 100, 25);
        cal2.AddSample("tok", 100, 25);
        cal2.AddSample("tok", 100, 25);

        Assert.Equal(3.0, cal1.CharsPerToken("tok"));
        Assert.Equal(5.0, cal2.CharsPerToken("tok"));
    }

    [Fact]
    public async Task Thread_safety_concurrent_add_and_read()
    {
        var cal = new TokenEstimateCalibrator(defaultCharsPerToken: 4.0, minimumSamples: 100);
        var tasks = new Task[20];
        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    cal.AddSample("tok", 100, 25);
                    _ = cal.CharsPerToken("tok");
                    _ = cal.SampleCount("tok");
                }
            }, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(tasks);
        Assert.Equal(1000, cal.SampleCount("tok"));
        Assert.Equal(4.0, cal.CharsPerToken("tok"));
    }

    [Fact]
    public void Empty_tokenizer_id_throws()
    {
        var cal = new TokenEstimateCalibrator();
        Assert.Throws<ArgumentException>(() => cal.AddSample("", 100, 25));
        Assert.Throws<ArgumentException>(() => cal.AddSample(null!, 100, 25));
        Assert.Throws<ArgumentException>(() => cal.CharsPerToken(""));
        Assert.Throws<ArgumentException>(() => cal.CharsPerToken(null!));
        Assert.Throws<ArgumentException>(() => cal.SampleCount(""));
        Assert.Throws<ArgumentException>(() => cal.SampleCount(null!));
    }

    [Fact]
    public void Constructor_validates_parameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(defaultCharsPerToken: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(defaultCharsPerToken: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(defaultCharsPerToken: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(minimumSamples: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(minimumSamples: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(minRatio: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(minRatio: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(minRatio: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(maxRatio: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(maxRatio: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenEstimateCalibrator(maxRatio: double.NaN));
        Assert.Throws<ArgumentException>(() => new TokenEstimateCalibrator(minRatio: 5.0, maxRatio: 3.0));
    }
}