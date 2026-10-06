using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class InteractionAuditFailureCommandOutcomeTests
{
    [Fact]
    public void Audit_failure_after_human_resolution_keeps_accepted_ack_range_and_retry_is_idempotent()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var originRun = TestRun.Open(store, session, "terminal effect origin");
        var originTurn = TurnId.New();
        var originCall = ToolCallId.New();
        var originStream = new EventStream(store, codecs, session);
        using (ExecutionScope.Begin(new ExecutionScopeState(originRun.RunId, originRun.RootTask,
            originRun.RootLane, originTurn)))
        {
            originStream.Append(new TurnStarted(originTurn, originRun.RootLane));
            originStream.Append(new ToolCallRequested(originCall, "old-provider-call", "test.effect", "{}"));
            originStream.Append(new ToolCallPrepared(originCall, "{}"));
            originStream.Append(new PermissionEvaluated(originCall, PermissionDecision.Allow, "{}", null));
            originStream.Append(new ToolCallAuthorized(originCall));
            originStream.Append(new ToolCallStarted(originCall, EffectClass.Reconcilable, "{}"), DurabilityClass.Barrier);
            originStream.Append(new ToolCallEffectUnknown(originCall, EffectClass.Reconcilable));
        }
        new RunControlService(store, codecs).CancelRun(session, originRun.RunId);
        var originUnknown = Assert.Single(store.ReadFrom(session, 1), evt =>
            evt.RunId == originRun.RunId && codecs.Decode(evt) is ToolCallEffectUnknown unknown
                && unknown.ToolCallId == originCall);

        var waitingRun = TestRun.Open(store, session, "waiting run");
        var root = Path.Combine(Path.GetTempPath(), "omni-interaction-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stateFile = Path.Combine(root, "session.state");
        File.WriteAllText(stateFile, session + "\n" + waitingRun.RunId);
        try
        {
            var audit = new SwitchableAuditSink();
            var server = new OmniServer(store, codecs, audit, stateFile);
            var events = store.ReadFrom(session, 1);
            var requestEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is InteractionRequested request
                    && request.Kind == InteractionKind.ReconciliationConflict);
            var request = Assert.IsType<InteractionRequested>(codecs.Decode(requestEnvelope));
            Assert.Equal(waitingRun.RunId, requestEnvelope.RunId);
            Assert.Equal(originRun.RunId, Assert.Single(events, evt =>
                evt.EventId == originUnknown.EventId).RunId);

            var beforeResponse = store.CurrentSequence(session);
            var commandId = CommandId.New();
            audit.ThrowOnRecord = true;
            var ack = server.Send(WireEnvelope.Command(commandId.ToString(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond",
                interactionId = request.InteractionId.ToString(),
                optionId = "resolution_applied",
            })), CancellationToken.None);

            Assert.Equal(commandId.ToString(), ack.CommandId);
            Assert.Equal("error", ack.Status);
            Assert.False(string.IsNullOrWhiteSpace(ack.Error));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);

            events = store.ReadFrom(session, 1);
            var resolvedEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is InteractionResolved resolved && resolved.InteractionId == request.InteractionId);
            var effectEnvelope = Assert.Single(events, evt =>
                codecs.Decode(evt) is ToolCallReconciled effect
                    && effect.ToolCallId == originCall && effect.Cause == InteractionCause.User);
            var effect = Assert.IsType<ToolCallReconciled>(codecs.Decode(effectEnvelope));
            var causallyOwned = events.Where(evt => evt.Sequence > beforeResponse
                && evt.Causation is CommandCausation cause && cause.CommandId == commandId).ToArray();
            Assert.NotEmpty(causallyOwned);
            Assert.Equal(causallyOwned.Min(evt => evt.Sequence), ack.FirstSeq);
            Assert.Equal(causallyOwned.Max(evt => evt.Sequence), ack.LastSeq);
            Assert.Contains(resolvedEnvelope, causallyOwned);
            Assert.Contains(effectEnvelope, causallyOwned);
            Assert.Equal(waitingRun.RunId, resolvedEnvelope.RunId);
            Assert.Equal(new CommandCausation(commandId), resolvedEnvelope.Causation);
            Assert.Equal(ReconciliationOutcome.Applied, effect.Outcome);
            Assert.Equal(originCall, effect.ToolCallId);
            Assert.Equal(originRun.RunId, effectEnvelope.RunId);
            Assert.Equal(originRun.RunId, effectEnvelope.CorrelationId);
            Assert.Equal(originRun.RootTask, effectEnvelope.TaskId);
            Assert.Equal(originRun.RootLane, effectEnvelope.LaneId);
            Assert.Equal(originTurn, effectEnvelope.TurnId);
            Assert.Equal(new CommandCausation(commandId), effectEnvelope.Causation);

            var afterResponse = store.CurrentSequence(session);
            var retryId = CommandId.New();
            var retry = server.Send(WireEnvelope.Command(retryId.ToString(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond",
                interactionId = request.InteractionId.ToString(),
                optionId = "resolution_applied",
            })), CancellationToken.None);

            Assert.Equal(retryId.ToString(), retry.CommandId);
            Assert.Equal("error", retry.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, retry.Outcome?.Kind);
            Assert.Null(retry.FirstSeq);
            Assert.Null(retry.LastSeq);
            Assert.Equal(afterResponse, store.CurrentSequence(session));
            Assert.Single(store.ReadFrom(session, 1), evt =>
                codecs.Decode(evt) is InteractionResolved resolved && resolved.InteractionId == request.InteractionId);
            Assert.Single(store.ReadFrom(session, 1), evt =>
                codecs.Decode(evt) is ToolCallReconciled effect
                    && effect.ToolCallId == originCall && effect.Cause == InteractionCause.User);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class SwitchableAuditSink : IAuditSink
    {
        public bool ThrowOnRecord { get; set; }

        public void Record(AuditRecord record, CancellationToken cancellationToken)
        {
            if (ThrowOnRecord) throw new IOException("controlled audit failure after durable interaction response");
        }
    }
}
