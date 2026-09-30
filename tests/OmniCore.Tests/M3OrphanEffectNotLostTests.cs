using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>ADR-0004 §5 + ADR-0035 §1: un efecto desconocido sin reconciliar no se pierde al llegar otro Run.</summary>
public sealed class M3OrphanEffectNotLostTests
{
    private static (InMemoryEventStore Store, SessionId Session, RunId Run, ToolCallId Call) CrashedWithUnknownEffect(
        string targetPath = "src/file.txt")
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
        stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent,
            "{\"path\":\"" + targetPath + "\",\"expectedPreHash\":\"pre\",\"expectedPostHash\":\"post\"}"),
            DurabilityClass.Barrier);
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
    public void Unresolvable_recovery_publishes_server_options_and_human_applied_unblocks_new_run()
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        var resume = new RunResumeService(store, codecs, new StubReconciler(ReconciliationOutcome.Unresolvable), ".");

        Assert.Equal(1, resume.ReconcileTerminalRuns(session));
        var request = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionRequested>().Single();
        Assert.Equal(InteractionKind.ReconciliationConflict, request.Kind);
        Assert.Contains("resolution_applied", request.OptionsJson);
        Assert.Contains("resolution_not_applied", request.OptionsJson);
        Assert.DoesNotContain("resolution_keep_current", request.OptionsJson);
        Assert.Contains("t", request.SubjectJson);
        Assert.Contains("src/file.txt", request.SubjectJson);
        Assert.Contains("stub", request.SubjectJson);

        control.Respond(session, request.InteractionId, "resolution_applied");
        var humanEvent = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<ToolCallReconciled>().Last();
        Assert.Equal(ReconciliationOutcome.Applied, humanEvent.Outcome);
        Assert.Equal(InteractionCause.User, humanEvent.Cause);
        Assert.Equal(ToolCallState.Reconciled, CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).ToolCall(call));
        Assert.NotEqual(run, control.StartRun(session, "nuevo", RunMode.Act));
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }

    [Fact]
    public void Protected_reconciliation_target_and_detail_are_redacted_in_human_interaction()
    {
        var (store, session, run, _) = CrashedWithUnknownEffect(".env");
        var codecs = EventCodecs.Create();
        new RunControlService(store, codecs).CancelRun(session, run);
        new RunResumeService(store, codecs, new StubReconciler(ReconciliationOutcome.Unresolvable), ".")
            .ReconcileTerminalRuns(session);
        var request = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionRequested>().Single();

        Assert.Contains("[recurso protegido]", request.SubjectJson);
        Assert.DoesNotContain(".env", request.SubjectJson);
        Assert.DoesNotContain("stub", request.SubjectJson);
    }

    [Fact]
    public void Conflict_offers_keep_current_and_resolution_unblocks_without_erasing_conflict()
    {
        var (store, session, run, call) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        new RunResumeService(store, codecs, new StubReconciler(ReconciliationOutcome.Conflict), ".")
            .ReconcileTerminalRuns(session);
        var request = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionRequested>().Single();
        Assert.Contains("resolution_keep_current", request.OptionsJson);

        control.Respond(session, request.InteractionId, "resolution_keep_current");
        var reconciled = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<ToolCallReconciled>().Last();
        Assert.Equal(ReconciliationOutcome.Conflict, reconciled.Outcome);
        Assert.Equal(InteractionCause.User, reconciled.Cause);
        Assert.Empty(control.UnreconciledEffects(session));
        Assert.NotEqual(run, control.StartRun(session, "nuevo", RunMode.Act));
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }

    [Fact]
    public void Invalid_and_duplicate_resolution_answers_are_rejected()
    {
        var (store, session, run, _) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        new RunResumeService(store, codecs, new StubReconciler(ReconciliationOutcome.Unresolvable), ".")
            .ReconcileTerminalRuns(session);
        var request = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionRequested>().Single();

        Assert.Throws<InvalidInteractionOptionException>(() => control.Respond(session, request.InteractionId, "resolution_keep_current"));
        control.Respond(session, request.InteractionId, "resolution_not_applied");
        Assert.Throws<InteractionNotPendingException>(() => control.Respond(session, request.InteractionId, "resolution_applied"));
    }

    [Fact]
    public void Human_resolution_is_audited_and_noninteractive_session_stays_blocked_with_guidance()
    {
        var (store, session, run, _) = CrashedWithUnknownEffect();
        var codecs = EventCodecs.Create();
        var control = new RunControlService(store, codecs);
        control.CancelRun(session, run);
        new RunResumeService(store, codecs, new StubReconciler(ReconciliationOutcome.Unresolvable), ".")
            .ReconcileTerminalRuns(session);
        var request = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionRequested>().Single();
        var blocked = Assert.Throws<UnreconciledEffectException>(() => control.StartRun(session, "nuevo", RunMode.Act));
        Assert.Contains(request.InteractionId, blocked.Interactions);
        Assert.Contains("sin resolución humana", blocked.Message);
        Assert.Empty(store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
            .OfType<InteractionResolved>());

        var audit = new InMemoryAuditSink();
        var stateFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".state");
        File.WriteAllText(stateFile, session + "\n" + run);
        try
        {
            var server = new OmniServer(store, codecs, audit, stateFile);
            var noTty = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"nuevo\"}"), CancellationToken.None);
            Assert.Equal("error", noTty.Status);
            Assert.Contains("effects.unresolved(interactionId=" + request.InteractionId, noTty.Error);
            Assert.Empty(store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<InteractionResolved>());
            Assert.Equal("ok", server.RespondToInteraction(request.InteractionId, "resolution_applied").Status);
            var record = Assert.Single(audit.Records(), item => item.EventName == "effect.human_resolution");
            Assert.Equal(request.InteractionId.ToString(), record.Details["interactionId"]);
        }
        finally
        {
            File.Delete(stateFile);
        }
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
