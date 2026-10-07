using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CanonicalReasoningRequestValidationTests
{
    [Theory]
    [InlineData("budget", null)]
    [InlineData("budget", 1023)]
    [InlineData("high", 1024)]
    public void Invalid_reasoning_request_is_rejected_before_append_and_during_raw_replay(
        string kind, int? budgetTokens)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(new EventStream(store, codecs, session), session, mode: RunMode.Plan);
        var preference = new RunReasoningPreferenceSelected(run.RunId, 1,
            new ReasoningRequest(kind, budgetTokens), "User", Ids.NewV7().ToString());
        var sequenceBefore = store.CurrentSequence(session);

        Assert.Throws<InvalidStateTransitionException>(() =>
            new EventStream(store, codecs, session).Append(preference, DurabilityClass.Barrier));
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));

        // Simulate a malformed row already present in a legacy/corrupt raw journal. Replay must
        // apply the same canonical validator as append; it cannot bless data the writer rejects.
        var payloadJson = codecs.CodecFor(preference.Type()).Encode(preference);
        var raw = DomainEvent.Stored(EventId.New(), session, sequenceBefore + 1, preference.Type(),
            preference.SchemaVersion(), DateTimeOffset.UtcNow, null, run.RunId, run.RunId,
            run.RootTask, run.RootLane, null, null, null, Array.Empty<ArtifactRef>(), payloadJson);
        var journal = store.ReadFrom(session, 1).Append(raw).ToArray();

        Assert.Throws<InvalidStateTransitionException>(() => CanonicalStateTracker.Replay(codecs, journal));
        Assert.Throws<InvalidStateTransitionException>(() =>
            RunProjection.Replay(session, run.RunId, codecs, journal));
    }
}
