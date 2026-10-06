namespace OmniCore.Host;

using System.Collections.ObjectModel;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;

/// <summary>
/// Opciones de una ejecución de la suite de cualificación. El consentimiento llega ya validado
/// por el cliente (<c>--yes</c> o confirmación interactiva); el Host lo revalida por defensa en
/// profundidad. <see cref="Probes"/> y <see cref="Provider"/> son puntos de inyección de
/// tests: producción usa siempre la suite <c>quick</c> y el provider configurado.
/// </summary>
public sealed class QualificationOptions
{
    public string Suite { get; init; } = "quick";

    /// <summary>Consentimiento explícito del usuario (ADR-0007 §7: la suite nunca corre sola).</summary>
    public bool ConsentGiven { get; init; }

    /// <summary>Tope de costo total en USD que el usuario aceptó pagar.</summary>
    public decimal MaxTotalCostUsd { get; init; } = 1.00m;

    public TimeSpan? PerProbeTimeout { get; init; }

    /// <summary>Probes a ejecutar (tests); null → la suite pedida.</summary>
    public IReadOnlyList<Probe>? Probes { get; init; }

    /// <summary>Provider ya construido (tests con provider scripteado); null → conexión normal.</summary>
    public IModelProvider? Provider { get; init; }
}

/// <summary>Resultado de un probe individual, con campos primitivos para el cliente.</summary>
public sealed record QualificationProbeOutcome(
    string Id,
    string Kind,
    string Status,
    double Score,
    string? Error,
    TimeSpan Duration,
    decimal? CostUsd)
{
    public QualificationProbeUsage? Usage { get; init; }
}

/// <summary>Reported counts only. Cache is included in input and reasoning in output;
/// do not add them again. Absent fields remain null.</summary>
public sealed record QualificationProbeUsage(long? Input, long? Output,
    long? CacheRead, long? CacheWrite, long? Reasoning);

/// <summary>Pre-call configuration estimate, not a measured charge. Null means unavailable.</summary>
public sealed record QualificationCostEstimate(decimal? Usd, string Source);

/// <summary>Trait empírico persistido para la revisión vigente del perfil.</summary>
public sealed record QualificationTraitValue(
    string Trait,
    double Value,
    double Confidence,
    int Samples,
    string Source);

/// <summary>
/// Resultado de cualificar: estados anterior y nuevo (ADR-0007 §4), probes, traits persistidos
/// y la recomendación de política operativa (ADR-0044 §6), que NUNCA se aplica sola. Los estados
/// viajan como string para que el cliente no referencie OmniCore.Domain (frontera IL de
/// ADR-0009 §2.4): son los identificadores estables de <see cref="ModelQualificationState"/>.
/// </summary>
public sealed record QualificationRunResult(
    string ModelId,
    string ProviderId,
    string KeyHash,
    string PreviousState,
    string NewState,
    long ProfileRevision,
    IReadOnlyList<QualificationProbeOutcome> Probes,
    IReadOnlyList<QualificationTraitValue> Traits,
    string RecommendedCategory,
    string RecommendedMutationMode,
    string RecommendedDeletePolicy,
    string RecommendedMoveOrRenamePolicy,
    int RecommendedMaxFilesPerTurn,
    int RecommendedMaxChangedLinesPerTurn,
    double RecommendedMaxRewriteRatio,
    IReadOnlyList<string> RecommendationNotes,
    decimal EstimatedCostUsd,
    decimal CostCapUsd)
{
    /// <summary>Provenance of the pre-call estimate, not a provider billing measurement.</summary>
    public string EstimatedCostSource { get; init; } = "declared-probe-maxima";
    /// <summary>User CAS audit artifact; content may be secret-redacted before hashing.</summary>
    public string? EvidenceHash { get; init; }
    public bool EvidenceRedacted { get; init; }
    /// <summary>True only when the executed task set exactly matches the supported suite.</summary>
    public bool SuiteComplete { get; init; }
}

