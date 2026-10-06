using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class TerminalRunReconciliationAttributionTests
{
    [Fact]
    public void Reconciliation_events_retain_their_origin_run_entities_and_unknown_event_causation()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var codecs = EventCodecs.Create();
        var first = CancelledRunWithUnknownEffect(store, codecs, session, "first");
        var second = CancelledRunWithUnknownEffect(store, codecs, session, "second");

        var reconciler = new AppliedReconciler();
        var resume = new RunResumeService(store, codecs, reconciler, ".");
        Assert.Equal(2, resume.ReconcileTerminalRuns(session));
        Assert.False(resume.HasPendingSideEffects(session));
        Assert.Equal(0, resume.ReconcileTerminalRuns(session));
        Assert.Equal(2, reconciler.Calls);

        var events = store.ReadFrom(session, 1);
        AssertReconciliation(events, codecs, first);
        AssertReconciliation(events, codecs, second);
    }

    private static RunFixture CancelledRunWithUnknownEffect(IEventStore store, IEventCodecRegistry codecs,
        SessionId session, string name)
    {
        var run = TestRun.Open(store, session, name);
        var turn = TurnId.New();
        var toolCall = ToolCallId.New();
        var stream = new EventStream(store, codecs, session);
        using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
        {
            stream.Append(new TurnStarted(turn, run.RootLane));
            stream.Append(new ToolCallRequested(toolCall, "provider-call-" + name, "test.effect", "{}"));
            stream.Append(new ToolCallPrepared(toolCall, "{}"));
            stream.Append(new PermissionEvaluated(toolCall, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(toolCall));
            stream.Append(new ToolCallStarted(toolCall, EffectClass.Reconcilable, "{}"), DurabilityClass.Barrier);
            stream.Append(new ToolCallEffectUnknown(toolCall, EffectClass.Reconcilable));
        }

        new RunControlService(store, codecs).CancelRun(session, run.RunId);
        var origin = store.ReadFrom(session, 1).Last(evt =>
            evt.RunId == run.RunId && codecs.Decode(evt) is ToolCallEffectUnknown unknown
                && unknown.ToolCallId == toolCall);
        return new RunFixture(run.RunId, run.RootTask, run.RootLane, turn, toolCall, origin.EventId);
    }

    private static void AssertReconciliation(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
        RunFixture expected)
    {
        var resultEnvelope = Assert.Single(events,
            evt => codecs.Decode(evt) is ToolCallReconciled reconciled && reconciled.ToolCallId == expected.ToolCall);
        var result = Assert.IsType<ToolCallReconciled>(codecs.Decode(resultEnvelope));

        Assert.Equal(ReconciliationOutcome.Applied, result.Outcome);
        Assert.Equal(expected.Run, resultEnvelope.RunId);
        Assert.Equal(expected.Run, resultEnvelope.CorrelationId);
        Assert.Equal(expected.Task, resultEnvelope.TaskId);
        Assert.Equal(expected.Lane, resultEnvelope.LaneId);
        Assert.Equal(expected.Turn, resultEnvelope.TurnId);
        Assert.Equal(expected.ToolCall, resultEnvelope.ToolCallId);
        Assert.Equal(new EventCausation(expected.OriginEvent), resultEnvelope.Causation);
    }

    private sealed record RunFixture(RunId Run, TaskId Task, LaneId Lane, TurnId Turn,
        ToolCallId ToolCall, EventId OriginEvent);

    private sealed class AppliedReconciler : IFilesystemReconciler
    {
        public int Calls { get; private set; }
        public FilesystemReconciliation Reconcile(string workspaceRoot, string reconciliationJson,
            CancellationToken cancellationToken)
        {
            Calls++;
            return FilesystemReconciliation.Applied("verified by fixture");
        }
    }
}
