using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class RunCreationAtomicFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_act_rejects_run_creation_failure_without_partial_session_or_selection_change(
        bool seedExistingRun)
    {
        var store = new RejectRunCreationStore();
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
        SessionId? previousSession = null;
        RunId? previousRun = null;
        if (seedExistingRun)
        {
            var existing = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, existing.Outcome?.Kind);
            previousSession = server.LastSessionId();
            previousRun = server.LastRunId();
            Assert.NotNull(previousSession);
            Assert.NotNull(previousRun);
        }
        else
        {
            Assert.Null(server.LastSessionId());
            Assert.Null(server.LastRunId());
        }

        var workspace = Path.Combine(Path.GetTempPath(), "omni-act-rejected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            store.RejectNextRunCreation = true;
            var commandId = Ids.NewV7();
            var ack = server.Send(WireEnvelope.Command(commandId,
                "{\"cmd\":\"act\",\"objective\":\"inspect the temporary workspace\",\"workspace\":"
                    + System.Text.Json.JsonSerializer.Serialize(workspace) + "}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(commandId, ack.CommandId);
            Assert.Equal("error", ack.Status);
            Assert.False(string.IsNullOrWhiteSpace(ack.Error));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, ack.Outcome?.Kind);
            Assert.Null(ack.FirstSeq);
            Assert.Null(ack.LastSeq);
            Assert.Equal(1, store.RejectedRunCreationCount);

            var command = new CommandId(Guid.Parse(commandId));
            Assert.DoesNotContain(store.ReadAll(), evt => evt.Causation is CommandCausation cause
                && cause.CommandId == command);

            var newSessions = store.AttemptedSessions.Where(session => session != previousSession).Distinct().ToArray();
            Assert.Single(newSessions);
            Assert.Empty(store.ReadFrom(newSessions[0], 1));
            Assert.Equal(previousSession, server.LastSessionId());
            Assert.Equal(previousRun, server.LastRunId());
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class RejectRunCreationStore : IEventStore, IWorkspaceJournalReader
    {
        private readonly InMemoryEventStore _inner = new();
        private readonly List<SessionId> _attemptedSessions = new();

        public bool RejectNextRunCreation { get; set; }
        public int RejectedRunCreationCount { get; private set; }
        public IReadOnlyList<SessionId> AttemptedSessions => _attemptedSessions.ToArray();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            _attemptedSessions.Add(sessionId);
            if (RejectNextRunCreation && events.Any(evt => evt.Type.ToString() == "run.created"))
            {
                RejectNextRunCreation = false;
                RejectedRunCreationCount++;
                throw new IOException("controlled run creation store failure before append");
            }

            _inner.AppendBatch(sessionId, events, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) => _inner.ReadEvents(type);

        public IReadOnlyList<DomainEvent> ReadAll() => _attemptedSessions.Distinct()
            .SelectMany(session => _inner.ReadFrom(session, 1)).ToArray();
    }
}
