using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CommandAdmissionBoundaryRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Act_with_invalid_workspace_syntax_is_rejected_without_write_or_selection_change(bool seedPriorRun)
    {
        var store = new RecordingStore();
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
        SessionId? priorSession = null;
        RunId? priorRun = null;
        if (seedPriorRun)
        {
            var prior = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, prior.Outcome?.Kind);
            priorSession = server.LastSessionId();
            priorRun = server.LastRunId();
            Assert.NotNull(priorSession);
            Assert.NotNull(priorRun);
        }
        var before = store.ReadAll();

        var commandId = Ids.NewV7();
        var ack = server.Send(WireEnvelope.Command(commandId, "{" + JsonObj.Field("cmd", "act") + ","
            + JsonObj.Field("objective", "invalid workspace must not start") + ","
            + JsonObj.Field("workspace", "\0") + "}"), TestContext.Current.CancellationToken);

        Assert.Equal(commandId, ack.CommandId);
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
        Assert.Null(ack.FirstSeq);
        Assert.Null(ack.LastSeq);
        Assert.Equal(before, store.ReadAll());
        Assert.Equal(priorSession, server.LastSessionId());
        Assert.Equal(priorRun, server.LastRunId());
    }

    [Fact]
    public void Session_input_partial_new_run_ack_invalidates_prior_context_state_cache()
    {
        var store = new RejectAppendTypeStore();
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
        var prior = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"),
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, prior.Outcome?.Kind);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var priorRun = Assert.IsType<RunId>(server.LastRunId());
        var beforeEvents = store.ReadFrom(session, 1);
        Assert.Equal(RunState.Completed,
            RunProjection.Replay(session, priorRun, EventCodecs.Create(), beforeEvents).State);
        using var previousState = JsonDocument.Parse(server.Query("state", CancellationToken.None)!.Json);
        var previousRunState = previousState.RootElement.GetProperty("runState").GetString();
        Assert.NotEqual("none", previousRunState);

        var sequenceBefore = store.CurrentSequence(session);
        store.RejectNextType = new UserInputReceived(priorRun, "[]", null).Type().Value();
        var commandId = Ids.NewV7();
        var ack = server.Send(WireEnvelope.Command(commandId, "{" + JsonObj.Field("cmd", "session.input") + ","
            + JsonObj.Field("text", "durable objective for the next run") + "}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(commandId, ack.CommandId);
        Assert.Null(store.RejectNextType); // prove the injection was consumed by the real event
        Assert.Equal("error", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.True(ack.FirstSeq.HasValue);
        Assert.True(ack.FirstSeq.Value > sequenceBefore);
        var command = new CommandId(Guid.Parse(commandId));
        var persisted = store.ReadFrom(session, ack.FirstSeq!.Value)
            .Where(evt => evt.Causation is CommandCausation cause && cause.CommandId == command)
            .ToArray();
        Assert.NotEmpty(persisted);
        Assert.Equal(persisted.Min(evt => evt.Sequence), ack.FirstSeq);
        Assert.Equal(persisted.Max(evt => evt.Sequence), ack.LastSeq);
        var created = Assert.Single(persisted, evt => EventCodecs.Create().Decode(evt) is RunCreated);
        var createdRun = Assert.IsType<RunCreated>(EventCodecs.Create().Decode(created)).RunId;
        Assert.NotEqual(priorRun, createdRun);
        Assert.Equal(createdRun, server.LastRunId());
        Assert.DoesNotContain(store.ReadFrom(session, 1), evt => evt.RunId == createdRun
            && EventCodecs.Create().Decode(evt) is UserInputReceived);

        using var currentState = JsonDocument.Parse(server.Query("state", CancellationToken.None)!.Json);
        Assert.Equal("none", currentState.RootElement.GetProperty("runState").GetString());
    }

    private sealed class RecordingStore : IEventStore, IWorkspaceJournalReader
    {
        private readonly InMemoryEventStore _inner = new();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => _inner.Append(sessionId, evt, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken) =>
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => _inner.ReadEvents(type);
        public IReadOnlyList<DomainEvent> ReadAll() => _inner.ReadEvents(EventType.Of("session.created"))
            .SelectMany(created => _inner.ReadFrom(created.SessionId, 1)).OrderBy(evt => evt.SessionId.ToString(),
                StringComparer.Ordinal).ThenBy(evt => evt.Sequence).ToArray();
    }

    private sealed class RejectAppendTypeStore : IEventStore, IWorkspaceJournalReader
    {
        private readonly InMemoryEventStore _inner = new();

        public string? RejectNextType { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (RejectNextType is { } rejectedType && events.Any(evt => evt.Type.ToString() == rejectedType))
            {
                RejectNextType = null;
                throw new IOException("controlled append failure before event persistence");
            }

            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => _inner.ReadEvents(type);
    }
}
