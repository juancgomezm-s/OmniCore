namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Validates the durable model binding history for one Session.</summary>
public static class SessionModelSelectionProjection
{
    public static SessionModelSelection? Read(IEnumerable<DomainEvent> events, IEventCodecRegistry codecs,
        SessionId session)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(codecs);
        var history = events.Where(item => item.SessionId == session).OrderBy(item => item.Sequence).ToArray();
        // Validate every route-policy change against its original user consent before using it
        // as an authorization source for a model selection.
        _ = SessionRoutingAuthorization.Read(history, codecs, session);
        SessionModelSelection? current = null;
        SessionRoutingPolicy? routing = null;
        var hasSession = false;
        foreach (var evt in history)
        {
            switch (codecs.Decode(evt))
            {
                case SessionCreated created when created.SessionId == session:
                    hasSession = true;
                    break;
                case SessionRoutingPolicySet routingSet when routingSet.SessionId == session:
                    routing = routingSet.Policy;
                    break;
                case SessionRoutingPolicyRevised routingRevised when routingRevised.SessionId == session:
                    routing = routingRevised.Policy;
                    break;
                case SessionModelSelectionSet selectionSet:
                    if (selectionSet.SessionId != session || !hasSession || current is not null
                        || selectionSet.Selection.Freeze().Revision != 1
                        || !RouteIsAllowed(routing, selectionSet.Selection))
                        throw new InvalidDataException("Invalid initial Session model selection.");
                    current = selectionSet.Selection;
                    break;
                case SessionModelSelectionRevised selectionRevised:
                    if (selectionRevised.SessionId != session || !hasSession || current is null
                        || selectionRevised.ExpectedRevision != current.Revision
                        || selectionRevised.Selection.Freeze().Revision != checked(current.Revision + 1)
                        || !RouteIsAllowed(routing, selectionRevised.Selection))
                        throw new InvalidDataException("Invalid Session model selection revision.");
                    current = selectionRevised.Selection;
                    break;
            }
        }
        return current;
    }

    private static bool RouteIsAllowed(SessionRoutingPolicy? policy, SessionModelSelection selection) =>
        policy is not null && (policy.CrossProviderRouting || policy.OriginProviderId.Length == 0
            || StringComparer.Ordinal.Equals(policy.OriginProviderId, selection.ProviderId))
        && policy.AllowedRoutes.Any(route => route.RouteId.Equals(selection.RouteId)
            && StringComparer.Ordinal.Equals(route.ProviderId, selection.ProviderId)
            && StringComparer.Ordinal.Equals(route.IdentityHash, selection.RouteIdentityHash)
            && route.BillingMode == selection.BillingMode)
        && policy.BillingPolicy.Contains(selection.BillingMode);
}
