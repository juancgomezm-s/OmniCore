using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>ADR-0004 §5 + ADR-0035 §1: un efecto desconocido sin reconciliar no se pierde al llegar otro Run.</summary>
public sealed class M3OrphanEffectNotLostTests
{
    private static (InMemoryEventStore Store, SessionId Session, RunId Run, ToolCallId Call) CrashedWithUnknownEffect()
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
        stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent, "{}"), DurabilityClass.Barrier);
        stream.Append(new ToolCallEffectUnknown(call, EffectClass.NonIdempotent));
        return (store, session, run, call);
    }

    private sealed class StubReconciler(ReconciliationOutcome outcome) : IFilesystemReconciler
    {
        public FilesystemReconciliation Reconcile(string workspaceRoot, string json, CancellationToken ct) =>
            new(outcome, "stub");
    }

    [Theory]
    [InlineData(ReconciliationOutcome.Applied)]
    [InlineData(ReconciliationOutcome.NotApplied)]
    public void Verifiable_effect_of_cancelled_run_is_reconciled_and_new_run_starts(ReconciliationOutcome outcome)
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        Assert.Throws<UnreconciledEffectException>(() => control.StartRun(session, "otro", RunMode.Act));

        var resume = new RunResumeService(store, codecs, new StubReconciler(outcome), ".");
        Assert.Equal(1, resume.ReconcileTerminalRuns(session));
        Assert.Equal(0, resume.ReconcileTerminalRuns(session)); // idempotente

        Assert.Equal(ToolCallState.Reconciled,
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).ToolCall(call));
        Assert.NotEqual(run, control.StartRun(session, "otro", RunMode.Act));
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }

    [Theory]
    [InlineData(ReconciliationOutcome.Unresolvable)]
    [InlineData(ReconciliationOutcome.Conflict)]
    public void Unverifiable_effect_of_cancelled_run_keeps_blocking_and_is_listed(ReconciliationOutcome outcome)
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        new RunResumeService(store, codecs, new StubReconciler(outcome), ".").ReconcileTerminalRuns(session);

        var ex = Assert.Throws<UnreconciledEffectException>(() => control.StartRun(session, "otro", RunMode.Act));
        Assert.Contains(call, ex.ToolCalls);
        Assert.Contains(call.ToString(), ex.Message);
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }

    [Fact]
    public void New_run_is_refused_while_an_effect_is_unreconciled_even_if_old_run_is_terminal()
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run); // el Run viejo queda terminal, pero el efecto sigue sin reconciliar

        var ex = Assert.Throws<UnreconciledEffectException>(() => control.StartRun(session, "otro", RunMode.Act));
        Assert.Contains(call, ex.ToolCalls);
        Assert.Throws<UnreconciledEffectException>(() => control.SendInput(session, "otro", RunMode.Act));

        Assert.Equal(ToolCallState.EffectUnknown,
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).ToolCall(call));
    }

    [Fact]
    public void New_run_is_allowed_once_the_effect_is_reconciled()
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        new EventStream(store, codecs, session).Append(
            new ToolCallReconciled(call, ReconciliationOutcome.Applied, "x"));
        control.CancelRun(session, run);

        var next = control.StartRun(session, "otro", RunMode.Act);
        Assert.NotEqual(run, next);
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }
}
