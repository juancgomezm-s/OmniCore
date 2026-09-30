namespace OmniCore.Host;

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
    /// <summary>Política de routing; null si el usuario no configuró <c>routing:</c> (se usa el modelo por defecto).</summary>
    public static RoutingPolicy? Policy(LoadedUserConfiguration loaded)
    {
        var routing = loaded.Models?.Routing;
        if (routing is null) return null;
        var preferences = new Dictionary<RoutingTaskKind, IReadOnlyList<string>>();
        void Add(RoutingTaskKind kind, List<string>? aliases)
        {
            if (aliases is { Count: > 0 }) preferences[kind] = aliases.Select(a => loaded.Registry.Resolve(a).Id).ToArray();
        }
        Add(RoutingTaskKind.Meta, routing.Meta);
        Add(RoutingTaskKind.Exploration, routing.Exploration);
        Add(RoutingTaskKind.Implementation, routing.Implementation);
        Add(RoutingTaskKind.Reasoning, routing.Reasoning);
        Add(RoutingTaskKind.Architecture, routing.Architecture);
        return new RoutingPolicy(preferences, routing.PreferLocal ?? false);
    }

    /// <summary>Candidatos del registro con su perfil efectivo y hechos declarados.</summary>
    public static IReadOnlyList<RouteCandidate> Candidates(LoadedUserConfiguration loaded, Func<ModelDefinition, bool> hasWritePolicy)
    {
        var resolver = new ModelProfileResolver();
        return loaded.Registry.Models().Select(model =>
        {
            var provider = loaded.Registry.Provider(model.ProviderId);
            var price = loaded.Pricing(model.Id)?.InputPricePerMillionUsd;
            return new RouteCandidate(model.Id, resolver.Resolve(model, provider),
                provider is not null && OmniHost.IsPrivateHost(provider.BaseUrl), true, hasWritePolicy(model), price);
        }).ToArray();
    }

    /// <summary>Elige modelo para una tarea; null si no hay <c>routing:</c> configurado.</summary>
    public static RoutingDecision? Route(LoadedUserConfiguration loaded, RoutingTaskKind kind, bool requiresWrite,
        long estimatedContextTokens, Func<ModelDefinition, bool> hasWritePolicy)
    {
        var policy = Policy(loaded);
        if (policy is null) return null;
        return ModelRouter.Select(new RoutingRequest(kind, requiresWrite, estimatedContextTokens, [], false),
            Candidates(loaded, hasWritePolicy), policy);
    }

    /// <summary>
    /// Siguiente modelo de la cadena de escalación que el router acepta para la necesidad actual
    /// (spec §73). Null si no hay cadena, el modo es <c>deny</c> o ningún candidato sirve.
    /// </summary>
    public static RouteCandidate? NextEscalation(LoadedUserConfiguration loaded, string currentModelId, bool requiresWrite,
        long neededContextTokens, Func<ModelDefinition, bool> hasWritePolicy)
    {
        var escalation = loaded.Models?.Routing?.Escalation;
        if (escalation?.Chain is not { Count: > 0 } chain || EscalationMode(loaded) == "deny") return null;
        var chainIds = chain.Select(a => loaded.Registry.Resolve(a).Id).Where(id => id != currentModelId).ToArray();
        var policy = new RoutingPolicy(new Dictionary<RoutingTaskKind, IReadOnlyList<string>>
        {
            [RoutingTaskKind.Reasoning] = chainIds,
        }, false);
        try
        {
            return ModelRouter.Select(new RoutingRequest(RoutingTaskKind.Reasoning, requiresWrite, neededContextTokens, [], false),
                Candidates(loaded, hasWritePolicy), policy).Chosen;
        }
        catch (NoRouteAvailableException) { return null; }
    }

    /// <summary><c>auto</c> | <c>ask</c> | <c>deny</c>; por defecto <c>ask</c> (una escalación nunca es silenciosa).</summary>
    public static string EscalationMode(LoadedUserConfiguration loaded) =>
        loaded.Models?.Routing?.Escalation?.Mode ?? "ask";
}
