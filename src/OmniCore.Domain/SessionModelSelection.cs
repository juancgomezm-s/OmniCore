namespace OmniCore.Domain;

/// <summary>A user-selected model route binding scoped to one durable Session.</summary>
public sealed record SessionModelSelection
{
    public int Revision { get; }
    public string ModelId { get; }
    public RouteId RouteId { get; }
    public string ProviderId { get; }
    public string RouteIdentityHash { get; }
    public BillingMode BillingMode { get; }

    public SessionModelSelection(int revision, string modelId, RouteId routeId, string providerId,
        string routeIdentityHash, BillingMode billingMode)
    {
        Revision = revision;
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        RouteIdentityHash = routeIdentityHash ?? throw new ArgumentNullException(nameof(routeIdentityHash));
        BillingMode = billingMode;
    }

    public SessionModelSelection Freeze()
    {
        if (Revision < 1 || string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 200
            || string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 120
            || !AuthorizedModelRoute.IsSha256(RouteIdentityHash) || !Enum.IsDefined(BillingMode))
            throw new ArgumentException("Invalid Session model selection.");
        return this;
    }
}

public sealed record SessionModelSelectionSet(SessionId SessionId, SessionModelSelection Selection) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.model_selection_set");
    public int SchemaVersion() => 1;
}

public sealed record SessionModelSelectionRevised(SessionId SessionId, SessionModelSelection Selection,
    int ExpectedRevision, InteractionId? InteractionId = null) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.model_selection_revised");
    public int SchemaVersion() => 1;
}
