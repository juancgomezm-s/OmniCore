using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

public sealed class FollowUpReplayContractTests
{
    private static (IEventStore Store, IEventCodecRegistry Codecs) Setup()
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        return (store, codecs);
    }

    private static void AppendRaw(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, DomainEventPayload payload)
    {
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        var evt = DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null,
            run, run, null, null, null, null, null, Array.Empty<ArtifactRef>(), json);
        store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
    }

    [Fact]
    public void Pending_RejectsDuplicateQueueIds()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var lane = LaneId.New();
        var followUp = FollowUpId.New();
        var queued = new FollowUpQueued(followUp, run, lane, null, "[\"same\"]", null);

        AppendRaw(store, codecs, session, run, queued);
        AppendRaw(store, codecs, session, run, queued);

        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, lane));
    }

    [Fact]
    public void Pending_RejectsOrphanPromotion()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var lane = LaneId.New();
        var promoted = new FollowUpPromoted(FollowUpId.New(), run, lane, TurnId.New());

        AppendRaw(store, codecs, session, run, promoted);

        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, lane));
    }

    [Fact]
    public void Pending_RejectsDuplicatePromotion()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var lane = LaneId.New();
        var followUp = FollowUpId.New();
        var turn = TurnId.New();

        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUp, run, lane, null, "[\"a\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpPromoted(followUp, run, lane, turn));
        AppendRaw(store, codecs, session, run, new FollowUpPromoted(followUp, run, lane, turn));

        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, lane));
    }

    [Fact]
    public void Pending_RejectsPromotionOnWrongLane()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var laneA = LaneId.New();
        var laneB = LaneId.New();
        var followUp = FollowUpId.New();
        var turn = TurnId.New();

        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUp, run, laneA, null, "[\"a\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpPromoted(followUp, run, laneB, turn));

        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, laneA));
    }

    [Fact]
    public void ValidPromotionRemovesOnlyItsItemAndPreservesOtherQueuesAndJournalOrder()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var laneA = LaneId.New();
        var laneB = LaneId.New();
        var otherRun = RunId.New();
        var followUpA1 = FollowUpId.New();
        var followUpA2 = FollowUpId.New();
        var followUpA3 = FollowUpId.New();
        var followUpB = FollowUpId.New();
        var otherRunFollowUp = FollowUpId.New();
        var turn = TurnId.New();

        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUpA1, run, laneA, null, "[\"a1\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUpA2, run, laneA, null, "[\"a2\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUpA3, run, laneA, null, "[\"a3\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUpB, run, laneB, null, "[\"b\"]", null));
        AppendRaw(store, codecs, session, otherRun, new FollowUpQueued(otherRunFollowUp, otherRun, laneA, null, "[\"other\"]", null));

        var journalBefore = store.ReadFrom(session, 1).Select(e => e.EventId).ToArray();

        AppendRaw(store, codecs, session, run, new FollowUpPromoted(followUpA1, run, laneA, turn));

        var laneAItems = FollowUpQueue.Pending(store, codecs, session, run, laneA);
        var laneBItems = FollowUpQueue.Pending(store, codecs, session, run, laneB);

        Assert.Equal(new[] { followUpA2, followUpA3 }, laneAItems.Select(item => item.Id));
        Assert.Single(laneBItems);
        Assert.Equal(followUpB, laneBItems[0].Id);

        var journalAfter = store.ReadFrom(session, 1).Select(e => e.EventId).ToArray();
        Assert.Equal(journalBefore.Length + 1, journalAfter.Length);
        Assert.Equal(journalBefore, journalAfter.Take(journalBefore.Length));
        Assert.Equal(otherRunFollowUp, Assert.Single(FollowUpQueue.Pending(store, codecs, session, otherRun, laneA)).Id);
        var last = codecs.Decode(store.ReadFrom(session, 1)[^1]);
        Assert.Equal(followUpA1, Assert.IsType<FollowUpPromoted>(last).FollowUpId);
    }

    [Fact]
    public void Pending_RejectsCrossLaneDuplicateQueuedId()
    {
        var (store, codecs) = Setup();
        var session = SessionId.New();
        var run = RunId.New();
        var laneA = LaneId.New();
        var laneB = LaneId.New();
        var followUp = FollowUpId.New();

        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUp, run, laneA, null, "[\"a\"]", null));
        AppendRaw(store, codecs, session, run, new FollowUpQueued(followUp, run, laneB, null, "[\"b\"]", null));

        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, laneA));
        Assert.Throws<InvalidStateTransitionException>(
            () => FollowUpQueue.Pending(store, codecs, session, run, laneB));
    }
}
