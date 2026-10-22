using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;

namespace OmniCore.Host;

/// <summary>Retires pre-fix monetary prompts in sessions authorized only for non-monetary routes.</summary>
internal static class ObsoleteMonetaryBudgetRecovery
{
    internal static int Reconcile(IEventStore store, IEventCodecRegistry codecs, SessionId session)
    {
        var events = store.ReadFrom(session, 1);
        var policy = SessionRoutingAuthorization.Read(events, codecs, session);
        // Missing/unknown/metered authorization is not evidence that spending is free.
        if (policy is null || policy.AllowedRoutes.Count == 0
            || policy.AllowedRoutes.Any(route => route.BillingMode is not (BillingMode.Local or BillingMode.IncludedQuota)))
            return 0;

        var pending = new Dictionary<InteractionId, (DomainEvent Event, InteractionRequested Request)>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case InteractionRequested request:
                    pending[request.InteractionId] = (evt, request);
                    break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        var stream = new EventStream(store, codecs, session);
        var count = 0;
        foreach (var (evt, request) in pending.Values)
        {
            if (evt.RunId is not { } run || !IsMonetary(request)
                || RunProjection.Replay(session, run, codecs, events).IsTerminal()) continue;
            // Expiration is not a fabricated user consent or denial. Keep the original
            // request, input, queued FollowUps and Run intact, with its original attribution.
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, evt.TaskId,
                evt.LaneId, evt.TurnId, ExecutionId: evt.ExecutionId));
            stream.Append(new InteractionExpired(request.InteractionId), DurabilityClass.Barrier);
            count++;
        }
        return count;
    }

    private static bool IsMonetary(InteractionRequested request)
    {
        if (request.Kind != InteractionKind.BudgetExceeded) return false;
        if (BudgetContinuation.Offer(request) is not null) return true;
        try
        {
            using var json = JsonDocument.Parse(request.SubjectJson);
            if (!json.RootElement.TryGetProperty("detail", out var field)
                || field.ValueKind != JsonValueKind.String) return false;
            return field.GetString() is
                "uso histórico incompleto: no se puede hacer cumplir el tope" or
                "precio desconocido: no se puede hacer cumplir el tope" or
                "uso del paso incompleto: no se puede hacer cumplir el tope" or
                "uso del paso inválido: no se puede hacer cumplir el tope" or
                "suma monetaria no representable: no se puede hacer cumplir el tope" or
                "cota monetaria de invocación desconocida" or
                "uso histórico incompleto para reserva" or
                "uso histórico del Task incompleto para reserva";
        }
        catch (JsonException) { return false; }
    }
}