/// <summary>
/// Error tipado de nivel Host (spec §71): la suite requiere consentimiento explícito. Envuelve
/// <see cref="OmniCore.Qualification.QualificationConsentRequiredException"/> para que el CLI no
/// referencie el módulo de cualificación (frontera binaria de ADR-0009 §2.4).
/// </summary>
public sealed class ModelQualificationConsentException : Exception
{
    public ModelQualificationConsentException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// Error tipado de nivel Host: el tope de coste aceptado no cubre el coste estimado de la suite.
/// Nada se ejecutó ni se persistió.
/// </summary>
public sealed class ModelQualificationCostCapException : Exception
{
    public decimal CapUsd { get; }

    public decimal EstimatedUsd { get; }

    public ModelQualificationCostCapException(decimal capUsd, decimal estimatedUsd)
        : base("tope de coste superado: estimado " + estimatedUsd + " USD > tope " + capUsd + " USD")
    {
        CapUsd = capUsd;
        EstimatedUsd = estimatedUsd;
    }
}

/// <summary>A cost estimate is unavailable or unrepresentable, including missing potentially paid pricing/output information.
/// No provider has been constructed or called.</summary>
public sealed class ModelQualificationCostEvidenceUnavailableException : Exception
{
    public ModelQualificationCostEvidenceUnavailableException()
        : base("la estimación de coste de cualificación no está disponible o no es representable") { }
}

/// <summary>
/// Error tipado de nivel Host: la suite no se completó (algún probe terminó en Error/Timeout).
/// Nada se persistió: una cualificación parcial no existe (ADR-0007 §4).
/// </summary>
public sealed class ModelQualificationSuiteIncompleteException : Exception
{
    public IReadOnlyList<string> Failures { get; }

    public ModelQualificationSuiteIncompleteException(IReadOnlyList<string> failures)
        : this(failures.Select(OmniCliRuntime.RedactSensitive).ToArray(), sanitized: true)
    {
    }

    private ModelQualificationSuiteIncompleteException(string[] failures, bool sanitized)
        : base("la suite de cualificación no se completó: " + string.Join("; ", failures))
    {
        Failures = Array.AsReadOnly(failures); // Snapshot once: caller mutation cannot alter the reported evidence.
    }
}

/// <summary>Error tipado de nivel Host: suite pedida no existe (solo `quick` en M5).</summary>
public sealed class ModelQualificationUnsupportedSuiteException : Exception
{
    public string Suite { get; }

    public ModelQualificationUnsupportedSuiteException(string suite)
        : base("suite de cualificación no soportada: " + suite)
    {
        Suite = suite;
    }
}

/// <summary>
/// Fachada de la suite de cualificación para clientes (M5, ADR-0007 §6–§7). Ejecuta la suite
/// <c>quick</c> contra el MISMO provider configurado que usa el runtime (OmniHost.ConnectProvider,
/// sin camino especial), exige consentimiento explícito, respeta el tope de coste y persiste el
/// perfil y sus traits en el almacén de scope User. La recomendación de categoría se calcula y se
/// devuelve, pero jamás se aplica: aplicarla exige el flujo explícito `omni model policy set`
/// (ADR-0044 §9) — la evidencia puede recomendar, nunca ampliar la política operativa.
/// Los errores tipados del módulo se envuelven en excepciones de Host (patrón de
/// <see cref="ModelPolicyRevisionConflict"/>) para mantener la frontera binaria del CLI.
/// </summary>
public sealed partial class ModelQualificationHost : IDisposable
{
    private readonly SqliteModelQualificationStore _store;

    private readonly ModelRegistry _registry;

    private readonly IPlatformPaths _paths;
    private readonly LoadedUserConfiguration? _configuration;

