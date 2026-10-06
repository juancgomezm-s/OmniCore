namespace OmniCore.Engine;

using System.Collections.Immutable;
using OmniCore.Domain;

/// <summary>Tipo de tarea de enrutamiento (spec §21).</summary>
public enum RoutingTaskKind
{
    Meta,
    Exploration,
    Implementation,
    Reasoning,
    Architecture,
}

/// <summary>Solicitud de enrutamiento con hechos declarados (ADR-0007, ADR-0011 §2).</summary>
/// <remarks>
/// RequiredCapabilities values:
/// - "native-tools" (profile.ToolCallFormats contains Native)
/// - "vision" (InputModalities contains "image")
/// </remarks>
public sealed record RoutingRequest(
    RoutingTaskKind Kind,
    bool RequiresWrite,
    long EstimatedContextTokens,
    IReadOnlyList<string> RequiredCapabilities,
    bool LocalOnly);

/// <summary>Concrete route with a separate logical model identity and declared routing facts.</summary>
public sealed record RouteCandidate(
    ModelRoute Route,
    string ModelId,
    EffectiveModelProfile Profile,
    bool IsLocal,
    bool Available,
    bool HasWritePolicy,
    decimal? PricePerMillionTokensUsd)
{
    public RouteId RouteId => Route.Id;
}

/// <summary>Política de enrutamiento: listas de preferencia por tipo de tarea y preferencia local (ADR-0007).</summary>
public sealed record RoutingPolicy(
    IReadOnlyDictionary<RoutingTaskKind, IReadOnlyList<RouteId>> Preferences,
    bool PreferLocal);

/// <summary>Razón de rechazo de un candidato.</summary>
public enum RouteRejection
{
    Unavailable,
    ContextTooSmall,
    MissingCapability,
    NotLocal,
    NoWritePolicy,
    NotInPreferences,
}

/// <summary>Candidato rechazado con su primera razón de fallo.</summary>
public sealed record RejectedRoute(RouteId RouteId, string ModelId, RouteRejection Reason);

/// <summary>Decisión de enrutamiento: elegido + rechazados con razones.</summary>
public sealed record RoutingDecision(
    RouteCandidate Chosen,
    IReadOnlyList<RejectedRoute> Rejected);

/// <summary>
/// Error tipado cuando no hay ruta disponible (INV-017, ADR-0011).
/// Mensaje en español; expone la lista completa de rechazos.
/// </summary>
public sealed class NoRouteAvailableException : InvalidOperationException
{
    public IReadOnlyList<RejectedRoute> Rejected { get; }

    public NoRouteAvailableException(IReadOnlyList<RejectedRoute> rejected)
        : base("No hay modelo disponible para la tarea: " + FormatRejections(rejected))
    {
        Rejected = rejected.ToImmutableList();
    }

    private static string FormatRejections(IReadOnlyList<RejectedRoute> rejected)
    {
        var groups = rejected.GroupBy(r => r.Reason)
            .Select(g => g.Key + ": " + string.Join(", ", g.Select(r => r.ModelId + " [" + r.RouteId.Value + "]")))
            .ToArray();
        return string.Join("; ", groups);
    }
}

