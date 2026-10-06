namespace OmniCore.Domain;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

/// <summary>A specifically authorized route binding within a session policy.</summary>
public sealed record AuthorizedModelRoute
{
    public RouteId RouteId { get; }
    public string ProviderId { get; }
    public string IdentityHash { get; }
    public BillingMode BillingMode { get; }

    public AuthorizedModelRoute(RouteId routeId, string providerId, string identityHash, BillingMode billingMode)
    {
        ArgumentNullException.ThrowIfNull(routeId);
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (!IsSha256(identityHash)) throw new ArgumentException("Identity hash must be lowercase SHA-256 hex.", nameof(identityHash));
        if (!Enum.IsDefined(billingMode)) throw new ArgumentOutOfRangeException(nameof(billingMode));
        RouteId = routeId;
        ProviderId = providerId;
        IdentityHash = identityHash;
        BillingMode = billingMode;
    }

    public static AuthorizedModelRoute From(ModelRoute route, BillingMode billingMode)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!Enum.IsDefined(billingMode)) throw new ArgumentOutOfRangeException(nameof(billingMode));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(route.CanonicalJson())));
        return new AuthorizedModelRoute(route.Id, route.ProviderId, hash, billingMode);
    }

    internal static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Frozen authorization boundary for model routes in one session (ADR-0046 §3).</summary>
public sealed record SessionRoutingPolicy
{
    public int Revision { get; }
    public IReadOnlyList<AuthorizedModelRoute> AllowedRoutes { get; }
    public IReadOnlyList<BillingMode> BillingPolicy { get; }
    public bool CrossProviderRouting { get; }
    public decimal? SessionSpendLimit { get; }
    public string OriginProviderId { get; }

    public SessionRoutingPolicy(int revision, IReadOnlyList<AuthorizedModelRoute> allowedRoutes,
        IReadOnlyList<BillingMode> billingPolicy, bool crossProviderRouting, decimal? sessionSpendLimit,
        string originProviderId)
    {
        Revision = revision;
        AllowedRoutes = (allowedRoutes ?? throw new ArgumentNullException(nameof(allowedRoutes))).ToImmutableArray();
        BillingPolicy = (billingPolicy ?? throw new ArgumentNullException(nameof(billingPolicy))).ToImmutableArray();
        CrossProviderRouting = crossProviderRouting;
        SessionSpendLimit = sessionSpendLimit;
        OriginProviderId = originProviderId ?? throw new ArgumentNullException(nameof(originProviderId));
    }

    public static SessionRoutingPolicy Empty() => new(1, [], [], false, null, "");

    /// <summary>Revalidates and copies every collection at the persistence boundary.</summary>
    public SessionRoutingPolicy Freeze()
    {
        if (Revision < 1) throw new ArgumentOutOfRangeException(nameof(Revision));
        if (SessionSpendLimit is < 0) throw new ArgumentOutOfRangeException(nameof(SessionSpendLimit));
        if (OriginProviderId is null) throw new ArgumentNullException(nameof(OriginProviderId));
        if (OriginProviderId.Length > 0 && string.IsNullOrWhiteSpace(OriginProviderId))
            throw new ArgumentException("Origin provider id must be empty or non-empty text.", nameof(OriginProviderId));
        if (AllowedRoutes.Any(static route => route is null))
            throw new ArgumentException("Allowed routes cannot contain null.", nameof(AllowedRoutes));
        if (AllowedRoutes.Select(static route => route.RouteId.Value).Distinct(StringComparer.Ordinal).Count() != AllowedRoutes.Count)
            throw new ArgumentException("Allowed route ids must be unique.", nameof(AllowedRoutes));
        if (BillingPolicy.Any(static mode => !Enum.IsDefined(mode)))
            throw new ArgumentOutOfRangeException(nameof(BillingPolicy));
        if (BillingPolicy.Distinct().Count() != BillingPolicy.Count)
            throw new ArgumentException("Billing policy modes must be unique.", nameof(BillingPolicy));
        return new SessionRoutingPolicy(Revision, AllowedRoutes, BillingPolicy, CrossProviderRouting,
            SessionSpendLimit, OriginProviderId);
    }

    public bool Allows(ModelRoute route, BillingMode mode)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!Enum.IsDefined(mode) || !BillingPolicy.Contains(mode)) return false;
        if (!CrossProviderRouting && OriginProviderId.Length > 0
            && !StringComparer.Ordinal.Equals(route.ProviderId, OriginProviderId)) return false;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(route.CanonicalJson())));
        return AllowedRoutes.Any(authorized => authorized.RouteId.Equals(route.Id)
            && StringComparer.Ordinal.Equals(authorized.ProviderId, route.ProviderId)
            && StringComparer.Ordinal.Equals(authorized.IdentityHash, hash)
            && authorized.BillingMode == mode);
    }

    /// <summary>Returns a new revision containing only the explicitly granted route binding.</summary>
    public SessionRoutingPolicy Grant(AuthorizedModelRoute routeAuthorization)
    {
        ArgumentNullException.ThrowIfNull(routeAuthorization);
        var policy = Freeze();
        var routes = policy.AllowedRoutes.Where(route => !route.RouteId.Equals(routeAuthorization.RouteId)).ToList();
        routes.Add(routeAuthorization);
        var modes = policy.BillingPolicy.Contains(routeAuthorization.BillingMode)
            ? policy.BillingPolicy.ToList()
            : policy.BillingPolicy.Append(routeAuthorization.BillingMode).ToList();
        var origin = policy.OriginProviderId.Length == 0 ? routeAuthorization.ProviderId : policy.OriginProviderId;
        var crossProvider = policy.CrossProviderRouting
            || !StringComparer.Ordinal.Equals(routeAuthorization.ProviderId, origin);
        return new SessionRoutingPolicy(checked(policy.Revision + 1), routes, modes, crossProvider,
            policy.SessionSpendLimit, origin).Freeze();
    }
}

/// <summary>Initial immutable session routing policy snapshot.</summary>
public sealed record SessionRoutingPolicySet(SessionId SessionId, SessionRoutingPolicy Policy) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.routing_policy_set");
    public int SchemaVersion() => 1;
}

/// <summary>Explicitly authorized revision of a session routing policy.</summary>
public sealed record SessionRoutingPolicyRevised(SessionId SessionId, SessionRoutingPolicy Policy,
    InteractionId InteractionId) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.routing_policy_revised");
    public int SchemaVersion() => 1;
}
