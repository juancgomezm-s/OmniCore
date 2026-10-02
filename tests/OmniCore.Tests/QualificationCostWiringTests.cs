using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Regresiones del cableado de costo de la cualificación (M5): el defecto era que los requests no
/// llevaban <c>max_tokens</c> y que <c>Probe.MaxCostUsd</c>/<c>ProbeResult.CostUsd</c> quedaban en
/// cero incluso para providers pagos remotos. Estos tests fijan: estimación conservadora no nula
/// con precios declarados, fail-closed sin requests cuando el tope no alcanza o el precio es
/// desconocido, costo real retenido, uso incompleto que nunca se reporta como gratis, timeout sin
/// perfil falso y el tope explícito de salida llegando al body nativo de cada familia.
/// </summary>
public sealed class QualificationCostWiringTests
{
    private const decimal InputPrice = 0.10m;
    private const decimal OutputPrice = 0.30m;

    private static readonly QualificationPricing NvidiaPricing = new(InputPrice, OutputPrice);

    private static ModelSelection Selection() =>
        new(new ModelIdValue("paid-model"), 8192, ToolMode.Direct, null);

    private static ProbeRequest Request(Probe probe) => new(probe, Selection());

    private static IReadOnlyList<ProbeRequest> QuickRequests() =>
        QuickProbeSuite.Probes().Select(Request).ToArray();

    private static QualificationCostPolicy DeclaredPolicy(long maxOutput = 8192) =>
        QualificationCostPolicy.Declared(NvidiaPricing, maxOutput);

    // ---- Estimación conservadora: no nula para pricing pago declarado ----

    [Fact]
    public void Declared_pricing_estimates_all_ten_quick_probes_and_matches_their_sum()
    {
        var policy = DeclaredPolicy();
        var estimate = policy.EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes());

        Assert.NotNull(estimate);
        Assert.True(estimate > 0m, "la estimación de una suite paga declarada no puede ser cero");

