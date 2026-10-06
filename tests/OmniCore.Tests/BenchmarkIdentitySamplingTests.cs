using OmniCore.Domain;

namespace OmniCore.Tests;

public sealed class BenchmarkIdentitySamplingTests
{
    [Fact]
    public void Explicit_zero_sampling_values_remain_known_and_legacy_overload_preserves_them()
    {
        // The legacy int/double signature means explicitly supplied values, not absent fields.
        var legacy = new BenchmarkIdentity("quick", "1.0.0", "task-set-hash", 0, 0.0, "fixture-version");
        var nullable = new BenchmarkIdentity("quick", "1.0.0", "task-set-hash",
            (int?)0, (double?)0.0, "fixture-version");

        Assert.Equal<int?>(0, legacy.Seed);
        Assert.Equal<double?>(0.0, legacy.Temperature);
        Assert.Equal(legacy.Seed, nullable.Seed);
        Assert.Equal(legacy.Temperature, nullable.Temperature);
        Assert.Equal("task-set-hash", nullable.TaskSetHash);
    }

    [Fact]
    public void Missing_sampling_values_are_null_and_known_nullable_temperature_is_preserved()
    {
        var absent = new BenchmarkIdentity("quick", "1.0.0", "task-set-hash",
            seed: null, temperature: null, omniCoreVersion: "fixture-version");
        var knownTemperatureOnly = new BenchmarkIdentity("quick", "1.0.0", "task-set-hash",
            seed: null, temperature: 0.7, omniCoreVersion: "fixture-version");

        Assert.Null(absent.Seed);
        Assert.Null(absent.Temperature);
        Assert.Null(knownTemperatureOnly.Seed);
        Assert.Equal<double?>(0.7, knownTemperatureOnly.Temperature);
    }
}
