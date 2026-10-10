namespace OmniCore.Models;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Descriptor de un provider (ADR-0011 §1, ADR-0005 §2): familia, endpoint, auth y compat
/// flags. Nunca contiene datos de secretos.
/// </summary>
public sealed class ProviderDescriptor
{
    public string Id { get; }

    public ProviderFamily Family { get; }

    public string BaseUrl { get; }

    public AuthConfig Auth { get; }

    public bool SupportsJsonSchemaPerRequest { get; }

    public bool SupportsGrammarPerRequest { get; }

    public bool SupportsNativeToolCalls { get; }

    /// <summary>
    /// Certificado raíz de confianza para el TLS de este provider (<c>caCertificate</c> en
    /// providers.yaml, PEM o DER). Con él, el certificado del servidor se valida contra esa raíz
    /// y el nombre del host debe coincidir; sin él rige la validación estándar del sistema.
    /// </summary>
    public string? TrustedCertificatePath { get; init; }

    /// <summary>Perfil dentro de la familia (p. ej. <c>codex</c> en OpenAIResponses: suscripción ChatGPT, ADR-0011 §3.4).</summary>
    public string? Profile { get; init; }

    /// <summary>Explicit provider billing declaration; never inferred from endpoint or credentials.</summary>
    public BillingMode BillingMode { get; init; } = OmniCore.Domain.BillingMode.Unknown;

    /// <summary>
    /// Cómo se relaciona OmniCore con el servidor local de este provider (ADR-0011 §4); null para
    /// providers remotos, que no tienen servidor que supervisar.
    /// </summary>
    public LocalHostConfig? LocalHost { get; init; }

    public ProviderDescriptor(string id, ProviderFamily family, string baseUrl, AuthConfig auth,
        bool supportsJsonSchemaPerRequest, bool supportsGrammarPerRequest, bool supportsNativeToolCalls)
    {
        Id = id;
        Family = family;
        BaseUrl = baseUrl;
        Auth = auth;
        SupportsJsonSchemaPerRequest = supportsJsonSchemaPerRequest;
        SupportsGrammarPerRequest = supportsGrammarPerRequest;
        SupportsNativeToolCalls = supportsNativeToolCalls;
    }
}

/// <summary>Error tipado cuando un alias de modelo es ambiguo (dos modelos lo declaran, o coincide con otro id).</summary>
public sealed class AmbiguousModelAliasException : InvalidOperationException
{
    public string Alias { get; }

    public string ModelId1 { get; }

    public string ModelId2 { get; }

    public LocalizedText UserMessage { get; }

    public AmbiguousModelAliasException(string alias, string modelId1, string modelId2)
        : base($"Model alias '{alias}' is declared by multiple models: {modelId1}, {modelId2}.")
    {
        Alias = alias;
        ModelId1 = modelId1;
        ModelId2 = modelId2;
        UserMessage = LocalizedText.Of("models.aliasAmbiguous", ("alias", alias), ("model1", modelId1), ("model2", modelId2));
    }
}

/// <summary>Error tipado cuando no se encuentra un modelo por id ni por alias.</summary>
public sealed class UnknownModelException : InvalidOperationException
{
    public string Name { get; }

    public LocalizedText UserMessage { get; }

    public UnknownModelException(string name)
        : base($"Unknown model or alias: {name}.")
    {
        Name = name;
        UserMessage = LocalizedText.Of("models.unknown", "name", name);
    }
}

/// <summary>Registro mínimo de providers y modelos (ADR-0011 §2).</summary>
public sealed class ModelRegistry
{
    private readonly List<ProviderDescriptor> _providers = new();

    private readonly List<ModelDefinition> _models = new();

    public ModelRegistry Add(ProviderDescriptor provider)
    {
        _providers.Add(provider);
        return this;
    }

    public ModelRegistry AddModel(ModelDefinition model)
    {
        // Check for ambiguity at registration time (invariant 20)
        foreach (var alias in model.Aliases)
        {
            // Another model declares the same alias
            foreach (var existing in _models)
            {
                if (existing.Aliases.Contains(alias, StringComparer.Ordinal))
                {
                    throw new AmbiguousModelAliasException(alias, existing.Id, model.Id);
                }
                // Alias equals another model's id
                if (existing.Id.Equals(alias, StringComparison.Ordinal))
                {
                    throw new AmbiguousModelAliasException(alias, existing.Id, model.Id);
                }
            }
        }
        // New model's id equals an existing model's alias
        foreach (var existing in _models)
        {
            if (existing.Aliases.Contains(model.Id, StringComparer.Ordinal))
            {
                throw new AmbiguousModelAliasException(model.Id, existing.Id, model.Id);
            }
        }

        _models.Add(model);
        return this;
    }

    /// <summary>Todos los providers registrados, en orden de declaración (solo lectura).</summary>
    public IReadOnlyList<ProviderDescriptor> Providers() => _providers.ToArray();

    public ProviderDescriptor? Provider(string id)
    {
        foreach (var p in _providers)
        {
            if (p.Id.Equals(id, StringComparison.Ordinal))
            {
                return p;
            }
        }

        return null;
    }

    public ModelDefinition? Model(string id)
    {
        foreach (var m in _models)
        {
            if (m.Id.Equals(id, StringComparison.Ordinal))
            {
                return m;
            }
        }

        return null;
    }

