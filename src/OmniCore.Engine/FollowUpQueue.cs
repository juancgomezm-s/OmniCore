namespace OmniCore.Engine;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Durable FIFO FollowUp intents. Queueing never changes Run/Lane state; promotion is owned by the next Turn.</summary>
public static class FollowUpQueue
{
    public sealed record Item(FollowUpId Id, RunId RunId, LaneId LaneId, TurnId? SourceTurnId,
        string InputPartsJson, string? Origin);

    public static bool TryQueue(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, LaneId? lane, string text, string? origin)
    {
        if (lane is null || string.IsNullOrWhiteSpace(text)) return false;
        var events = store.ReadFrom(session, 1);
        if (RunProjection.Replay(session, run, codecs, events).IsTerminal()) return false;
        var open = OpenTurn(events, codecs, run, lane);
        var pending = PendingFromEvents(events, codecs, run, lane);
        var sourceTurn = open?.TurnId ?? pending.LastOrDefault()?.SourceTurnId;
        if (open is null && pending.Count == 0 && !HasPendingInteraction(events, codecs, run)) return false;

        var safeText = new RedactionPolicy().Redact(text);
        var parts = "[\"" + JsonEncodedText.Encode(safeText) + "\"]";
        new EventStream(store, codecs, session).Append(new FollowUpQueued(FollowUpId.New(), run,
            lane, sourceTurn, parts, origin), DurabilityClass.Barrier);
        return true;
    }

    public static IReadOnlyList<Item> Pending(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, LaneId lane)
        => PendingFromEvents(store.ReadFrom(session, 1), codecs, run, lane);

    private static IReadOnlyList<Item> PendingFromEvents(IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs, RunId run, LaneId lane)
    {
        var queued = new List<Item>();
        var byId = new Dictionary<FollowUpId, Item>();
        var promoted = new HashSet<FollowUpId>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case FollowUpQueued item when item.RunId.Equals(run):
                    if (byId.ContainsKey(item.FollowUpId))
                        throw new InvalidStateTransitionException("followup", "duplicated or cross-lane input", "followup.queued");
                    var queuedItem = new Item(item.FollowUpId, item.RunId, item.LaneId, item.TurnId,
                        item.InputPartsJson, item.Origin);
                    byId.Add(item.FollowUpId, queuedItem);
                    if (item.LaneId == lane)
                        queued.Add(queuedItem);
                    break;
                case FollowUpPromoted item when item.RunId.Equals(run):
                    if (!byId.TryGetValue(item.FollowUpId, out var queuedForPromotion)
                        || queuedForPromotion.LaneId != item.LaneId || !promoted.Add(item.FollowUpId))
                        throw new InvalidStateTransitionException("followup", "invalid promotion", "followup.promoted");
                    break;
            }
        }
        return queued.Where(item => !promoted.Contains(item.Id)).ToArray();
    }

    public static IReadOnlyList<DomainEventPayload> PromotionEvents(IReadOnlyList<Item> items,
        RunId run, LaneId lane, TurnId targetTurn)
    {
        var result = new List<DomainEventPayload>(items.Count * 2);
        foreach (var item in items)
        {
            if (item.RunId != run || item.LaneId != lane)
                throw new InvalidOperationException("FollowUp fuera del scope Run/Lane destino");
            result.Add(new FollowUpPromoted(item.Id, run, lane, targetTurn));
            result.Add(new UserInputReceived(run, item.InputPartsJson, null, item.Origin));
        }
        return result;
    }

    private static (TurnId TurnId, LaneId LaneId)? OpenTurn(IReadOnlyList<DomainEvent> events,
        IEventCodecRegistry codecs, RunId run, LaneId? lane)
    {
        (TurnId TurnId, LaneId LaneId)? open = null;
        foreach (var evt in events)
        {
            if (evt.RunId != run) continue;
            switch (codecs.Decode(evt))
            {
                case TurnStarted started when lane is null || started.LaneId == lane:
                    open = (started.TurnId, started.LaneId);
                    break;
                case TurnCompleted completed when open?.TurnId == completed.TurnId:
                case TurnAbandoned abandoned when open?.TurnId == abandoned.TurnId:
                case TurnInterrupted interrupted when open?.TurnId == interrupted.TurnId:
                    open = null;
                    break;
            }
        }
        return open;
    }

    private static bool HasPendingInteraction(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunId run)
    {
        var pending = new HashSet<InteractionId>();
        foreach (var evt in events)
        {
            switch (codecs.Decode(evt))
            {
                case InteractionRequested request when evt.RunId == run:
                    pending.Add(request.InteractionId);
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Count > 0;
    }
}