/// <summary>
/// Router puro de modelos: consume SOLO EffectiveModelProfile + hechos declarados.
/// Nunca ramifica por nombre de modelo o proveedor (INV-007).
/// Selects physical RouteIds; logical model ids are retained for lookup and display only.
/// </summary>
public static class ModelRouter
{
    /// <summary>
    /// Selecciona el mejor candidato según las reglas de enrutamiento (spec §21, ADR-0007, ADR-0011 §2).
    /// </summary>
    /// <param name="request">Solicitud con tipo de tarea, requisitos y capacidades.</param>
    /// <param name="candidates">Candidatos disponibles con sus perfiles y hechos.</param>
    /// <param name="policy">Política de preferencias por tipo de tarea y preferencia local.</param>
    /// <returns>Decisión con el elegido y todos los rechazados con su primera razón.</returns>
    /// <exception cref="NoRouteAvailableException">Si no sobrevive ningún candidato.</exception>
    public static RoutingDecision Select(
        RoutingRequest request,
        IReadOnlyList<RouteCandidate> candidates,
        RoutingPolicy policy)
    {
        // 1. Obtener lista de preferencias para el Kind (puede ser null/empty)
        var preferenceList = policy.Preferences.TryGetValue(request.Kind, out var prefs) ? prefs : null;

        // 2. Evaluar cada candidato: rechazar con primera razón que falle, o marcar como sobreviviente
        var rejected = new List<RejectedRoute>();
        var survivors = new List<RouteCandidate>();

        foreach (var candidate in candidates)
        {
            var rejection = EvaluateCandidate(candidate, request, preferenceList);
            if (rejection is not null)
            {
                rejected.Add(new RejectedRoute(candidate.RouteId, candidate.ModelId, rejection.Value));
            }
            else
            {
                survivors.Add(candidate);
            }
        }

        // 3. Si no hay preferencia explícita para este Kind, todos los candidatos son elegibles
        //    en su orden original (los que no fueron rechazados por otras razones)
        if (preferenceList is null || preferenceList.Count == 0)
        {
            // survivors ya están en orden original
        }
        else
        {
            // Reordenar survivors según preferenceList (los que están en la lista primero, en ese orden)
            var orderedSurvivors = new List<RouteCandidate>();
            var survivorSet = survivors.ToHashSet();

            foreach (var routeId in preferenceList)
            {
                var match = survivors.FirstOrDefault(c => c.RouteId.Equals(routeId));
                if (match is not null)
                {
                    orderedSurvivors.Add(match);
                    survivorSet.Remove(match);
                }
            }

            // Los supervivientes que no estaban en la lista de preferencias van al final en su orden original
            foreach (var s in survivors)
            {
                if (survivorSet.Contains(s))
                {
                    orderedSurvivors.Add(s);
                }
            }

            survivors = orderedSurvivors;
        }

        // 4. PreferLocal: locales primero (estable), luego remotos (estable)
        if (policy.PreferLocal)
        {
            // OrderBy es estable: entre locales (y entre remotos) se conserva el orden de preferencia.
            survivors = survivors.OrderByDescending(c => c.IsLocal).ToList();
        }

        // 5. Elegir el primero o lanzar
        if (survivors.Count == 0)
        {
            throw new NoRouteAvailableException(rejected);
        }

        var chosen = survivors[0];

        // 6. Construir lista final de rechazados: todos los que no son el elegido
        var finalRejected = new List<RejectedRoute>(rejected);

        // Añadir supervivientes no elegidos como NotInPreferences (o mantener su razón original si la tenían)
        var rejectedRoutes = rejected.Select(r => r.RouteId).ToHashSet();
        foreach (var s in survivors.Skip(1))
        {
            if (!rejectedRoutes.Contains(s.RouteId))
            {
                finalRejected.Add(new RejectedRoute(s.RouteId, s.ModelId, RouteRejection.NotInPreferences));
            }
        }

        return new RoutingDecision(chosen, finalRejected.ToImmutableList());
    }

    private static RouteRejection? EvaluateCandidate(
        RouteCandidate candidate,
        RoutingRequest request,
        IReadOnlyList<RouteId>? preferenceList)
    {
        // Unavailable
        if (!candidate.Available)
        {
            return RouteRejection.Unavailable;
        }

        // ContextTooSmall
        if (candidate.Profile.RecommendedUsableContext < request.EstimatedContextTokens)
        {
            return RouteRejection.ContextTooSmall;
        }

        // MissingCapability
        foreach (var cap in request.RequiredCapabilities)
        {
            if (!HasCapability(candidate.Profile, cap))
            {
                return RouteRejection.MissingCapability;
            }
        }

        // NotLocal
        if (request.LocalOnly && !candidate.IsLocal)
        {
            return RouteRejection.NotLocal;
        }

        // NoWritePolicy (ADR-0044: a writing task never goes to a model without a sufficient policy)
        if (request.RequiresWrite && !candidate.HasWritePolicy)
        {
            return RouteRejection.NoWritePolicy;
        }

        // NotInPreferences: physical route membership, never a logical model or display alias.
        if (preferenceList is not null && preferenceList.Count > 0 && !preferenceList.Contains(candidate.RouteId))
        {
            return RouteRejection.NotInPreferences;
        }

        return null;
    }

    private static bool HasCapability(EffectiveModelProfile profile, string capability)
    {
        return capability switch
        {
            "native-tools" => profile.ToolCallFormats.Contains(ToolCallFormat.Native),
            "vision" => profile.InputModalities.Contains("image"),
            _ => false, // capacidades desconocidas = no soportadas
        };
    }
}