        Assert.Equal(10, QuickProbeSuite.Probes().Count);
        // La estimación incluye cada probe de la nueva suite (input + maxOutput).
        var perProbe = QuickProbeSuite.Probes().Sum(probe => policy.EstimateProbeMaxCostUsd(probe)!.Value);
        Assert.Equal(perProbe, estimate!.Value);
    }

    [Fact]
    public void Declared_estimate_is_conservative_about_input_tokens()
    {
        var prompt = new string('a', 300);
        Assert.Equal((long)Math.Ceiling(300 / 3.0) + QualificationCostPolicy.InputFramingTokens,
            QualificationCostPolicy.EstimateInputTokens(prompt));
    }

    [Fact]
    public void Unknown_paid_pricing_has_no_estimate_and_local_zero_cost_is_exactly_zero()
    {
        Assert.Null(QualificationCostPolicy.UnknownPaid().EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes()));
        Assert.Equal(0m, QualificationCostPolicy.LocalZeroCost().EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes()));
    }

    // ---- Fail-closed: nada se ejecuta sin poder acotar el gasto ----

    [Fact]
    public async Task Unknown_paid_pricing_fails_closed_without_calling_the_provider()
    {
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, QualificationCostPolicy.UnknownPaid());

        await Assert.ThrowsAsync<QualificationUnknownPricingException>(() =>
            runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 100m), CancellationToken.None));

        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    public async Task Cap_below_the_estimate_runs_no_requests()
    {
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, DeclaredPolicy());
        var estimate = DeclaredPolicy().EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes())!.Value;
        var consent = new QualificationConsent(true, estimate / 2m);

        var exception = await Assert.ThrowsAsync<QualificationCostCapExceededException>(() =>
            runner.RunSuiteAsync(QuickRequests(), consent, CancellationToken.None));

        Assert.Equal(consent.MaxTotalCostUsd, exception.MaxTotalCostUsd);
        Assert.Equal(estimate, exception.EstimatedCostUsd);
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    public async Task Cap_that_only_covers_the_previous_three_probe_suite_rejects_the_ten_probe_suite()
    {
        var probes = QuickProbeSuite.Probes();
        var policy = DeclaredPolicy();
        var previousThreeProbeEstimate = policy.EstimateSuiteMaxCostUsd(probes.Take(3).ToArray())!.Value;
        var tenProbeEstimate = policy.EstimateSuiteMaxCostUsd(probes)!.Value;
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, policy);

        var exception = await Assert.ThrowsAsync<QualificationCostCapExceededException>(() =>
            runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, previousThreeProbeEstimate),
                CancellationToken.None));

        Assert.Equal(10, probes.Count);
        Assert.True(tenProbeEstimate > previousThreeProbeEstimate);
        Assert.Equal(tenProbeEstimate, exception.EstimatedCostUsd);
        Assert.Equal(previousThreeProbeEstimate, exception.MaxTotalCostUsd);
        Assert.Equal(0, provider.RequestCount);
    }

    // ---- Costo real medido con la evidencia del provider ----

    [Fact]
    public async Task Completed_probes_retain_the_real_cost_of_the_reported_usage()
    {
        // usage (10 input, 5 output) × (0.10, 0.30) por millón = 0.0000025 USD.
        var provider = new CountingProvider(new TokenUsage(10, 5, 0, 0, 0));
        var runner = new ProbeRunner(provider, DeclaredPolicy());

        var results = await runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m),
            CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.All(results, result => Assert.Equal(0.0000025m, result.CostUsd));
    }

    [Fact]
    public async Task Declared_pricing_without_usage_evidence_is_not_reported_as_free()
    {
        var provider = new CountingProvider(new TokenUsage(0, 0, 0, 0, 0));
        var runner = new ProbeRunner(provider, DeclaredPolicy());

        var results = await runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m),
            CancellationToken.None);

        var first = results[0];
        Assert.Equal(ProbeStatus.Error, first.Status);
        Assert.Null(first.CostUsd);
        Assert.Contains("uso incompleto", first.Error);
        Assert.All(results.Skip(1), result => Assert.Equal(ProbeStatus.NotRun, result.Status));
    }

    [Fact]
    public async Task Local_zero_cost_is_preserved_and_runs_even_with_a_zero_cap()
    {
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, QualificationCostPolicy.LocalZeroCost());

        var results = await runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 0m),
            CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.All(results, result => Assert.Equal(0m, result.CostUsd));
        Assert.Equal(10, provider.RequestCount);
    }

    // ---- Tope explícito de salida en el ModelRequest ----

    [Fact]
    public async Task The_request_carries_the_explicit_max_output_tokens_bound()
    {
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, DeclaredPolicy(maxOutput: 1234));
        var probe = new Probe(ProbeId.WellKnown("p"), ProbeKind.Reading, "prompt", "x", 0m);

        await runner.RunProbeAsync(Request(probe), CancellationToken.None);

        var sent = Assert.Single(provider.Requests);
        Assert.Equal(1234, sent.MaxOutputTokens);
    }

    [Fact]
    public async Task The_chat_completions_body_sends_max_tokens_when_the_request_bounds_it()
    {
        var handler = new CapturingHandler();
        var provider = new OpenAiChatCompatibleProvider(
            new ProviderDescriptor("test", ProviderFamily.OpenAiChatCompatible, "https://example.test/v1",
                AuthConfig.None(), false, false, true),
            new NoSecrets(), () => new HttpClient(handler, disposeHandler: false));
        var request = new ModelRequest(Selection(), [new ModelMessage(MessageRole.User, [new TextBlock("hi")])],
            null, [], ToolChoice.None(), null, null, null, null, maxOutputTokens: 777);

        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.NotNull(handler.Body);
        Assert.Contains("\"max_tokens\":777", handler.Body);
    }

    [Fact]
    public async Task The_chat_completions_body_omits_max_tokens_when_the_request_does_not_bound_it()
    {
        var handler = new CapturingHandler();
        var provider = new OpenAiChatCompatibleProvider(
            new ProviderDescriptor("test", ProviderFamily.OpenAiChatCompatible, "https://example.test/v1",
                AuthConfig.None(), false, false, true),
            new NoSecrets(), () => new HttpClient(handler, disposeHandler: false));
        var request = new ModelRequest(Selection(), [new ModelMessage(MessageRole.User, [new TextBlock("hi")])],
            null, [], ToolChoice.None(), null, null, null, null);

        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("max_tokens", handler.Body);
    }

    [Fact]
    public void The_anthropic_body_prefers_the_explicit_bound_over_its_default()
    {
        var request = new ModelRequest(Selection(), [new ModelMessage(MessageRole.User, [new TextBlock("hi")])],
            null, [], ToolChoice.None(), null, null, null, null, maxOutputTokens: 1234);

        var plain = AnthropicMessagesProvider.BuildBody(request, new AnthropicProviderOptions());
        using var doc = JsonDocument.Parse(plain);
        Assert.Equal(1234, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void The_responses_body_prefers_the_explicit_bound_over_the_adapter_option()
    {
        var request = new ModelRequest(Selection(), [new ModelMessage(MessageRole.User, [new TextBlock("hi")])],
            null, [], ToolChoice.None(), null, null, null, null, maxOutputTokens: 1234);

        var body = OpenAIResponsesProvider.BuildBody(request, new OpenAIResponsesOptions { MaxOutputTokens = 777 });
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1234, doc.RootElement.GetProperty("max_output_tokens").GetInt32());
    }

    // ---- Timeout y cancelación: nunca un perfil exitoso falso ----

    [Fact]
    public async Task Timeout_produces_a_timeout_result_with_partial_usage_and_no_success()
    {
        var provider = new PartialUsageSlowProvider(new TokenUsage(7, 3, 0, 0, 0));
        var runner = new ProbeRunner(provider, TimeSpan.FromMilliseconds(50), DeclaredPolicy());

        var results = await runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m),
            CancellationToken.None);

        var first = results[0];
        Assert.Equal(ProbeStatus.Timeout, first.Status);
        Assert.Null(first.Output);
        // Uso parcial retenido: (7 input, 3 output) × (0.10, 0.30) = 0.0000016 USD.
        Assert.Equal(0.0000016m, first.CostUsd);
        Assert.All(results.Skip(1), result => Assert.Equal(ProbeStatus.NotRun, result.Status));
    }

    [Fact]
    public async Task RunProbeAsync_still_signals_a_timeout_with_the_typed_exception()
    {
        var provider = new PartialUsageSlowProvider(new TokenUsage(7, 3, 0, 0, 0));
        var runner = new ProbeRunner(provider, TimeSpan.FromMilliseconds(50), DeclaredPolicy());
        var probe = new Probe(ProbeId.WellKnown("slow"), ProbeKind.Reading, "p", "x", 0m);

        await Assert.ThrowsAsync<ProbeTimeoutException>(() =>
            runner.RunProbeAsync(Request(probe), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_before_the_run_marks_not_run_without_requests()
    {
        var provider = new CountingProvider();
        var runner = new ProbeRunner(provider, DeclaredPolicy());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<QualificationCancelledException>(() =>
            runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m), cts.Token));

        Assert.Equal(10, exception.Results.Count);
        Assert.All(exception.Results, result => Assert.Equal(ProbeStatus.NotRun, result.Status));
        Assert.True(exception.IsFinalCostConfirmed);
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    public async Task Cancellation_during_probe_preserves_partial_usage_once_and_marks_rest_not_run()
    {
        var usage = new TokenUsage(7, 3, 0, 0, 0);
        var provider = new PartialUsageBlockingProvider(usage);
        var runner = new ProbeRunner(provider, DeclaredPolicy());
        using var cts = new CancellationTokenSource();

        var run = runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m), cts.Token);
        await provider.UsageEmitted.Task;
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<QualificationCancelledException>(() => run);

        Assert.Equal(usage, exception.ObservedUsage);
        Assert.Equal(0.0000016m, exception.KnownCostUsd);
        Assert.False(exception.HasUnknownCost);
        Assert.False(exception.IsFinalCostConfirmed);
        Assert.Equal(new[] { ProbeStatus.Cancelled }.Concat(Enumerable.Repeat(ProbeStatus.NotRun, 9)),
            exception.Results.Select(result => result.Status));
        Assert.Equal(0.0000016m, exception.Results[0].CostUsd);
        Assert.All(exception.Results.Skip(1), result => Assert.Null(result.CostUsd));
        Assert.Equal(1, provider.RequestCount);
        Assert.Equal(cts.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_without_paid_usage_keeps_cost_unknown_not_zero()
    {
        var provider = new PartialUsageBlockingProvider(new TokenUsage(0, 0, 0, 0, 0));
        var runner = new ProbeRunner(provider, DeclaredPolicy());
        using var cts = new CancellationTokenSource();

        var run = runner.RunSuiteAsync(QuickRequests(), new QualificationConsent(true, 1.00m), cts.Token);
        await provider.UsageEmitted.Task;
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<QualificationCancelledException>(() => run);

        Assert.Equal(new TokenUsage(0, 0, 0, 0, 0), exception.ObservedUsage);
        Assert.True(exception.HasUnknownCost);
        Assert.Null(exception.Results[0].CostUsd);
        Assert.Equal(0m, exception.KnownCostUsd);
        Assert.Equal(1, provider.RequestCount);
    }

    // ---- Cableado de la fachada Host: preview y fail-closed end-to-end ----

    [Fact]
    public async Task Host_preview_is_nonzero_for_declared_paid_pricing_and_qualify_retains_real_cost()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: true);
        using var host = ModelQualificationHost.Create(dir);

        var preview = host.PreviewCost("paid-model", "quick");
        Assert.False(preview.IsUnknownPaidPricing);
        Assert.NotNull(preview.EstimatedUsd);
        Assert.True(preview.EstimatedUsd > 0m);
        Assert.Equal(8192, preview.MaxOutputTokens);
        var expectedPreview = DeclaredPolicy(preview.MaxOutputTokens).EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes());
        Assert.Equal(expectedPreview, preview.EstimatedUsd);

        var provider = new CountingProvider(new TokenUsage(10, 5, 0, 0, 0));
        var result = await host.QualifyAsync("paid-model", new QualificationOptions
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1.00m,
            Provider = provider,
        }, CancellationToken.None);

        Assert.True(result.EstimatedCostUsd > 0m);
        Assert.Equal(10, provider.RequestCount);
        Assert.All(result.Probes, probe => Assert.Equal(0.0000025m, probe.CostUsd));

        // La recomendación es visible pero la política operativa no se crea ni se toca.
        Assert.False(string.IsNullOrEmpty(result.RecommendedCategory));
        var policyService = OmniHost.CreateModelPolicyService(dir);
        Assert.Null(policyService.Get(ModelPolicyKey.For("nvidia", "paid-model"), CancellationToken.None));
    }

    [Fact]
    public async Task Host_reports_cancellation_accounting_without_persisting_a_partial_profile()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: true);
        var artifacts = new CountingArtifactStore(new FileArtifactStore(dir));
        using var host = ModelQualificationHost.Create(dir, artifactStore: artifacts);
        var usage = new TokenUsage(7, 3, 0, 0, 0);
        var provider = new PartialUsageBlockingProvider(usage);
        using var cts = new CancellationTokenSource();

        var run = host.QualifyAsync("paid-model", new QualificationOptions
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1.00m,
            Provider = provider,
        }, cts.Token);
        await provider.UsageEmitted.Task;
        cts.Cancel();

        var exception = await Assert.ThrowsAsync<ModelQualificationCancelledException>(() => run);

        Assert.Equal(usage, exception.ObservedUsage);
        Assert.Equal(0.0000016m, exception.KnownCostUsd);
        Assert.False(exception.HasUnknownCost);
        Assert.False(exception.IsFinalCostConfirmed);
        Assert.Equal(new[] { "Cancelled" }.Concat(Enumerable.Repeat("NotRun", 9)),
            exception.Probes.Select(probe => probe.Status));
        Assert.Equal(0.0000016m, exception.Probes[0].CostUsd);
        Assert.Equal(1, provider.RequestCount);
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Equal(0, artifacts.PutCount);

        // Una nueva suite completa empieza desde Declared: la cancelada no creó perfil ni traits.
        var completed = await host.QualifyAsync("paid-model", new QualificationOptions
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1.00m,
            Provider = new CountingProvider(),
        }, CancellationToken.None);
        Assert.Equal("Declared", completed.PreviousState);
        Assert.Equal(1, completed.ProfileRevision);
        Assert.Equal(1, artifacts.PutCount);
    }

    [Fact]
    public async Task Completed_suite_persists_redacted_cas_bundle_and_links_it_to_profile_revision()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: true);
        using var host = ModelQualificationHost.Create(dir);
        const string canary = "sk-test-qualification-secret123";

        var result = await host.QualifyAsync("paid-model", new QualificationOptions
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1.00m,
            Provider = new CountingProvider(responseText: canary),
        }, CancellationToken.None);

        using var profileStore = new SqliteModelQualificationStore(Path.Combine(dir, "user.db"));
        var profile = Assert.Single(profileStore.List(CancellationToken.None));
        var reference = Assert.IsType<ArtifactRef>(profile.ResultsArtifact);
        Assert.Equal(result.ProfileRevision, profile.ProfileRevision);
        Assert.True(reference.Redacted);
        Assert.Equal(ArtifactKind.Other, reference.Kind);
        Assert.Equal(Sensitivity.Sensitive, reference.Sensitivity);

        var artifactStore = new FileArtifactStore(dir);
        Assert.True(artifactStore.Verify(reference.Hash, reference.Size));
        var content = artifactStore.GetText(reference.Hash)!;
        Assert.DoesNotContain(canary, content);
        Assert.Contains("[REDACTED]", content);
        using var evidence = JsonDocument.Parse(content);
        var benchmark = evidence.RootElement.GetProperty("BenchmarkIdentity");
        Assert.Equal("omnicore-quick", benchmark.GetProperty("SuiteId").GetString());
        Assert.Equal("2.0.0", benchmark.GetProperty("SuiteVersion").GetString());
        Assert.Equal(ProbeScorer.TaskSetHash(QuickProbeSuite.Probes()),
            benchmark.GetProperty("TaskSetHash").GetString());
        Assert.Equal(10, evidence.RootElement.GetProperty("Probes").GetArrayLength());
        Assert.Contains(evidence.RootElement.GetProperty("Probes").EnumerateArray(), probe =>
            probe.GetProperty("Output").GetString() == "[REDACTED]");
    }

    [Fact]
    public async Task Incomplete_suite_does_not_write_a_complete_evidence_artifact()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: true);
        var artifacts = new CountingArtifactStore(new FileArtifactStore(dir));
        using var host = ModelQualificationHost.Create(dir, artifactStore: artifacts);

        await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() =>
            host.QualifyAsync("paid-model", new QualificationOptions
            {
                Suite = "quick",
                ConsentGiven = true,
                MaxTotalCostUsd = 1.00m,
                PerProbeTimeout = TimeSpan.FromMilliseconds(50),
                Provider = new PartialUsageSlowProvider(new TokenUsage(7, 3, 0, 0, 0)),
            }, CancellationToken.None));

        Assert.Equal(0, artifacts.PutCount);
        using var profileStore = new SqliteModelQualificationStore(Path.Combine(dir, "user.db"));
        Assert.Empty(profileStore.List(CancellationToken.None));
    }

    [Fact]
    public async Task Host_fails_closed_for_unknown_paid_pricing_without_calling_the_provider()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: false);
        using var host = ModelQualificationHost.Create(dir);

        var preview = host.PreviewCost("paid-model", "quick");
        Assert.True(preview.IsUnknownPaidPricing);
        Assert.Null(preview.EstimatedUsd);

        var provider = new CountingProvider();
        await Assert.ThrowsAsync<ModelQualificationUnknownPricingException>(() =>
            host.QualifyAsync("paid-model", new QualificationOptions
            {
                Suite = "quick",
                ConsentGiven = true,
                MaxTotalCostUsd = 1.00m,
                Provider = provider,
            }, CancellationToken.None));

        Assert.Equal(0, provider.RequestCount);
    }

    [Fact]
    public void Host_does_not_treat_a_public_provider_without_authRef_as_free()
    {
        var dir = TempDir();
        var config = Path.Combine(dir, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"),
            "providers:\n  public-endpoint:\n    family: OpenAiChatCompatible\n"
            + "    baseUrl: \"https://example.test/v1\"\n");
        File.WriteAllText(Path.Combine(config, "models.yaml"),
            "models:\n  remote-model:\n    provider: \"public-endpoint\"\n"
            + "    context: 8192\n    recommendedUsableContext: 8192\n    maxOutput: 1024\n");
        using var host = ModelQualificationHost.Create(dir);

        var preview = host.PreviewCost("remote-model", "quick");

        Assert.True(preview.IsUnknownPaidPricing);
        Assert.Null(preview.EstimatedUsd);
    }

    [Fact]
    public void Host_preview_keeps_local_models_at_zero_cost()
    {
        var dir = TempDir();
        using var host = ModelQualificationHost.Create(dir, new[]
        {
            new ModelRegistryModelDescriptor("qwen-test", "local", 8192, 8192, 2048),
        });

        var preview = host.PreviewCost("qwen-test", "quick");

        Assert.False(preview.IsUnknownPaidPricing);
        Assert.Equal(0m, preview.EstimatedUsd);
        Assert.Equal(2048, preview.MaxOutputTokens);
    }

    // ---- Cableado de la CLI: el aviso de consentimiento muestra un estimado no nulo ----

    [Fact]
    public async Task Cli_consent_prompt_shows_a_nonzero_estimate_for_declared_paid_pricing()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: true);
        var output = new StringWriter();

        var code = await OmniCore.Cli.ModelPolicyCommands.Run(
            ["model", "qualify", "paid-model"], new StringReader("n\n"), output, interactive: true,
            dataDirectoryOverride: dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains("coste estimado", text);
        Assert.DoesNotContain("~0.00 USD", text);
        var expected = QualificationCostPolicy.Declared(new QualificationPricing(0.10m, 0.30m), 8192)
            .EstimateSuiteMaxCostUsd(QuickProbeSuite.Probes())!.Value;
        Assert.Contains(expected.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.Contains("Cancelado", text);
    }

    [Fact]
    public async Task Cli_fails_closed_for_unknown_paid_pricing_without_any_call()
    {
        var dir = TempDir();
        WritePaidConfiguration(dir, declaredPricing: false);
        var output = new StringWriter();

        var code = await OmniCore.Cli.ModelPolicyCommands.Run(
            ["model", "qualify", "paid-model", "--yes"], new StringReader(""), output, interactive: false,
            dataDirectoryOverride: dir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, code);
        Assert.Contains("pricing desconocido", output.ToString());
    }

    // ---- Helpers ----

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-qual-cost-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePaidConfiguration(string dataDir, bool declaredPricing)
    {
        var config = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(config);
        var prices = declaredPricing
            ? "    inputPricePerMillionUsd: 0.10\n    outputPricePerMillionUsd: 0.30\n"
            : "";
        File.WriteAllText(Path.Combine(config, "providers.yaml"),
            "providers:\n  nvidia:\n    family: OpenAiChatCompatible\n"
            + "    baseUrl: \"https://integrate.api.nvidia.com/v1\"\n    authRef: \"nvidia-key\"\n" + prices);
        File.WriteAllText(Path.Combine(config, "models.yaml"),
            "models:\n  paid-model:\n    provider: \"nvidia\"\n    context: 65536\n"
            + "    recommendedUsableContext: 65536\n    maxOutput: 8192\n");
    }

    private sealed class CountingProvider : IModelProvider
    {
        private readonly TokenUsage _usage;
        private readonly string _responseText;

        private readonly List<ModelRequest> _requests = [];

        public CountingProvider(TokenUsage? usage = null, string responseText = "wrong-answer")
        {
            _usage = usage ?? new TokenUsage(10, 5, 0, 0, 0);
            _responseText = responseText;
        }

        public int RequestCount => _requests.Count;

        public IReadOnlyList<ModelRequest> Requests => _requests;

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(_responseText)], StopReason.EndTurn, _usage, null,
                new ProviderMetadata("req", "paid-model", null)));
        }
    }

    private sealed class CountingArtifactStore : IArtifactStore
    {
        private readonly IArtifactStore _inner;

        public CountingArtifactStore(IArtifactStore inner) => _inner = inner;

        public int PutCount { get; private set; }

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            PutCount++;
            return _inner.PutText(content, mediaType, kind, sensitivity);
        }

        public string? GetText(ContentHash hash) => _inner.GetText(hash);

        public bool Verify(ContentHash hash, long expectedSize) => _inner.Verify(hash, expectedSize);
    }

    private sealed class PartialUsageSlowProvider : IModelProvider
    {
        private readonly TokenUsage _usage;

        public PartialUsageSlowProvider(TokenUsage usage) => _usage = usage;

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new UsageUpdated(_usage);
            await Task.Delay(5000, cancellationToken);
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock("late")], StopReason.EndTurn, _usage, null,
                new ProviderMetadata("req", "paid-model", null)));
        }
    }

    private sealed class PartialUsageBlockingProvider : IModelProvider
    {
        private readonly TokenUsage _usage;
        private int _requestCount;

        public PartialUsageBlockingProvider(TokenUsage usage) => _usage = usage;

        public TaskCompletionSource<bool> UsageEmitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => _requestCount;

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            yield return new UsageUpdated(_usage);
            UsageEmitted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no secret expected");
    }

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream)
        {
            events.Add(item);
        }

        return events;
    }
}