    public IReadOnlyList<ModelDefinition> Models() => _models.ToArray();

    /// <summary>Modelo por defecto (el primero configurado). Lanza si no hay ninguno.</summary>
    /// <exception cref="NoModelConfiguredException">No hay ningún modelo configurado.</exception>
    public ModelDefinition ResolveDefault() =>
        _models.Count > 0 ? _models[0] : throw new NoModelConfiguredException();

    /// <summary>
    /// Resuelve un modelo por id exacto o por alias único (ADR-0011 §2).
    /// <para>Orden de precedencia:</para>
    /// <list type="number">
    ///   <item><description>Coincidencia exacta de <c>Id</c> (case-sensitive, ordinal).</description></item>
    ///   <item><description>Alias único declarado por un solo modelo.</description></item>
    /// </list>
    /// <para>La ambigüedad se detecta en <see cref="AddModel"/>; aquí solo puede fallar por nombre desconocido.</para>
    /// </summary>
    /// <exception cref="UnknownModelException">No hay modelo con ese id ni alias.</exception>
    public ModelDefinition Resolve(string nameOrAlias)
    {
        // 1) Coincidencia exacta de Id (ordinal, case-sensitive) - el id siempre gana
        foreach (var m in _models)
        {
            if (m.Id.Equals(nameOrAlias, StringComparison.Ordinal))
            {
                return m;
            }
        }

        // 2) Buscar por alias (garantizado único por AddModel)
        foreach (var m in _models)
        {
            foreach (var alias in m.Aliases)
            {
                if (alias.Equals(nameOrAlias, StringComparison.Ordinal))
                {
                    return m;
                }
            }
        }

        throw new UnknownModelException(nameOrAlias);
    }
}

/// <summary>Definición de un modelo: provider + hechos (ADR-0007 §1).</summary>
public sealed class ModelDefinition
{
    public string Id { get; }

    public string ProviderId { get; }

    public long ContextWindow { get; }

    public long RecommendedUsableContext { get; }

    public long MaxOutputTokens { get; }

    public double? ParameterCountBillions { get; }

    /// <summary>Alias declarativos para este modelo (p. ej. "local", "frontier", "fast").</summary>
    public IReadOnlyList<string> Aliases { get; }
    public ReasoningCapability ReasoningCapability { get; }

    public ModelDefinition(string id, string providerId, long contextWindow, long recommendedUsableContext,
        long maxOutputTokens, double? parameterCountBillions = null, IEnumerable<string>? aliases = null,
        ReasoningCapability? reasoningCapability = null)
    {
        Id = id;
        ProviderId = providerId;
        ContextWindow = contextWindow;
        RecommendedUsableContext = recommendedUsableContext;
        MaxOutputTokens = maxOutputTokens;
        ParameterCountBillions = parameterCountBillions;
        Aliases = (aliases ?? Array.Empty<string>()).ToArray();
        ReasoningCapability = reasoningCapability ?? OmniCore.Domain.ReasoningCapability.Unknown;
    }
}

/// <summary><c>attach</c>: servidor ya levantado; <c>managed</c>: lo lanza y supervisa OmniCore (ADR-0011 §4).</summary>
public enum LocalHostMode
{
    Attach,
    Managed,
}

/// <summary>
/// Configuración del servidor local de un provider (<c>host</c> y <c>managed</c> en providers.yaml).
/// Con <c>Managed</c>, el <c>baseUrl</c> de la ruta es una identidad lógica estable y el endpoint real
/// (puerto efímero) se resuelve al arrancar; un puerto explícito en <c>baseUrl</c> lo fija.
/// <c>Declared</c> es false cuando el modo es el <c>attach</c> implícito de un provider local sin <c>host</c>:
/// <c>omni doctor</c> lo diagnostica, pero el runtime no lo sondea antes de cada Turn.
/// </summary>
public sealed record LocalHostConfig(LocalHostMode Mode, string? Executable = null,
    IReadOnlyList<string>? Args = null, string? WorkingDirectory = null, int FixedPort = 0,
    TimeSpan? ReadinessTimeout = null, bool Declared = true)
{
    /// <summary>Endpoint lógico de un provider managed con puerto efímero (loopback, sin puerto).</summary>
    public const string ManagedLogicalBaseUrl = "http://127.0.0.1/v1";

    public static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(120);

    public TimeSpan EffectiveReadinessTimeout => ReadinessTimeout ?? DefaultReadinessTimeout;
}

/// <summary>Error tipado cuando una familia de provider aún no está implementada.</summary>
public sealed class ProviderFamilyNotSupportedException : NotSupportedException
{
    public ProviderFamily Family { get; }

    public LocalizedText UserMessage => LocalizedText.Of("provider.familyNotSupported", "family", Family.ToString());

    public ProviderFamilyNotSupportedException(ProviderFamily family)
        : base("Provider family is not supported: " + family) => Family = family;
}

/// <summary>Error tipado cuando no hay modelo configurado (ADR-0011 §2; spec §71).</summary>
public sealed class NoModelConfiguredException : InvalidOperationException
{
    public LocalizedText UserMessage { get; } = LocalizedText.Of("models.noneConfigured");

    public NoModelConfiguredException()
        : base("No model is configured.")
    {
    }
}
