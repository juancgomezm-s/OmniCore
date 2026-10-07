using System.Globalization;
using OmniCore.Domain;
using OmniCore.Qualification;

namespace OmniCore.Tests;

/// <summary>
/// Cubre la expansión de quick a diez fixtures exactos y deja expresados sus límites: la suite
/// actual no mide tool calling, coding, recuperación, planificación ni mutación de archivos.
/// </summary>
public sealed class M5QualificationQuickSuiteCoverageTests
{
    [Fact]
    public void Quick_has_ten_unique_deterministic_probes_and_no_unmeasured_capability_claims()
    {
        var first = QuickProbeSuite.Probes();
        var second = QuickProbeSuite.Probes();

        Assert.Equal(10, first.Count);
        Assert.Equal(10, first.Select(probe => probe.Id.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(first.Select(probe => probe.Id.ToString()), second.Select(probe => probe.Id.ToString()));
        Assert.Equal(QuickProbeSuite.SuiteId, "omnicore-quick");
        Assert.Equal(QuickProbeSuite.SuiteVersion, "1.1.1");
        Assert.All(first, probe => Assert.True(ProbeScorer.Score(probe.Kind, probe.Expected, probe.Expected) == 1.0));

        Assert.DoesNotContain(first, probe => probe.Kind.ToString() is "ToolCall" or "Coding"
            or "ToolErrorRecovery" or "PlanTracking" or "FileMutation");
    }

    [Fact]
    public void Task_set_hash_separates_delimiters_and_newlines_in_prompt_and_expected()
    {
        var first = Probe("a\u0001b", "c\nnext");
        var second = Probe("a", "b\u0001c\nnext");

        Assert.NotEqual(Hash(first), Hash(second));
        Assert.NotEqual(Hash(Probe("prompt", "expected\nline1")), Hash(Probe("prompt", "expected\nline2")));
    }

    [Fact]
    public void Task_set_hash_changes_for_prompt_expected_kind_and_cost()
    {
        var original = Probe("prompt", "expected", ProbeKind.Reading, 1m);

        Assert.NotEqual(Hash(original), Hash(Probe("prompt changed", "expected", ProbeKind.Reading, 1m)));
        Assert.NotEqual(Hash(original), Hash(Probe("prompt", "expected changed", ProbeKind.Reading, 1m)));
        Assert.NotEqual(Hash(original), Hash(Probe("prompt", "expected", ProbeKind.Reasoning, 1m)));
        Assert.NotEqual(Hash(original), Hash(Probe("prompt", "expected", ProbeKind.Reading, 2m)));
    }

    [Fact]
    public void Task_set_hash_is_order_independent_and_decimal_culture_and_scale_canonical()
    {
        var first = Probe("first", "one", ProbeKind.Reading, 1m, "first-probe");
        var second = Probe("second", "two", ProbeKind.StructuredOutput, 2.5m, "second-probe");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = Hash(first, second);
            var reordered = Hash(second, first);
            var scaled = Hash(Probe("first", "one", ProbeKind.Reading, 1.00m, "first-probe"), second);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = Hash(first, second);

            Assert.Equal(french, reordered);
            Assert.Equal(french, scaled);
            Assert.Equal(french, english);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static Probe Probe(string prompt, string expected, ProbeKind kind = ProbeKind.Reading,
        decimal cost = 0m, string id = "hash-fixture") => new(ProbeId.WellKnown(id), kind, prompt, expected, cost);

    private static string Hash(params Probe[] probes) => ProbeScorer.TaskSetHash(probes);
}
