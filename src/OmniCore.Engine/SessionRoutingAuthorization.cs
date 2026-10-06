using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Engine;

/// <summary>Durable Session authorization. Credentials, prices and router preferences do not grant it.</summary>
public static class SessionRoutingAuthorization
{
    public sealed record Offer(int PolicyRevision, AuthorizedModelRoute Route);

    public static SessionRoutingPolicy? Read(IEnumerable<DomainEvent> events, IEventCodecRegistry codecs, SessionId session)
    {
        SessionRoutingPolicy? policy = null;
        var requests = new Dictionary<InteractionId, InteractionRequested>();
        var resolutions = new Dictionary<InteractionId, InteractionResolved>();
        foreach (var evt in events.Where(evt => evt.SessionId == session).OrderBy(evt => evt.Sequence))
        {
            switch (codecs.Decode(evt))
            {
                case InteractionRequested request: requests[request.InteractionId] = request; break;
                case InteractionResolved resolution:
                    if (requests.TryGetValue(resolution.InteractionId, out var requested)
                        && requested.Kind == InteractionKind.ModelRouteConsent
                        && !resolutions.TryAdd(resolution.InteractionId, resolution))
                        throw new InvalidDataException("Duplicate routing interaction resolution");
                    break;
                case InteractionExpired expired:
                    requests.Remove(expired.InteractionId);
                    break;
                case SessionRoutingPolicySet set:
                    if (set.SessionId != session || policy is not null || set.Policy.Revision != 1)
                        throw new InvalidDataException("Invalid initial Session routing policy");
                    policy = set.Policy.Freeze();
                    break;
                case SessionRoutingPolicyRevised revised:
                    if (revised.SessionId != session || policy is null
                        || !requests.TryGetValue(revised.InteractionId, out var consentRequest)
                        || !resolutions.TryGetValue(revised.InteractionId, out var consentResolution)
                        || consentResolution.Cause != InteractionCause.User || consentResolution.OptionId != "allow_route"
                        || Parse(consentRequest) is not { } offer || offer.PolicyRevision != policy.Revision)
                        throw new InvalidDataException("Routing policy revision lacks matching user consent");
                    var expected = policy.Grant(offer.Route);
                    var actual = revised.Policy.Freeze();
                    if (!Equivalent(expected, actual))
                        throw new InvalidDataException("Routing policy revision expands beyond user consent");
                    policy = actual;
                    requests.Remove(revised.InteractionId);
                    break;
            }
        }
        return policy;
    }

    private static bool Equivalent(SessionRoutingPolicy left, SessionRoutingPolicy right) =>
        left.Revision == right.Revision && left.CrossProviderRouting == right.CrossProviderRouting
        && left.SessionSpendLimit == right.SessionSpendLimit && left.OriginProviderId == right.OriginProviderId
        && left.AllowedRoutes.SequenceEqual(right.AllowedRoutes) && left.BillingPolicy.SequenceEqual(right.BillingPolicy);

    public static string Context(Offer offer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("routingConsent", 1);
            writer.WriteNumber("policyRevision", offer.PolicyRevision);
            writer.WriteString("routeId", offer.Route.RouteId.Value);
            writer.WriteString("providerId", offer.Route.ProviderId);
            writer.WriteString("identityHash", offer.Route.IdentityHash);
            writer.WriteString("billingMode", offer.Route.BillingMode.ToString());
            writer.WriteString("target", offer.Route.RouteId.Value);
            writer.WriteString("detail", offer.Route.ProviderId + " · " + offer.Route.BillingMode);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static Offer? Parse(InteractionRequested request)
    {
        if (request.Kind != InteractionKind.ModelRouteConsent) return null;
        try
        {
            using var document = JsonDocument.Parse(request.SubjectJson);
            var root = document.RootElement;
            if (root.GetProperty("routingConsent").GetInt32() != 1) return null;
            var revision = root.GetProperty("policyRevision").GetInt32();
            var billing = root.GetProperty("billingMode").GetString();
            if (!Enum.TryParse<BillingMode>(billing, out var mode) || !Enum.IsDefined(mode)
                || mode.ToString() != billing || revision < 1) return null;
            var route = new AuthorizedModelRoute(new RouteId(root.GetProperty("routeId").GetString()!),
                root.GetProperty("providerId").GetString()!, root.GetProperty("identityHash").GetString()!, mode);
            // Domain validation also rejects malformed/duplicate identities and undefined billing modes.
            new SessionRoutingPolicy(revision, new[] { route }, new[] { mode }, false, null, route.ProviderId).Freeze();
            return new(revision, route);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or ArgumentException or OverflowException)
        { return null; }
    }

    public static SessionRoutingPolicy Resolve(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        SessionId session, InteractionRequested request)
    {
        var offer = Parse(request) ?? throw new InvalidInteractionOptionException(request.InteractionId, "allow_route");
        var policy = Read(events, codecs, session)
            ?? throw new InvalidInteractionOptionException(request.InteractionId, "allow_route");
        if (policy.Revision != offer.PolicyRevision)
            throw new InvalidInteractionOptionException(request.InteractionId, "allow_route");
        return policy.Grant(offer.Route);
    }
}
