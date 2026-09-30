using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>ADR-0004 §3: una lectura interrumpida (EffectClass.None) no bloquea la recuperación.</summary>
public sealed class M3InterruptedReadRecoveryTests
{
    private static (InMemoryEventStore Store, SessionId Session, RunId Run, ToolCallId Call) Crash(EffectClass cls)
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session).RunId;
        var call = ToolCallId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        stream.Append(new ToolCallRequested(call, "pc-1", "t", "{}"));
        stream.Append(new ToolCallPrepared(call, "{}"));
        stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
        stream.Append(new ToolCallAuthorized(call));
        stream.Append(new ToolCallStarted(call, cls, null), DurabilityClass.Barrier);
        return (store, session, run, call);
    }

    private static (List<DomainEventPayload> Payloads, CanonicalStateTracker Tracker) Resume(
        InMemoryEventStore store, SessionId session, RunId run)
    {
        var codecs = EventCodecs.Create();
        new RunResumeService(store, codecs, null, ".").Resume(session, run);
        var journal = store.ReadFrom(session, 1);
        var payloads = journal.Select(e => codecs.Decode(e)).ToList();
        return (payloads, CanonicalStateTracker.Replay(codecs, journal));
    }

    [Fact]
    public void Interrupted_read_is_failed_without_effect_unknown()
    {
        var (store, session, run, call) = Crash(EffectClass.None);
        var (payloads, tracker) = Resume(store, session, run);
        Assert.DoesNotContain(payloads, p => p is ToolCallEffectUnknown);
        Assert.Contains(payloads, p => p is ToolCallFailed f && f.EffectOutcome == EffectOutcome.None);
        Assert.Equal(ToolCallState.Failed, tracker.ToolCall(call));
    }

    [Fact]
    public void Interrupted_read_resume_is_idempotent()
    {
        var (store, session, run, _) = Crash(EffectClass.None);
        Resume(store, session, run);
        var count = store.ReadFrom(session, 1).Count;
        Resume(store, session, run);
        Assert.Equal(count, store.ReadFrom(session, 1).Count);
    }

    [Theory]
    [InlineData(EffectClass.Reconcilable)]
    [InlineData(EffectClass.NonIdempotent)]
    public void Interrupted_side_effect_stays_effect_unknown(EffectClass cls)
    {
        var (store, session, run, call) = Crash(cls);
        var (payloads, tracker) = Resume(store, session, run);
        Assert.Contains(payloads, p => p is ToolCallEffectUnknown);
        Assert.DoesNotContain(payloads, p => p is ToolCallFailed);
        Assert.Equal(ToolCallState.Reconciled, tracker.ToolCall(call));
    }
}
