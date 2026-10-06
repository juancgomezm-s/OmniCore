using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Xunit;

namespace OmniCore.Tests;

/// <summary>Command ranges include descendants caused through durable event causation.</summary>
public sealed class CommandCausalRangeBoundaryTests
{
    [Fact]
    public void Terminal_sim_resume_ack_includes_its_event_caused_reconciliation_request()
    {
        var store = new FailOnceStore();
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        var startId = Ids.NewV7();
        var start = Send(server, startId, "{\"cmd\":\"sim\",\"scenario\":\"with-tool-crash\"}");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, start.Outcome?.Kind);

        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        // Simulate a durable partial resume: EffectUnknown commits; the following reconciliation
        // append fails. Cancellation leaves that already-unknown call for terminal recovery.
        store.SuccessfulWritesBeforeFailure = 1;
        var interruptedResumeId = Ids.NewV7();
        var interruptedResume = Send(server, interruptedResumeId, "{\"cmd\":\"sim.resume\"}");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, interruptedResume.Outcome?.Kind);
        Assert.Equal("error", interruptedResume.Status);
        var call = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
            .OfType<ToolCallEffectUnknown>()).ToolCallId;

        var cancelId = Ids.NewV7();
        var cancel = Send(server, cancelId,
            "{\"cmd\":\"run.cancel\",\"runId\":" + System.Text.Json.JsonSerializer.Serialize(run.ToString()) + "}");
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, cancel.Outcome?.Kind);

        var beforeResume = store.CurrentSequence(session);
        var resumeId = Ids.NewV7();
        var resume = Send(server, resumeId, "{\"cmd\":\"sim.resume\"}");

        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resume.Outcome?.Kind);
        Assert.Equal("ok", resume.Status);
        var newEvents = store.ReadFrom(session, beforeResume + 1);
        var reconciledEnvelope = Assert.Single(newEvents,
            evt => codecs.Decode(evt) is ToolCallReconciled item && item.ToolCallId == call);
        var requestEnvelope = Assert.Single(newEvents,
            evt => codecs.Decode(evt) is InteractionRequested item && item.Kind == InteractionKind.ReconciliationConflict
                && JsonObj.Parse(item.ToolCallJson ?? "{}").GetValueOrDefault("toolCallId") == call.ToString());
        var commandCause = new CommandCausation(new CommandId(Guid.Parse(resumeId)));
        Assert.Equal(commandCause, reconciledEnvelope.Causation);
        Assert.Equal(new EventCausation(reconciledEnvelope.EventId), requestEnvelope.Causation);

        Assert.Equal(reconciledEnvelope.Sequence, resume.FirstSeq);
        Assert.Equal(requestEnvelope.Sequence, resume.LastSeq);
        Assert.True(resume.FirstSeq > beforeResume);
        Assert.DoesNotContain(newEvents, evt => evt.Sequence < resume.FirstSeq || evt.Sequence > resume.LastSeq);
        Assert.DoesNotContain(newEvents, evt => evt.Causation is CommandCausation cause
            && cause.CommandId != new CommandId(Guid.Parse(resumeId)));
    }

    [Fact]
    public void Background_terminal_reconciliation_keeps_origin_event_causation()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var call = ToolCallId.New();
        var stream = new EventStream(store, codecs, session);
        stream.Append(new ToolCallRequested(call, "pc", "filesystem.patch", "{}"));
        stream.Append(new ToolCallPrepared(call, "{}"));
        stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
        stream.Append(new ToolCallAuthorized(call));
        stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent, null), DurabilityClass.Barrier);
        stream.Append(new ToolCallEffectUnknown(call, EffectClass.NonIdempotent));
        new RunControlService(store, codecs).CancelRun(session, run.RunId);
        var origin = store.ReadFrom(session, 1).Single(evt => codecs.Decode(evt) is ToolCallEffectUnknown);

        new RunResumeService(store, codecs, null, "").ReconcileTerminalRuns(session);

        var events = store.ReadFrom(session, 1);
        var reconciledEnvelope = Assert.Single(events,
            evt => codecs.Decode(evt) is ToolCallReconciled item && item.ToolCallId == call);
        Assert.Equal(new EventCausation(origin.EventId), reconciledEnvelope.Causation);
        var requestEnvelope = Assert.Single(events,
            evt => codecs.Decode(evt) is InteractionRequested item && item.Kind == InteractionKind.ReconciliationConflict
                && JsonObj.Parse(item.ToolCallJson ?? "{}").GetValueOrDefault("toolCallId") == call.ToString());
        Assert.Equal(new EventCausation(reconciledEnvelope.EventId), requestEnvelope.Causation);
        Assert.Null(reconciledEnvelope.Causation as CommandCausation);
    }

    private static CommandAck Send(OmniServer server, string messageId, string payload) =>
        server.Send(WireEnvelope.Command(messageId, payload), CancellationToken.None);

    private sealed class FailOnceStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public int? SuccessfulWritesBeforeFailure { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (SuccessfulWritesBeforeFailure == 0)
            {
                SuccessfulWritesBeforeFailure = null;
                throw new IOException("controlled store failure before append");
            }

            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
            if (SuccessfulWritesBeforeFailure is { } remaining)
                SuccessfulWritesBeforeFailure = remaining - 1;
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
