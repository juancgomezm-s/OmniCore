using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Tests de integración de la fachada de cualificación (M5, ADR-0007 §6–§7): `omni model qualify`
/// contra un provider scripteado (nunca un servicio real), consentimiento explícito, tope de coste
/// tipado, persistencia en el store de scope User, recomendación SIN ampliar la política
/// operativa (ADR-0044 §9) y pickup de los traits empíricos por EffectiveModelProfile (ADR-0007 §1).
/// </summary>
public sealed class ModelQualificationHostTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-qual-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<ModelRegistryModelDescriptor> Registry() => new[]
    {
        new ModelRegistryModelDescriptor("qwen-test", "local", 8192, 8192, 2048),
    };

    private static ModelDefinition Model() => new("qwen-test", "local", 8192, 8192, 2048);

    // ---- Provider scripteado local (mismo patrón que QualificationQuickSuiteTests) ----

    // Scripted fixture has one generation response per StreamAsync invocation; this is not a billing guarantee.
    private sealed class ScriptedProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly Dictionary<string, string?> _outputsByPrompt;
        public long? MaximumGenerationRequestAttempts => 1;

        public ScriptedProvider(Dictionary<string, string?> outputsByPrompt) => _outputsByPrompt = outputsByPrompt;

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
                Metadata: new ProviderMetadata("req-1", "qwen-test", null)));
        }
    }

    /// <summary>Provider scripteado que responde la suite completa con sus fixtures exactos.</summary>
    private static ScriptedProvider PassingProvider() => new(QuickProbeSuite.Probes()
        .ToDictionary(probe => probe.Prompt, probe => (string?)probe.Expected));

    private static ScriptedProvider FailingReadingProvider()
    {
        var outputs = QuickProbeSuite.Probes()
            .ToDictionary(probe => probe.Prompt, probe => (string?)probe.Expected);
        outputs[QuickProbeSuite.Probes()[0].Prompt] = "dog";
        outputs[QuickProbeSuite.Probes()[2].Prompt] = "{\"status\":\"ok\",\"count\":2}";
        return new ScriptedProvider(outputs);
    }

    private static QualificationOptions Options(ScriptedProvider provider, decimal cap = 1.00m) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = cap,
        Provider = provider,
    };

    // ---- Cualificar persiste el perfil, los traits y la transición de estado ----

    [Fact]
    public async Task Qualify_with_scripted_provider_stores_profile_traits_and_Qualified_state()
    {
        var dir = TempDir();
        using var host = ModelQualificationHost.Create(dir, Registry());

        var result = await host.QualifyAsync("qwen-test", Options(PassingProvider()), CancellationToken.None);

        // Transición de estado (ADR-0007 §4): Declared → Qualified al pasar toda la suite.
        Assert.Equal(ModelQualificationState.Declared.ToString(), result.PreviousState);
        Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
        Assert.Equal(1, result.ProfileRevision);
        Assert.All(result.Probes, probe => Assert.Equal("Passed", probe.Status));

        // Persistido en el store de scope User bajo la clave exacta de la configuración.
        var model = Model();
        var key = ModelQualificationHost.QualificationKeyFor(model, provider: null);
        using var store = OmniHost.CreateModelQualificationStore(dir);
        var profile = store.Get(key, CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(ModelQualificationState.Qualified, profile!.State);
        Assert.Equal(1, profile.ProfileRevision);

        var traits = store.Traits(key, profile.ProfileRevision, CancellationToken.None);
        Assert.Contains(traits, t => t.Trait == "InstructionFollowing" && t.Value == 1.0 && t.Source == "empirical");
        Assert.Contains(traits, t => t.Trait == "StructuredOutputReliability" && t.Value == 1.0 && t.Source == "empirical");
    }

    // ---- Consentimiento explícito ----

    [Fact]
    public async Task Qualify_without_consent_is_refused_and_nothing_runs_or_persists()
    {
        var dir = TempDir();
        using var host = ModelQualificationHost.Create(dir, Registry());

        // El Host revalida el consentimiento aunque el cliente ya lo haya validado.
        await Assert.ThrowsAsync<ModelQualificationConsentException>(() => host.QualifyAsync("qwen-test",
            new QualificationOptions { Suite = "quick", ConsentGiven = false, Provider = PassingProvider() },
            CancellationToken.None));

        // Nada se persistió.
        using var store = OmniHost.CreateModelQualificationStore(dir);
        Assert.Empty(store.List(CancellationToken.None));
    }

    // ---- Tope de coste: error tipado ----

    [Fact]
    public async Task Qualify_exceeding_cost_cap_throws_typed_error_and_nothing_persists()
    {
        var dir = TempDir();
        using var host = ModelQualificationHost.Create(dir, Registry());
        var expensive = new Probe(ProbeId.WellKnown("expensive-reading"), ProbeKind.Reading, "prompt", "x", 5.00m);
        var options = new QualificationOptions
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1.00m,
            Provider = PassingProvider(),
            Probes = [expensive],
        };

        var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(
            () => host.QualifyAsync("qwen-test", options, CancellationToken.None));

        Assert.Equal(5.00m, exception.EstimatedUsd);
        Assert.Equal(1.00m, exception.CapUsd);

        // Nada se ejecutó ni se persistió.
        using var store = OmniHost.CreateModelQualificationStore(dir);
        Assert.Empty(store.List(CancellationToken.None));
    }

    // ---- Recomendación SIN ampliar la política operativa (ADR-0044 §9, criterio M5) ----

    [Fact]
    public async Task Qualify_recommends_a_category_but_never_changes_the_operational_policy()
    {
        var dir = TempDir();
        // Política operativa vigente del usuario: ObserveOnly rev=1 (la más restrictiva).
        var policyService = OmniHost.CreateModelPolicyService(dir);
        var policyKey = ModelPolicyKey.For("local", "qwen-test");
        policyService.Set(policyKey, 0, ModelPolicyPresets.For(ModelPolicyCategory.ObserveOnly),
            CancellationToken.None);

        using var host = ModelQualificationHost.Create(dir, Registry());
        var result = await host.QualifyAsync("qwen-test", Options(PassingProvider()), CancellationToken.None);

        // La evidencia recomienda PatchOnly…
        Assert.Equal("PatchOnly", result.RecommendedCategory);
        Assert.Contains(result.RecommendationNotes,
            note => note.Contains("FullAgent", StringComparison.Ordinal));

        // …pero la política operativa guardada sigue siendo la del usuario, sin tocar.
        var stored = policyService.Get(policyKey, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(ModelPolicyCategory.ObserveOnly, stored!.Policy.Category);
        Assert.Equal(1, stored.Revision);
    }

    // ---- Cualificación parcial: ProvisionallyClassified no es evidencia utilizable ----

    [Fact]
    public async Task Qualify_with_failed_probes_stays_provisional_and_is_not_used_by_the_profile()
    {
        var dir = TempDir();
        using var host = ModelQualificationHost.Create(dir, Registry());

        var result = await host.QualifyAsync("qwen-test", Options(FailingReadingProvider()), CancellationToken.None);

        // Suite completa con fallos exactos: ProvisionallyClassified con los traits medidos.
        Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
        var instruction = Assert.Single(result.Traits, t => t.Trait == "InstructionFollowing");
        Assert.Equal(6.0 / 7.0, instruction.Value);

        // La capa Empirical SOLO usa perfiles Qualified/Calibrated/Stale (ADR-0007 §1, §4).
        var model = Model();
        using var store = OmniHost.CreateModelQualificationStore(dir);
        Assert.Null(ModelQualificationHost.UsableTraits(store, model, provider: null, CancellationToken.None));
    }

    // ---- Pickup por EffectiveModelProfile (ADR-0007 §1: Heuristic → Empirical) ----

    [Fact]
    public async Task EffectiveModelProfile_picks_up_empirical_traits_from_a_qualified_profile()
    {
        var dir = TempDir();
        var model = Model();
        var provider = new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1:8080/v1", AuthConfig.None(), false, false, false);

        // Sin cualificación: la heurística provisiona 0.5 (sin tamaño declarado).
        var heuristic = new ModelProfileResolver().Resolve(model, provider);
        Assert.Equal(0.5, heuristic.Traits["InstructionFollowing"]);

        // Cualificar con la MISMA configuración exacta.
        using (var host = ModelQualificationHost.Create(dir, Registry()))
        {
            await host.QualifyAsync("qwen-test", Options(PassingProvider()), CancellationToken.None);
        }

        // La capa Empirical reemplaza a la heurística SOLO por clave exacta, nunca por nombre.
        using var store = OmniHost.CreateModelQualificationStore(dir);
        var empirical = ModelQualificationHost.UsableTraits(store, model, provider, CancellationToken.None);
        Assert.NotNull(empirical);
        var resolved = new ModelProfileResolver().Resolve(model, provider, overrides: null,
            empiricalTraits: empirical);
        Assert.Equal(1.0, resolved.Traits["InstructionFollowing"]);
        Assert.Equal(1.0, resolved.Traits["StructuredOutputReliability"]);

        // El resto de traits no medidos conserva la heurística.
        Assert.Equal(0.5, resolved.Traits["ToolCallReliability"]);

        // Otra configuración (otro provider con tool calls nativos: otra clave exacta) no hereda
        // la cualificación.
        var otherProvider = new ProviderDescriptor("remote", ProviderFamily.OpenAiChatCompatible,
            "https://api.example.com/v1", AuthConfig.None(), false, false, true);
        Assert.Null(ModelQualificationHost.UsableTraits(store, model, otherProvider, CancellationToken.None));
    }
}
