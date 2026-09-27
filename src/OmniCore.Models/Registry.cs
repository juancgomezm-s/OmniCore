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
}

/// <summary>Definición de un modelo: provider + hechos (ADR-0007 §1).</summary>
public sealed class ModelDefinition
{
    public string Id { get; }

    public string ProviderId { get; }

    public long ContextWindow { get; }

    public long RecommendedUsableContext { get; }

    public long MaxOutputTokens { get; }

    public ModelDefinition(string id, string providerId, long contextWindow, long recommendedUsableContext,
        long maxOutputTokens)
    {
        Id = id;
        ProviderId = providerId;
        ContextWindow = contextWindow;
        RecommendedUsableContext = recommendedUsableContext;
        MaxOutputTokens = maxOutputTokens;
    }
}

/// <summary>Error tipado cuando no hay modelo configurado (ADR-0011 §2; spec §71).</summary>
public sealed class NoModelConfiguredException : InvalidOperationException
{
    public NoModelConfiguredException()
    {
    }
}