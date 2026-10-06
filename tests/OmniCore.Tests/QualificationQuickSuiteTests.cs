using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using Task = System.Threading.Tasks.Task;
using OmniCore.Domain;
using OmniCore.Qualification;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la suite `quick` de cualificación (ADR-0007 §6, M5): runner determinista,
/// puntuación por reglas exactas, consentimiento explícito, tope de costo, reproducibilidad,
/// error y timeout. Solo usa un fake provider local: nunca llama a un servicio real.
/// </summary>
public sealed class QualificationQuickSuiteTests
{
    private static ModelSelection LocalSelection() =>
        new(new ModelIdValue("fake-model"), 8192, ToolMode.Direct, null);

    private static ProbeRequest Request(Probe probe) => new(probe, LocalSelection());

    // ---- Fake provider local ----

    private sealed class FakeProvider : IModelProvider
    {
        private readonly Dictionary<string, string?> _outputsByPrompt;

        public FakeProvider(Dictionary<string, string?> outputsByPrompt) => _outputsByPrompt = outputsByPrompt;

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prompt = (request.Messages[0].Content[0] as TextBlock)?.Text ?? "";
            var output = _outputsByPrompt.GetValueOrDefault(prompt);

            await System.Threading.Tasks.Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                new[] { new TextBlock(output ?? "") },
                StopReason.EndTurn,
                new TokenUsage(10, 5, 0, 0, 0),
                State: null,
                Metadata: new ProviderMetadata("req-1", "fake-model", null)));
        }
    }

    private static Dictionary<string, string?> PromptOutputs(IEnumerable<Probe> probes, params string[] answers)
    {
        var dict = new Dictionary<string, string?>();
        var i = 0;
        foreach (var p in probes)
        {
            dict[p.Prompt] = i < answers.Length ? answers[i] : null;
            i++;
        }
        return dict;
    }

    // ---- Scoring: reglas exactas por tipo ----

    [Fact]
    public void Reading_probe_scores_exact_text_case_insensitive_and_trimmed()
    {
        Assert.Equal(1.0, ProbeScorer.Score(ProbeKind.Reading, "  FOX \n", "fox"));
        Assert.Equal(1.0, ProbeScorer.Score(ProbeKind.Reading, "fox", "fox"));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.Reading, "dog", "fox"));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.Reading, null, "fox"));
    }

    [Fact]
    public void Reasoning_probe_scores_exact_text()
    {
        Assert.Equal(1.0, ProbeScorer.Score(ProbeKind.Reasoning, "42", "42"));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.Reasoning, "42 tokens", "42"));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.Reasoning, null, "42"));
    }

    [Fact]
    public void Structured_output_probe_scores_exact_json_ignoring_key_order()
    {
        var expected = "{\"status\":\"ok\",\"count\":1}";

        Assert.Equal(1.0, ProbeScorer.Score(ProbeKind.StructuredOutput, "{\"status\":\"ok\",\"count\":1}", expected));
        Assert.Equal(1.0, ProbeScorer.Score(ProbeKind.StructuredOutput, "{\"count\":1,\"status\":\"ok\"}", expected));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.StructuredOutput, "{\"status\":\"ok\",\"count\":2}", expected));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.StructuredOutput, "no json", expected));
        Assert.Equal(0.0, ProbeScorer.Score(ProbeKind.StructuredOutput, null, expected));
    }

    // ---- Runner: puntuación end-to-end con fake ----

    [Fact]
    public async Task Runner_passes_all_quick_probes_when_answers_are_exact()
    {
        var outputs = PromptOutputs(QuickProbeSuite.Probes(),
            QuickProbeSuite.Probes().Select(p => p.Expected).ToArray());
        var runner = new ProbeRunner(new FakeProvider(outputs));
        var requests = QuickProbeSuite.Probes().Select(Request).ToList();

        var results = await runner.RunSuiteAsync(requests, QualificationConsent.Local(), CancellationToken.None);

        Assert.Equal(10, results.Count);
        foreach (var r in results)
        {
            Assert.Equal(ProbeStatus.Passed, r.Status);
            Assert.Equal(1.0, r.Score);
        }
    }

    [Fact]
    public async Task Runner_scores_failed_probe_when_answer_is_wrong()
    {
        var outputs = PromptOutputs(QuickProbeSuite.Probes(), "dog");
        var runner = new ProbeRunner(new FakeProvider(outputs));
        var requests = QuickProbeSuite.Probes().Select(Request).ToList();

        var results = await runner.RunSuiteAsync(requests, QualificationConsent.Local(), CancellationToken.None);

        var reading = results.First(r => r.Id.ToString() == "reading-basic");
        Assert.Equal(ProbeStatus.Failed, reading.Status);
        Assert.Equal(0.0, reading.Score);
    }

    // ---- Reproducibilidad ----

    [Fact]
    public async Task Same_probes_and_answers_are_reproducible()
    {
        var outputs = PromptOutputs(QuickProbeSuite.Probes(), "fox", "42", "{\"status\":\"ok\",\"count\":1}");
        var runner = new ProbeRunner(new FakeProvider(outputs));
        var requests = QuickProbeSuite.Probes().Select(Request).ToList();

        var first = await runner.RunSuiteAsync(requests, QualificationConsent.Local(), CancellationToken.None);
        var second = await runner.RunSuiteAsync(requests, QualificationConsent.Local(), CancellationToken.None);

        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Id.ToString(), second[i].Id.ToString());
            Assert.Equal(first[i].Status, second[i].Status);
            Assert.Equal(first[i].Score, second[i].Score);
        }
    }

    // ---- Consentimiento explícito ----

    [Fact]
    public async Task Runner_rejects_suite_without_explicit_consent()
    {
        var runner = new ProbeRunner(new FakeProvider(new()));
        var requests = QuickProbeSuite.Probes().Select(Request).ToList();
        var consent = new QualificationConsent(false, 100m);

        await Assert.ThrowsAsync<QualificationConsentRequiredException>(() =>
            runner.RunSuiteAsync(requests, consent, CancellationToken.None));
    }

    // ---- Tope de costo ----

    [Fact]
    public async Task Runner_rejects_suite_when_estimated_cost_exceeds_cap()
    {
        var expensive = new Probe(ProbeId.WellKnown("expensive"), ProbeKind.Reading, "p", "x", 5.00m);
        var runner = new ProbeRunner(new FakeProvider(new Dictionary<string, string?>()));
        var requests = new List<ProbeRequest> { Request(expensive) };
        var consent = new QualificationConsent(true, 1.00m);

        await Assert.ThrowsAsync<QualificationCostCapExceededException>(() =>
            runner.RunSuiteAsync(requests, consent, CancellationToken.None));
    }

    [Fact]
    public async Task Runner_allows_suite_when_estimated_cost_is_within_cap()
    {
        var cheap = new Probe(ProbeId.WellKnown("cheap"), ProbeKind.Reading, "p", "x", 0.50m);
        var outputs = new Dictionary<string, string?> { ["p"] = "x" };
        var runner = new ProbeRunner(new FakeProvider(outputs));
        var requests = new List<ProbeRequest> { Request(cheap) };
        var consent = new QualificationConsent(true, 1.00m);

        var results = await runner.RunSuiteAsync(requests, consent, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(ProbeStatus.Passed, results[0].Status);
    }

    // ---- Timeout ----

    private sealed class SlowProvider : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await System.Threading.Tasks.Task.Delay(5000, cancellationToken);
            yield return new ResponseCompleted(new ModelResponse(
                new[] { new TextBlock("late") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("r", "m", null)));
        }
    }

    [Fact]
    public async Task Runner_times_out_slow_probe()
    {
        var runner = new ProbeRunner(new SlowProvider(), TimeSpan.FromMilliseconds(50));
        var probe = new Probe(ProbeId.WellKnown("slow"), ProbeKind.Reading, "p", "x", 0.00m);

        await Assert.ThrowsAsync<ProbeTimeoutException>(() =>
            runner.RunProbeAsync(Request(probe), CancellationToken.None));
    }

    // ---- TaskSetHash ----

    [Fact]
    public void TaskSetHash_is_deterministic_and_64_hex_chars()
    {
        var a = ProbeScorer.TaskSetHash(QuickProbeSuite.Probes());
        var b = ProbeScorer.TaskSetHash(QuickProbeSuite.Probes());

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    // ---- Suite identity ----

    [Fact]
    public void Quick_suite_has_ten_probes_and_stable_identity()
    {
        var probes = QuickProbeSuite.Probes();

        Assert.Equal(10, probes.Count);
        Assert.Equal("omnicore-quick", QuickProbeSuite.SuiteId);
        Assert.False(string.IsNullOrEmpty(QuickProbeSuite.SuiteVersion));
        Assert.All(probes, p => Assert.Equal(0m, p.MaxCostUsd));
    }
}
