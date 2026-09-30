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
        _models.Add(model);
        return this;
    }

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

    public ModelDefinition(string id, string providerId, long contextWindow, long recommendedUsableContext,
        long maxOutputTokens, double? parameterCountBillions = null)
    {
        Id = id;
        ProviderId = providerId;
        ContextWindow = contextWindow;
        RecommendedUsableContext = recommendedUsableContext;
        MaxOutputTokens = maxOutputTokens;
        ParameterCountBillions = parameterCountBillions;
    }
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
