namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Durable evidence that a pending ContextLimit escalation received exact route consent.</summary>
public sealed record GrantedPendingModelEscalation(
    ModelEscalationRequested Request,
    long RequestSequence,
    InteractionId InteractionId);

/// <summary>Read-only projection for resuming an explicitly consented model escalation.</summary>
public static class ModelEscalationConsentReplay
{
    public static GrantedPendingModelEscalation? FindGrantedPending(IEnumerable<DomainEvent> events,
        IEventCodecRegistry codecs, SessionId session, RunId run, string targetModelId,
        ModelRoute currentRoute, BillingMode billingMode)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(targetModelId);
        ArgumentNullException.ThrowIfNull(currentRoute);
        if (!StringComparer.Ordinal.Equals(currentRoute.ProviderModelName, targetModelId)) return null;

        var sessionEvents = events.Where(evt => evt.SessionId == session)
            .OrderBy(evt => evt.Sequence).ToArray();
        var escalation = sessionEvents
            .Where(evt => evt.RunId == run)
            .Select(evt => (Event: evt, Payload: DecodeAs<ModelEscalationRequested>(codecs, evt)))
            .Where(pair => pair.Payload is { } payload && payload.RunId == run)
            .LastOrDefault();

        // The newest escalation request for this Run owns the attempt. An older grant cannot
        // authorize a later request, including a request to the same model.
        if (escalation.Event is null || escalation.Payload is not { } requested
            || requested.Cause != EscalationCause.ContextLimit
            || !StringComparer.Ordinal.Equals(requested.ToModel, targetModelId)) return null;

        if (sessionEvents.Any(evt => evt.RunId == run && evt.Sequence > escalation.Event.Sequence
            && DecodeAs<ModelEscalationApproved>(codecs, evt) is { } approved
            && approved.RunId == run && StringComparer.Ordinal.Equals(approved.ToModel, targetModelId))) return null;
        if (sessionEvents.Any(evt => evt.RunId == run && evt.Sequence > escalation.Event.Sequence
            && DecodeAs<ModelEscalationCompleted>(codecs, evt) is { } completed
            && completed.RunId == run && StringComparer.Ordinal.Equals(completed.ToModel, targetModelId))) return null;

        var expectedBinding = AuthorizedModelRoute.From(currentRoute, billingMode);
        foreach (var requestEvent in sessionEvents.Where(evt => evt.RunId == run
                     && evt.Sequence > escalation.Event.Sequence))
        {
            if (DecodeAs<InteractionRequested>(codecs, requestEvent) is not { Kind: InteractionKind.ModelRouteConsent } request
                || SessionRoutingAuthorization.Parse(request) is not { } offer
                || !offer.Route.Equals(expectedBinding)) continue;

            var resolutionEvent = sessionEvents.FirstOrDefault(evt => evt.Sequence > requestEvent.Sequence
                && evt.RunId == run && DecodeAs<InteractionResolved>(codecs, evt) is { } resolution
                && resolution.InteractionId == request.InteractionId
                && resolution.Cause == InteractionCause.User
                && resolution.OptionId == "allow_route");
            if (resolutionEvent is null) continue;

            var revisedEvent = sessionEvents.FirstOrDefault(evt => evt.Sequence > resolutionEvent.Sequence
                && evt.RunId == run && DecodeAs<SessionRoutingPolicyRevised>(codecs, evt) is { } revised
                && revised.SessionId == session && revised.InteractionId == request.InteractionId);
            if (revisedEvent is null) continue;

            if (sessionEvents.Any(evt => evt.Sequence > requestEvent.Sequence
                && DecodeAs<InteractionExpired>(codecs, evt) is { } expired
                && expired.InteractionId == request.InteractionId)) continue;

            SessionRoutingPolicy? policy;
            try { policy = SessionRoutingAuthorization.Read(sessionEvents, codecs, session); }
            catch (InvalidDataException) { return null; }
            if (policy?.Allows(currentRoute, billingMode) == true)
                return new GrantedPendingModelEscalation(requested, escalation.Event.Sequence, request.InteractionId);
        }

        return null;
    }

    private static T? DecodeAs<T>(IEventCodecRegistry codecs, DomainEvent evt) where T : class, DomainEventPayload =>
        codecs.Decode(evt) as T;
}