    private ModelQualificationHost(SqliteModelQualificationStore store, ModelRegistry registry,
        IPlatformPaths paths, LoadedUserConfiguration? configuration)
    {
        _store = store;
        _registry = registry;
        _paths = paths;
        _configuration = configuration;
    }

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Fachada respaldada por el user.db del usuario y el registro de modelos configurado.</summary>
    /// <summary>
    /// Crea la fachada sobre el store de cualificación y el registro de modelos del usuario. El
    /// provider no se construye aquí: lo decide <see cref="QualificationOptions"/> en cada
    /// ejecución (la firma se mantiene en tipos del Host para no arrastrar referencias IL al
    /// binario omni — frontera de ADR-0009 §2.4 verificada por CliAssemblyReferenceTests).
    /// </summary>
    public static ModelQualificationHost Create(string? dataDirectoryOverride = null,
        IReadOnlyList<ModelRegistryModelDescriptor>? registryOverride = null)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectoryOverride);
        var configuration = registryOverride is null ? OmniHost.LoadUserConfiguration(paths) : null;
        var registry = registryOverride is null
            ? configuration!.Registry
            : ToRegistry(registryOverride);
        var store = OmniHost.CreateModelQualificationStore(dataDirectoryOverride);
        return new ModelQualificationHost(store, registry, paths, configuration);
    }

    /// <summary>Perfil de cualificación vigente para la configuración exacta del modelo, o null.</summary>
    public ModelQualificationProfile? Get(ModelDefinition model, ProviderDescriptor? provider,
        CancellationToken cancellationToken) =>
        _store.Get(QualificationKeyFor(model, provider), cancellationToken);

    /// <summary>Preview for consent; constructs no provider and performs no authenticated query.</summary>
    public QualificationCostEstimate PreviewSuiteCost(string modelId, string suite)
    {
        var model = _registry.Model(modelId)
            ?? throw new ArgumentException("modelo desconocido en el registro", nameof(modelId));
        var provider = _registry.Provider(model.ProviderId);
        var probes = Suite(suite);
        var configured = ConfiguredEstimate(model, Selection(model, provider),
            _configuration?.Pricing(model.Id), probes.Count, GenerationAttempts(provider, null));
        if (configured is { } value)
            return new QualificationCostEstimate(Math.Max(value, DeclaredEstimate(probes)),
                "max-declared-and-configured-descriptor-token-estimate");
        return provider?.BillingMode == BillingMode.Local
            ? new QualificationCostEstimate(DeclaredEstimate(probes), "declared-local-probe-maxima")
            : new QualificationCostEstimate(null, "unavailable");
    }

    /// <summary>Ejecuta la suite de cualificación y persiste el resultado (transición de estados de ADR-0007 §4).</summary>
    public async Task<QualificationRunResult> QualifyAsync(string modelId, QualificationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = _registry.Model(modelId);
        if (model is null)
        {
            throw new ArgumentException("modelo desconocido en el registro: '" + modelId + "'", nameof(modelId));
        }

        var provider = _registry.Provider(model.ProviderId);

        // Consentimiento explícito (ADR-0007 §7): sin él la suite no se ejecuta jamás. El cliente
        // ya lo validó; el Host lo revalida por defensa en profundidad.
        if (!options.ConsentGiven)
        {
            throw new ModelQualificationConsentException(
                "la suite de cualificación requiere consentimiento explícito (--yes o confirmación interactiva)");
        }

        // An absent descriptor is not proof of Local billing. Registry overrides without
        // an explicitly injected provider must not reach the environment-driven fallback.
        if (provider is null && options.Provider is null)
            throw new ModelQualificationCostEvidenceUnavailableException();

        var suiteProbes = Suite(options.Suite);
        IReadOnlyList<Probe> probes = (options.Probes ?? suiteProbes).ToArray();
        if (probes.Count == 0 || probes.Select(probe => probe.Id.ToString()).Distinct(StringComparer.Ordinal).Count() != probes.Count)
            throw new ArgumentException("Qualification probes must be nonempty and have unique IDs.", nameof(options));
        var suiteComplete = ProbeScorer.TaskSetHash(probes) == ProbeScorer.TaskSetHash(suiteProbes);
        var key = QualificationKeyFor(model, provider);
        var existing = _store.Get(key, cancellationToken);
        var previousState = existing?.State ?? ModelQualificationState.Declared;

        var estimatedCost = DeclaredEstimate(probes);
        if (estimatedCost > options.MaxTotalCostUsd)
        {
            throw new ModelQualificationCostCapException(options.MaxTotalCostUsd, estimatedCost);
        }

        // El runner revalida consentimiento y tope como defensa en profundidad; el guard del Host
        // ocurre antes de construir el provider o el runner. Nada se persiste si la suite no completa.
        var selection = Selection(model, provider);
        var requests = probes.Select(probe => new ProbeRequest(probe, selection)).ToArray();
        var pricing = _configuration?.Pricing(model.Id);
        var estimateSource = "declared-probe-maxima";
        var generationAttempts = GenerationAttempts(provider, options.Provider);
        if (generationAttempts is not > 0 && provider?.BillingMode != BillingMode.Local)
            throw new ModelQualificationCostEvidenceUnavailableException();
        var configuredEstimate = ConfiguredEstimate(model, selection, pricing, probes.Count,
            generationAttempts);
        if (configuredEstimate is { } configured)
        {
            estimatedCost = Math.Max(estimatedCost, configured);
            estimateSource = "max-declared-and-configured-descriptor-token-estimate";
        }
        // Unknown is potentially paid, not a free route. Consent does not turn an
        // unavailable estimate into evidence that the accepted monetary cap can be respected.
        else if (provider?.BillingMode is BillingMode.MeteredCurrency or BillingMode.Unknown)
        {
            throw new ModelQualificationCostEvidenceUnavailableException();
        }
        if (estimatedCost > options.MaxTotalCostUsd)
            throw new ModelQualificationCostCapException(options.MaxTotalCostUsd, estimatedCost);

        var runner = new ProbeRunner(ConnectProvider(model, provider, options),
            options.PerProbeTimeout ?? ProbeRunner.DefaultPerProbeTimeout,
            pricing is null ? null : pricing.CostUsd);
        var consent = new QualificationConsent(explicitlyGiven: true, options.MaxTotalCostUsd);
        IReadOnlyList<ProbeResult> results;
        try
        {
            results = await runner.RunSuiteAsync(requests, consent, cancellationToken);
        }
        catch (QualificationCostCapExceededException exception)
        {
            throw new ModelQualificationCostCapException(exception.MaxTotalCostUsd, exception.EstimatedCostUsd);
        }
        catch (QualificationCostEstimateUnavailableException)
        {
            throw new ModelQualificationCostEvidenceUnavailableException();
        }
        catch (ProbeTimeoutException exception)
        {
            throw new ModelQualificationSuiteIncompleteException(["timeout: " + exception.Message]);
        }
        // Envoltorio de los errores tipados del módulo: el CLI solo ve excepciones de Host.

        // The runner can return NotRun after caller cancellation between probes, or
        // finish its last response after cancellation. Preserve cancellation before
        // interpreting statuses or publishing evidence that cannot be committed.
        cancellationToken.ThrowIfCancellationRequested();

        var failures = new List<string>();
        foreach (var result in results)
        {
            if (result.Status is ProbeStatus.Error or ProbeStatus.Timeout or ProbeStatus.NotRun)
            {
                failures.Add(result.Id + ": " + (result.Error ?? result.Status.ToString()));
            }
        }

        if (failures.Count > 0)
        {
            throw new ModelQualificationSuiteIncompleteException(failures);
        }

        // Transición de estado (ADR-0007 §4): suite completa y toda Passed → Qualified;
        // con fallos exactos el perfil queda ProvisionallyClassified con los traits medidos.
        var newState = suiteComplete && results.All(result => result.Status == ProbeStatus.Passed)
            ? ModelQualificationState.Qualified
            : ModelQualificationState.ProvisionallyClassified;

        var traits = MeasuredTraits(probes, results, existing is null
            ? null
            : _store.Traits(key, existing.ProfileRevision, cancellationToken));

        var expectedRevision = existing?.ProfileRevision ?? 0;
        var nextRevision = checked(expectedRevision + 1);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = new FileArtifactStore(_paths.DataDirectory).PutText(
            QualificationEvidenceJson(key, nextRevision, options, suiteComplete, probes, results, traits,
                estimatedCost, estimateSource, generationAttempts), "application/vnd.omnicore.model-qualification+json",
            ArtifactKind.Other, Sensitivity.Sensitive);
        var profile = _store.UpsertWithTraitsAndEvidence(key, expectedRevision, newState,
            QuickProbeSuite.SuiteId, QuickProbeSuite.SuiteVersion,
            traits.Select(trait => new ModelTraitRecord(key.QualificationKeyHash(), nextRevision,
                trait.Trait, trait.Value, trait.Confidence, trait.Samples, trait.Source)).ToArray(),
            evidence, cancellationToken);

        // Recomendación de política operativa (ADR-0044 §6): se calcula y se muestra, pero solo el
        // flujo explícito `omni model policy set` puede aplicarla. Nunca amplía la política.
        var recommendation = QualificationRecommender.Recommend(ToTraitMap(traits));

        return new QualificationRunResult(
            model.Id, model.ProviderId, key.QualificationKeyHash(), previousState.ToString(), newState.ToString(),
            profile.ProfileRevision,
            results.Select(result => new QualificationProbeOutcome(result.Id.ToString(),
                probes.First(p => p.Id.Equals(result.Id)).Kind.ToString(), result.Status.ToString(),
                result.Score, result.Error, result.Duration, result.CostUsd)
                { Usage = ReportedUsage(result) }).ToArray(),
            traits,
            recommendation.Category.ToString(),
            recommendation.MutationPolicy.Mode.ToString(),
            recommendation.MutationPolicy.Delete.ToString(),
            recommendation.MutationPolicy.MoveOrRename.ToString(),
            recommendation.MutationPolicy.MaxFilesPerTurn,
            recommendation.MutationPolicy.MaxChangedLinesPerTurn,
            recommendation.MutationPolicy.MaxRewriteRatio,
            recommendation.Notes,
            estimatedCost,
            options.MaxTotalCostUsd) { EstimatedCostSource = estimateSource,
                EvidenceHash = evidence.Hash.ToString(), EvidenceRedacted = evidence.Redacted,
                SuiteComplete = suiteComplete };
    }

    private static ModelSelection Selection(ModelDefinition model, ProviderDescriptor? provider) =>
        new(new ModelIdValue(model.Id),
            model.RecommendedUsableContext > 0 ? model.RecommendedUsableContext : model.ContextWindow,
            ToolMode.Direct, null, ModelRoutingHost.RouteFor(model, provider).Id,
            maxOutputTokens: ModelRoutingHost.OutputTokenLimit(model, provider));

    // Descriptor-based token upper estimate across known adapter generation sends. Neither
    // this bound nor the configured context is an authenticated charge/token measurement.
    private static decimal? ConfiguredEstimate(ModelDefinition model, ModelSelection selection,
        ModelPricing? pricing, int probeCount, long? maximumAttempts)
    {
        if (pricing?.IsComplete != true || model.ContextWindow <= 0
            || selection.MaxOutputTokens is not { } output || maximumAttempts is not > 0) return null;
        var perProbe = pricing.CostUsd(new TokenUsage(model.ContextWindow, output, 0, 0, 0));
        if (perProbe is null || perProbe < 0) return null;
        try { return checked(perProbe.Value * probeCount * maximumAttempts.Value); }
        catch (OverflowException) { return null; }
    }

    private static long? GenerationAttempts(ProviderDescriptor? descriptor, IModelProvider? injected)
    {
        if (injected is IModelRequestAttemptBound bounded)
        {
            try { return bounded.MaximumGenerationRequestAttempts is > 0 and var bound ? bound : null; }
            catch (Exception) { return null; } // No safe finite evidence; never expose provider getter details.
        }
        if (injected is not null) return null;
        // Normal Host factories use default resilience options, including one Codex refresh cycle.
        var attempts = (long)new OpenAiProviderOptions().MaxRetries + 1;
        return descriptor?.Family == ProviderFamily.OpenAIResponses
            && string.Equals(descriptor.Profile, "codex", StringComparison.Ordinal)
                ? checked(attempts * 2) : attempts;
    }

    /// <summary>
    /// Traits empíricos utilizables por <see cref="ModelProfileResolver"/> (capa Empirical de
    /// ADR-0007 §1): solo los de un perfil Qualified/Calibrated/Stale. Stale sigue usándose con la
    /// confianza reducida registrada en el store (ADR-0007 §4). Resuelve por clave exacta, nunca
    /// por nombre de modelo.
    /// </summary>
    public static IReadOnlyDictionary<string, double>? UsableTraits(IModelQualificationStore store,
        ModelDefinition model, ProviderDescriptor? provider, CancellationToken cancellationToken)
    {
        var snapshot = UsableSnapshot(store, model, provider, cancellationToken);
        return snapshot is null ? null : new Dictionary<string, double>(snapshot.Traits, StringComparer.Ordinal);
    }

    /// <summary>
    /// Captures eligible qualification identity and its traits from one profile read. Trait lookup
    /// is pinned to that exact revision, so callers can fingerprint the same evidence they apply.
    /// </summary>
    internal static ModelQualificationSnapshot? UsableSnapshot(IModelQualificationStore store,
        ModelDefinition model, ProviderDescriptor? provider, CancellationToken cancellationToken,
        string? endpointOverride = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var key = QualificationKeyFor(model, provider, endpointOverride);
        var profile = store.Get(key, cancellationToken);
        if (profile is null || profile.State is not (ModelQualificationState.Qualified
            or ModelQualificationState.Calibrated or ModelQualificationState.Stale))
        {
            return null;
        }

        var traits = store.Traits(key, profile.ProfileRevision, cancellationToken);
        if (traits.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, double>();
        foreach (var trait in traits)
        {
            map[trait.Trait] = trait.Value;
        }

        return new ModelQualificationSnapshot(profile.Key, profile.KeyHash, profile.ProfileRevision,
            profile.State, new ReadOnlyDictionary<string, double>(map));
    }

    /// <summary>
    /// Clave de cualificación de la configuración exacta (ADR-0007 §5): registro + capacidades
    /// declaradas del provider. El ToolCallFormat se deriva igual que en
    /// <see cref="ModelProfileResolver"/>; los probes corren en ToolMode.Direct.
    /// </summary>
    public static ModelQualificationKey QualificationKeyFor(ModelDefinition model, ProviderDescriptor? provider,
        string? endpointOverride = null, string? runtimeBuildOverride = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var format = provider?.SupportsNativeToolCalls == true ? ToolCallFormat.Native
            : provider?.SupportsGrammarPerRequest == true ? ToolCallFormat.Grammar
            : ToolCallFormat.PromptedJson;
        var route = ModelRoutingHost.RouteFor(model, provider, endpointOverride);
        var assembly = typeof(ModelQualificationHost).Assembly;
        var build = runtimeBuildOverride ?? RuntimeBuildIdentity.ForAssembly(assembly);
        return new ModelQualificationKey(model.ProviderId, model.Id, null, null, Array.Empty<string>(),
            null, null, null, provider?.Profile ?? "default", format, ToolMode.Direct, "v1",
            route.Endpoint, route.Protocol.ToString(), build);
    }

    private static IReadOnlyList<Probe> Suite(string suite) => suite switch
    {
        "quick" => QuickProbeSuite.Probes(),
        _ => throw new ModelQualificationUnsupportedSuiteException(suite),
    };

    /// <summary>Maps the provider's reported field mask without inventing absent counts.</summary>
    private static QualificationProbeUsage? ReportedUsage(ProbeResult result)
    {
        if (result.Usage is not { } usage) return null;
        long? Value(TokenUsageFields field, long value) =>
            (result.ReportedUsageFields & field) != 0 ? value : null;
        return new QualificationProbeUsage(Value(TokenUsageFields.Input, usage.Input),
            Value(TokenUsageFields.Output, usage.Output), Value(TokenUsageFields.CacheRead, usage.CacheRead),
            Value(TokenUsageFields.CacheWrite, usage.CacheWrite), Value(TokenUsageFields.Reasoning, usage.Reasoning));
    }

    /// <summary>Suma de topes declarados, no coste observado ni cota derivada de tarifas.</summary>
    public static decimal EstimateSuiteCostUsd(string suite) => DeclaredEstimate(Suite(suite));

    private static decimal DeclaredEstimate(IEnumerable<Probe> probes)
    {
        try
        {
            var total = 0m;
            foreach (var probe in probes) total = checked(total + probe.MaxCostUsd);
            return total;
        }
        catch (OverflowException) { throw new ModelQualificationCostEvidenceUnavailableException(); }
    }

    private IModelProvider ConnectProvider(ModelDefinition model, ProviderDescriptor? provider,
        QualificationOptions options)
    {
        if (options.Provider is { } scripted)
        {
            return scripted;
        }

        // Mismo camino que el runtime (OmniCliRuntime/ask|act): OMNI_BASE_URL + credenciales del
        // usuario, connection TLS de ADR-0011 §4. Ningún provider especial para la suite.
        var baseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? provider?.BaseUrl
            ?? "http://127.0.0.1:8080/v1";
        var secretRef = provider?.Auth.SecretRef ?? "qwen";
        var key = "";
        if (provider?.Auth.Kind == AuthKind.ApiKey)
        {
            key = OmniHost.ResolveApiKey(OmniHost.CreateUserCredentialStore(_paths), secretRef,
                Environment.GetEnvironmentVariable("OMNI_QWEN_KEY"), CancellationToken.None) ?? "";
        }

        var subscription = provider is { Family: ProviderFamily.OpenAIResponses, Profile: "codex" }
            ? OmniHost.CreateChatGptAuth(_paths)
            : null;

        return provider is null
            ? OmniHost.ConnectLocalChatCompletions(baseUrl, model.Id, secretRef, key)
            : OmniHost.ConnectProvider(provider, baseUrl, secretRef, key, subscription: subscription);
    }

    /// <summary>
    /// Traits medidos por la suite (deterministas, sin LLM juez): Reading/Reasoning alimentan
    /// InstructionFollowing y StructuredOutput alimenta StructuredOutputReliability. Los traits
    /// de la revisión anterior que esta suite no mide (p. ej. FileMutationReliability de una suite
    /// full futura) se conservan: re-cualificar no destruye evidencia (ADR-0007 §4).
    /// </summary>
    private static List<QualificationTraitValue> MeasuredTraits(IReadOnlyList<Probe> probes,
        IReadOnlyList<ProbeResult> results, IReadOnlyList<ModelTraitRecord>? previous)
    {
        var instruction = new List<double>();
        var structured = new List<double>();
        var byId = new Dictionary<string, ProbeKind>();
        // El mapeo probe→trait es por tipo, no por nombre: la suite es la única fuente.
        foreach (var probe in probes)
        {
            byId[probe.Id.ToString()] = probe.Kind;
        }

        foreach (var result in results)
        {
            if (!byId.TryGetValue(result.Id.ToString(), out var kind))
            {
                continue;
            }

            switch (kind)
            {
                case ProbeKind.Reading or ProbeKind.Reasoning:
                    instruction.Add(result.Score);
                    break;
                case ProbeKind.StructuredOutput:
                    structured.Add(result.Score);
                    break;
            }
        }

        var traits = new List<QualificationTraitValue>();
        if (instruction.Count > 0)
        {
            traits.Add(new QualificationTraitValue("InstructionFollowing",
                instruction.Average(), Confidence(instruction.Count), instruction.Count, "empirical"));
        }

        if (structured.Count > 0)
        {
            traits.Add(new QualificationTraitValue("StructuredOutputReliability",
                structured.Average(), Confidence(structured.Count), structured.Count, "empirical"));
        }

        var measured = traits.Select(trait => trait.Trait).ToHashSet(StringComparer.Ordinal);
        if (previous is not null)
        {
            foreach (var trait in previous)
            {
                if (!measured.Contains(trait.Trait))
                {
                    traits.Add(new QualificationTraitValue(trait.Trait, trait.Value, trait.Confidence,
                        trait.Samples, trait.Source));
                }
            }
        }

        return traits;
    }

    private static double Confidence(int samples) => Math.Min(1.0, samples / 10.0);

    private static IReadOnlyDictionary<string, double> ToTraitMap(
        IReadOnlyList<QualificationTraitValue> traits)
    {
        var map = new Dictionary<string, double>();
        foreach (var trait in traits)
        {
            map[trait.Trait] = trait.Value;
        }

        return map;
    }

    private static ModelRegistry ToRegistry(IReadOnlyList<ModelRegistryModelDescriptor> models)
    {
        var registry = new ModelRegistry();
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model);
            registry.AddModel(new ModelDefinition(model.Id, model.ProviderId, model.ContextWindow,
                model.RecommendedUsableContext, model.MaxOutputTokens, model.ParameterCountBillions));
        }

        return registry;
    }
}

/// <summary>Immutable identity and values from one usable qualification profile revision.</summary>
internal sealed record ModelQualificationSnapshot(ModelQualificationKey Key, string KeyHash,
    long ProfileRevision, ModelQualificationState State, IReadOnlyDictionary<string, double> Traits);
