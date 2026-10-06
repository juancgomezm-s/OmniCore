namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Models;

/// <summary>
/// Composición del router (M5): traduce la sección <c>routing:</c> del <c>models.yaml</c> del usuario a
/// una <see cref="RoutingPolicy"/>, construye los candidatos con su perfil efectivo y hechos declarados
/// (local por dirección, precio declarado, política de escritura de ADR-0044) y aplica el router puro.
/// Nunca decide por nombre de modelo: los nombres solo vienen de la configuración (ADR-0011 §2).
/// </summary>
public static class ModelRoutingHost
{
    /// <summary>Explicit output bound for currently wired native API contracts.
    /// Null does not claim an enforced bound for legacy Chat or the subscription backend.</summary>
    public static long? OutputTokenLimit(ModelDefinition model, ProviderDescriptor? provider) =>
        provider?.Family == ProviderFamily.AnthropicMessages
        || (provider?.Family == ProviderFamily.OpenAIResponses
            && !string.Equals(provider.Profile, "codex", StringComparison.OrdinalIgnoreCase))
            ? model.MaxOutputTokens > 0 ? model.MaxOutputTokens : null
            : null;

    /// <summary>One-time initial non-metered authorizations from User configuration.
    /// Later configuration changes never expand a Session snapshot.</summary>
    public static SessionRoutingPolicy InitialSessionPolicy(LoadedUserConfiguration loaded, string originProviderId)
    {
        var routes = loaded.Registry.Models().Select(model => (Model: model, Provider: loaded.Registry.Provider(model.ProviderId)))
            .Where(pair => pair.Provider?.Id == originProviderId
                && pair.Provider.BillingMode is BillingMode.Local or BillingMode.IncludedQuota or BillingMode.CreditBalance)
            .Select(pair => AuthorizedModelRoute.From(RouteFor(pair.Model, pair.Provider), pair.Provider!.BillingMode))
            .ToArray();
        return new SessionRoutingPolicy(1, routes, routes.Select(route => route.BillingMode).Distinct().ToArray(),
            false, loaded.SessionCapUsd, originProviderId).Freeze();
    }

    /// <summary>Ruta 1:1 del YAML existente; un endpoint override define una ruta distinta.</summary>
    public static ModelRoute RouteFor(ModelDefinition model, ProviderDescriptor? provider, string? endpointOverride = null)
    {
        var configured = provider?.BaseUrl ?? "http://127.0.0.1:8080/v1";
        var endpoint = endpointOverride ?? Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? configured;
        var protocol = provider?.Family ?? ProviderFamily.OpenAiChatCompatible;
        return string.Equals(endpoint, configured, StringComparison.Ordinal)
            ? ModelRoute.DefaultForModel(model.Id, model.ProviderId, endpoint, protocol, provider?.Profile)
            : new ModelRoute(model.ProviderId, endpoint, protocol, provider?.Profile, model.Id);
    }

    /// <summary>Política de routing; null si el usuario no configuró <c>routing:</c> (se usa el modelo por defecto).</summary>
    public static RoutingPolicy? Policy(LoadedUserConfiguration loaded)
        => PolicyForCandidates(loaded, Candidates(loaded, _ => false));

    private static RoutingPolicy? PolicyForCandidates(LoadedUserConfiguration loaded, IReadOnlyList<RouteCandidate> candidates)
    {
        var routing = loaded.Models?.Routing;
        if (routing is null) return null;
        var preferences = new Dictionary<RoutingTaskKind, IReadOnlyList<RouteId>>();
        void Add(RoutingTaskKind kind, List<string>? aliases)
        {
            if (aliases is { Count: > 0 }) preferences[kind] = aliases
                .Select(alias => candidates.Single(candidate => candidate.ModelId == loaded.Registry.Resolve(alias).Id).RouteId).ToArray();
        }
        Add(RoutingTaskKind.Meta, routing.Meta);
        Add(RoutingTaskKind.Exploration, routing.Exploration);
        Add(RoutingTaskKind.Implementation, routing.Implementation);
        Add(RoutingTaskKind.Reasoning, routing.Reasoning);
        Add(RoutingTaskKind.Architecture, routing.Architecture);
        return new RoutingPolicy(preferences, routing.PreferLocal ?? false);
    }

