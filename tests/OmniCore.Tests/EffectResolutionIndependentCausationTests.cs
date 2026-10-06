using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class EffectResolutionIndependentCausationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_run_resume_attributes_unknown_and_resolution_to_their_original_events_and_scope(bool unknownAlreadyPersisted)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "active effect recovery");
        var turn = TurnId.New();
        var call = ToolCallId.New();
        var stream = new EventStream(store, codecs, session);
        DomainEvent startedEnvelope;
        using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
        {
            stream.Append(new TurnStarted(turn, run.RootLane));
            stream.Append(new ToolCallRequested(call, "resume-provider", "test.effect", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(call));
            stream.Append(new ToolCallStarted(call, EffectClass.Reconcilable, "{\"path\":\"src/resume.txt\"}"),
                DurabilityClass.Barrier);
            startedEnvelope = Assert.Single(store.ReadFrom(session, 1), evt =>
                codecs.Decode(evt) is ToolCallStarted started && started.ToolCallId == call);
            if (unknownAlreadyPersisted)
                using (CausationScope.Begin(new EventCausation(startedEnvelope.EventId)))
                    stream.Append(new ToolCallEffectUnknown(call, EffectClass.Reconcilable));
        }

        var resume = new RunResumeService(store, codecs, new UnresolvableReconciler(), ".");
        Assert.Equal(1, resume.Resume(session, run.RunId));

        var events = store.ReadFrom(session, 1);
        var unknownEnvelope = Assert.Single(events, evt =>
            codecs.Decode(evt) is ToolCallEffectUnknown unknown && unknown.ToolCallId == call);
        var resolutionEnvelope = Assert.Single(events, evt =>
            codecs.Decode(evt) is ToolCallReconciled reconciled && reconciled.ToolCallId == call);
        Assert.Equal(new EventCausation(startedEnvelope.EventId), unknownEnvelope.Causation);
        Assert.Equal(new EventCausation(unknownEnvelope.EventId), resolutionEnvelope.Causation);
        foreach (var evt in new[] { unknownEnvelope, resolutionEnvelope })
        {
            Assert.Equal(run.RunId, evt.RunId);
            Assert.Equal(run.RootTask, evt.TaskId);
            Assert.Equal(run.RootLane, evt.LaneId);
            Assert.Equal(turn, evt.TurnId);
            Assert.Equal(call, evt.ToolCallId);
        }
        CanonicalStateTracker.Replay(codecs, events);
        var before = store.CurrentSequence(session);
        Assert.Equal(0, resume.Resume(session, run.RunId));
        Assert.Equal(before, store.CurrentSequence(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflict_requests_are_caused_by_their_own_reconciliation_and_publish_idempotently(bool sqlite)
    {
        using var fixture = new StoreFixture(sqlite);
        var store = fixture.Store;
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var originRun = TestRun.Open(store, session, "terminal effects source");
        var stream = new EventStream(store, codecs, session);
        var sourceCalls = new ToolCallId[] { ToolCallId.New(), ToolCallId.New() };
        var turn = TurnId.New();

        using (ExecutionScope.Begin(new ExecutionScopeState(originRun.RunId, originRun.RootTask,
            originRun.RootLane, turn)))
        {
            stream.Append(new TurnStarted(turn, originRun.RootLane));
            foreach (var call in sourceCalls)
            {
                stream.Append(new ToolCallRequested(call, "effect-provider", "test.effect", "{}"));
                stream.Append(new ToolCallPrepared(call, "{}"));
                stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
                stream.Append(new ToolCallAuthorized(call));
                stream.Append(new ToolCallStarted(call, EffectClass.Reconcilable,
                    "{\"path\":\"src/independent-" + call + ".txt\"}"), DurabilityClass.Barrier);
                stream.Append(new ToolCallEffectUnknown(call, EffectClass.Reconcilable));
            }
        }

        new RunControlService(store, codecs).CancelRun(session, originRun.RunId);

        // TestRun intentionally materializes a second, active Run after the orphan effects exist;
        // recovery should publish interaction ownership to this waiter, not steal effect scope.
        var waitingRun = TestRun.Open(store, session, "waiting on old effect reconciliation");
        var resume = new RunResumeService(store, codecs, new UnresolvableReconciler(), ".");
        Assert.Equal(sourceCalls.Length, resume.ReconcileTerminalRuns(session));

        var events = store.ReadFrom(session, 1);
        var reconciliations = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(pair => pair.Payload is ToolCallReconciled reconciliation
                && reconciliation.Cause != InteractionCause.User
                && sourceCalls.Contains(reconciliation.ToolCallId))
            .ToDictionary(pair => ((ToolCallReconciled)pair.Payload).ToolCallId, pair => pair.Event);
        Assert.Equal(sourceCalls.Length, reconciliations.Count);

        var requests = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(pair => pair.Payload is InteractionRequested request
                && request.Kind == InteractionKind.ReconciliationConflict)
            .ToArray();
        Assert.Equal(sourceCalls.Length, requests.Length);
        foreach (var (requestEnvelope, payload) in requests)
        {
            var request = Assert.IsType<InteractionRequested>(payload);
            var call = ReadToolCall(request.ToolCallJson);
            Assert.Contains(call, sourceCalls);
            Assert.Equal(waitingRun.RunId, requestEnvelope.RunId);
            Assert.NotEqual(originRun.RunId, requestEnvelope.RunId);
            Assert.Equal(new EventCausation(reconciliations[call].EventId), requestEnvelope.Causation);
        }

        var awaiting = Assert.Single(events, evt => codecs.Decode(evt) is RunAwaitingInput input
            && input.RunId == waitingRun.RunId);
        Assert.Equal(waitingRun.RunId, awaiting.RunId);

        var sequenceBeforeReplay = store.CurrentSequence(session);
        Assert.Equal(0, resume.ReconcileTerminalRuns(session));
        Assert.Equal(sequenceBeforeReplay, store.CurrentSequence(session));
        Assert.Equal(sourceCalls.Length, store.ReadFrom(session, 1).Count(evt =>
            codecs.Decode(evt) is InteractionRequested request
                && request.Kind == InteractionKind.ReconciliationConflict));
        CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
    }

    private static ToolCallId ReadToolCall(string? json)
    {
        using var document = JsonDocument.Parse(json!);
        return ToolCallId.Parse(document.RootElement.GetProperty("toolCallId").GetString()!);
    }

    private sealed class UnresolvableReconciler : IFilesystemReconciler
    {
        public FilesystemReconciliation Reconcile(string workspaceRoot, string json, CancellationToken ct) =>
            new(ReconciliationOutcome.Unresolvable, "controlled independent conflict");
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _journal;
        private readonly SqliteEventStore? _sqlite;
        private readonly SqliteConnection? _connection;
        public IEventStore Store { get; }

        public StoreFixture(bool sqlite)
        {
            _root = Path.Combine(Path.GetTempPath(), "omni-independent-causation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.db");
            if (sqlite)
            {
                _sqlite = new SqliteEventStore(_journal);
                _connection = (SqliteConnection)_sqlite.Connection;
                Store = _sqlite;
            }
            else
            {
                Store = new InMemoryEventStore();
            }
        }

        public void Dispose()
        {
            if (_sqlite is not null)
            {
                _sqlite.Close();
                SqliteConnection.ClearPool(_connection!);
                _connection!.Dispose();
            }
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