    /// <summary>Candidatos del registro con su perfil efectivo y hechos declarados.</summary>
    public static IReadOnlyList<RouteCandidate> Candidates(LoadedUserConfiguration loaded, Func<ModelDefinition, bool> hasWritePolicy,
        ProviderResilienceCatalog? circuits = null)
    {
        var resolver = new ModelProfileResolver();
        var endpointOverride = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        return loaded.Registry.Models().Select(model =>
        {
            var provider = loaded.Registry.Provider(model.ProviderId);
            var price = loaded.Pricing(model.Id)?.InputPricePerMillionUsd;
            var route = RouteFor(model, provider, endpointOverride ?? provider?.BaseUrl ?? "http://127.0.0.1:8080/v1");
            return new RouteCandidate(route, model.Id, resolver.Resolve(model, provider, route: route),
                OmniHost.IsPrivateHost(route.Endpoint),
                circuits?.Snapshot(model.ProviderId)?.CanAttempt ?? true, hasWritePolicy(model), price);
        }).ToArray();
    }

    /// <summary>Elige modelo para una tarea; null si no hay <c>routing:</c> configurado.</summary>
    public static RoutingDecision? Route(LoadedUserConfiguration loaded, RoutingTaskKind kind, bool requiresWrite,
        long estimatedContextTokens, Func<ModelDefinition, bool> hasWritePolicy, ProviderResilienceCatalog? circuits = null)
    {
        if (loaded.Models?.Routing is null) return null;
        var candidates = Candidates(loaded, hasWritePolicy, circuits);
        var policy = PolicyForCandidates(loaded, candidates);
        if (policy is null) return null;
        return ModelRouter.Select(new RoutingRequest(kind, requiresWrite, estimatedContextTokens, [], false),
            candidates, policy);
    }

    /// <summary>
    /// Siguiente modelo de la cadena de escalación que el router acepta para la necesidad actual
    /// (spec §73). Null si no hay cadena, el modo es <c>deny</c> o ningún candidato sirve.
    /// </summary>
    public static RouteCandidate? NextEscalation(LoadedUserConfiguration loaded, string currentModelId, bool requiresWrite,
        long neededContextTokens, Func<ModelDefinition, bool> hasWritePolicy, ProviderResilienceCatalog? circuits = null)
    {
        var model = loaded.Registry.Resolve(currentModelId);
        return NextEscalation(loaded, RouteFor(model, loaded.Registry.Provider(model.ProviderId)).Id,
            requiresWrite, neededContextTokens, hasWritePolicy, circuits);
    }

    public static RouteCandidate? NextEscalation(LoadedUserConfiguration loaded, RouteId currentRouteId, bool requiresWrite,
        long neededContextTokens, Func<ModelDefinition, bool> hasWritePolicy, ProviderResilienceCatalog? circuits = null)
    {
        var escalation = loaded.Models?.Routing?.Escalation;
        if (escalation?.Chain is not { Count: > 0 } chain || EscalationMode(loaded) == "deny") return null;
        var candidates = Candidates(loaded, hasWritePolicy, circuits);
        var chainIds = chain.Select(alias => candidates.Single(candidate => candidate.ModelId == loaded.Registry.Resolve(alias).Id).RouteId)
            .Where(id => !id.Equals(currentRouteId)).ToArray();
        if (chainIds.Length == 0) return null;
        var policy = new RoutingPolicy(new Dictionary<RoutingTaskKind, IReadOnlyList<RouteId>>
        {
            [RoutingTaskKind.Reasoning] = chainIds,
        }, false);
        if (EscalationMode(loaded) == "auto")
        {
            // El coste pagado desconocido se excluye antes de la selección automática.
            candidates = candidates.Where(c =>
                loaded.Registry.Provider(c.Route.ProviderId)?.Auth
                    is not { Kind: AuthKind.ApiKey } || loaded.Pricing(c.ModelId) is { IsComplete: true }).ToArray();
        }
        try
        {
            return ModelRouter.Select(new RoutingRequest(RoutingTaskKind.Reasoning, requiresWrite, neededContextTokens, [], false),
                candidates, policy).Chosen;
        }
        catch (NoRouteAvailableException) { return null; }
    }

    /// <summary><c>auto</c> | <c>ask</c> | <c>deny</c>; por defecto <c>ask</c> (una escalación nunca es silenciosa).</summary>
    public static string EscalationMode(LoadedUserConfiguration loaded) =>
        loaded.Models?.Routing?.Escalation?.Mode ?? "ask";
}
