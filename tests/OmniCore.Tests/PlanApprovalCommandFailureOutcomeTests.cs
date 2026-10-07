using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Injected store faults and scripted plan.propose; no provider queries.</summary>
public sealed class PlanApprovalCommandFailureOutcomeTests
{
    [Fact]
    public void Precommit_failure_is_rejected_and_retry_publishes_once_then_noops_same_interaction()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        var original = new IOException("controlled precommit batch failure");
        setup.Store.FailNextBatchBeforeCommit = original;

        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) failed;
        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) published;
        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) retry;
        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            failed = setup.Server.RequestPlanApprovalCommand();
            Assert.Same(original, failed.Failure);
            Assert.Null(failed.InteractionId);
            AssertErrorAck(failed.Ack);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, failed.Ack.Outcome?.Kind);
            Assert.Null(failed.Ack.FirstSeq);
            Assert.Null(failed.Ack.LastSeq);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
            Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));

            published = setup.Server.RequestPlanApprovalCommand();
            Assert.NotNull(published.InteractionId);
            Assert.Null(published.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, published.Ack.Outcome?.Kind);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            retry = setup.Server.RequestPlanApprovalCommand();
            Assert.Equal(published.InteractionId, retry.InteractionId);
            Assert.Null(retry.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.NoOp, retry.Ack.Outcome?.Kind);
            Assert.Null(retry.Ack.FirstSeq);
            Assert.Null(retry.Ack.LastSeq);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
        var events = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Equal(2, events.Count);
        Assert.Equal(events.Min(evt => evt.Sequence), published.Ack.FirstSeq);
        Assert.Equal(events.Max(evt => evt.Sequence), published.Ack.LastSeq);
        Assert.All(events, evt => Assert.Equal(Guid.Parse(published.Ack.CommandId),
            Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value));
        var request = Assert.Single(events, evt => evt.Type.ToString() == "interaction.requested");
        Assert.Equal(published.InteractionId, Assert.IsType<InteractionRequested>(setup.Codecs.Decode(request)).InteractionId);
        Assert.Single(events, evt => evt.Type.ToString() == "run.awaiting_input");
        Assert.Equal(before + 2, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Postcommit_exception_returns_durable_interaction_id_and_exact_range_without_duplicate_on_retry()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        var original = new IOException("controlled exception after committed batch");
        setup.Store.ThrowAfterNextBatchCommit = original;

        var failed = setup.Server.RequestPlanApprovalCommand();

        Assert.Same(original, failed.Failure);
        Assert.NotNull(failed.InteractionId);
        AssertErrorAck(failed.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, failed.Ack.Outcome?.Kind);
        var events = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Equal(2, events.Count);
        Assert.Equal(events.Min(evt => evt.Sequence), failed.Ack.FirstSeq);
        Assert.Equal(events.Max(evt => evt.Sequence), failed.Ack.LastSeq);
        var request = Assert.Single(events, evt => evt.Type.ToString() == "interaction.requested");
        Assert.Equal(failed.InteractionId, Assert.IsType<InteractionRequested>(setup.Codecs.Decode(request)).InteractionId);
        Assert.All(events, evt => Assert.Equal(Guid.Parse(failed.Ack.CommandId),
            Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value));

        var afterCommit = setup.Store.CurrentSequence(setup.Session);
        var retry = setup.Server.RequestPlanApprovalCommand();
        Assert.Equal(failed.InteractionId, retry.InteractionId);
        Assert.Null(retry.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, retry.Ack.Outcome?.Kind);
        Assert.Null(retry.Ack.FirstSeq);
        Assert.Null(retry.Ack.LastSeq);
        Assert.Equal(afterCommit, setup.Store.CurrentSequence(setup.Session));
        Assert.Equal(2, setup.Store.ReadFrom(setup.Session, before + 1).Count);
    }

    [Fact]
    public void Unreadable_postcommit_confirmation_is_deferred_without_fabricated_interaction_id()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        var original = new IOException("controlled postcommit failure");
        setup.Store.ThrowAfterNextBatchCommit = original;
        setup.Store.FailNextReadAfterCommittedBatch = true;

        var uncertain = setup.Server.RequestPlanApprovalCommand();

        Assert.Same(original, uncertain.Failure);
        Assert.Null(uncertain.InteractionId);
        AssertErrorAck(uncertain.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, uncertain.Ack.Outcome?.Kind);
        Assert.Equal("JournalOutcomeUnavailable", uncertain.Ack.Outcome?.Reason);
        Assert.Null(uncertain.Ack.FirstSeq);
        Assert.Null(uncertain.Ack.LastSeq);

        // The confirmation read fault is consumed. Inspect the journal after the returned
        // Deferred outcome; a fresh internal command must discover the existing pending request.
        var events = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Equal(2, events.Count);
        var durableRequest = Assert.IsType<InteractionRequested>(setup.Codecs.Decode(
            Assert.Single(events, evt => evt.Type.ToString() == "interaction.requested")));
        var currentSequence = setup.Store.CurrentSequence(setup.Session);
        var recovered = setup.Server.RequestPlanApprovalCommand();
        Assert.Equal(durableRequest.InteractionId, recovered.InteractionId);
        Assert.Null(recovered.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, recovered.Ack.Outcome?.Kind);
        Assert.Null(recovered.Ack.FirstSeq);
        Assert.Null(recovered.Ack.LastSeq);
        Assert.Equal(currentSequence, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Cancellation_exception_before_batch_preserves_original_token_and_returns_rejected()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        setup.Store.FailNextBatchBeforeCommit = original;

        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) result;
        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            result = setup.Server.RequestPlanApprovalCommand();
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
        }

        Assert.Same(original, result.Failure);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(result.Failure).CancellationToken);
        Assert.Null(result.InteractionId);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Ack.Outcome?.Kind);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
        Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Successful_append_with_unreadable_ack_is_deferred_and_consumer_does_not_wait_on_unconfirmed_id()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        setup.Store.FailNextReadAfterCommittedBatch = true;
        var result = setup.Server.RequestPlanApprovalCommand();
        Assert.Null(result.Failure);
        Assert.Null(result.InteractionId);
        AssertErrorAck(result.Ack);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        Assert.Equal("JournalOutcomeUnavailable", result.Ack.Outcome?.Reason);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
        Assert.Throws<InvalidOperationException>(() => OmniServer.RequirePlanApprovalInteraction(result));
        Assert.Equal(2, setup.Store.ReadFrom(setup.Session, before + 1).Count);
        var recovered = setup.Server.RequestPlanApprovalCommand();
        Assert.NotNull(recovered.InteractionId);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, recovered.Ack.Outcome?.Kind);
        Assert.Equal(recovered.InteractionId, OmniServer.RequirePlanApprovalInteraction(recovered));
        Assert.Equal(before + 2, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Public_compatibility_wrapper_preserves_original_cancellation_without_creating_an_approval()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        setup.Store.FailNextBatchBeforeCommit = original;
        var thrown = Assert.Throws<OperationCanceledException>(() => setup.Server.RequestPlanApprovalIfNeeded());
        Assert.Same(original, thrown);
        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
        Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Postcommit_failure_survives_sqlite_reopen_and_reuses_the_exact_pending_request(bool unreadable)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-plan-command-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.db");
        var statePath = Path.Combine(root, "session.state");
        var sqlite = new SqliteEventStore(path);
        try
        {
            using var setup = StartWithAcceptedProposal(sqlite, statePath);
            var before = setup.Store.CurrentSequence(setup.Session);
            var failure = new IOException("committed plan approval batch fault");
            setup.Store.ThrowAfterNextBatchCommit = failure;
            setup.Store.FailNextReadAfterCommittedBatch = unreadable;
            var result = setup.Server.RequestPlanApprovalCommand();
            Assert.Same(failure, result.Failure);
            AssertErrorAck(result.Ack);
            Assert.Equal(unreadable ? RuntimeCommandOutcomeKind.Deferred : RuntimeCommandOutcomeKind.Accepted,
                result.Ack.Outcome?.Kind);
            Assert.Same(failure, Assert.Throws<IOException>(() => OmniServer.RequirePlanApprovalInteraction(result)));
            Close(sqlite);
            sqlite = new SqliteEventStore(path);
            var events = sqlite.ReadFrom(setup.Session, before + 1);
            Assert.Equal(2, events.Count);
            var request = Assert.IsType<InteractionRequested>(setup.Codecs.Decode(
                Assert.Single(events, evt => evt.Type.ToString() == "interaction.requested")));
            Assert.Equal(InteractionKind.PlanApproval, request.Kind);
            Assert.Equal(unreadable ? null : request.InteractionId, result.InteractionId);
            Assert.Equal(unreadable ? (long?)null : events.Min(evt => evt.Sequence), result.Ack.FirstSeq);
            Assert.Equal(unreadable ? (long?)null : events.Max(evt => evt.Sequence), result.Ack.LastSeq);
            Assert.All(events, evt => Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(result.Ack.CommandId))), evt.Causation));
            var reopenedServer = new OmniServer(sqlite, setup.Codecs, new InMemoryAuditSink(), statePath, setup.Artifacts);
            // Restore the actual saved session state together with the reopened journal.
            Assert.Equal(setup.Session, reopenedServer.LastSessionId());
            Assert.Equal(setup.Run, reopenedServer.LastRunId());
            var recovered = reopenedServer.RequestPlanApprovalCommand();
            Assert.Equal(request.InteractionId, recovered.InteractionId);
            Assert.Null(recovered.Failure);
            Assert.Equal(RuntimeCommandOutcomeKind.NoOp, recovered.Ack.Outcome?.Kind);
            Assert.Null(recovered.Ack.FirstSeq);
            Assert.Null(recovered.Ack.LastSeq);
            Assert.Equal(request.InteractionId, OmniServer.RequirePlanApprovalInteraction(recovered));
            Assert.Equal(before + 2, sqlite.CurrentSequence(setup.Session));
            var retained = sqlite.ReadFrom(setup.Session, 1);
            Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(setup.Session, setup.Run, setup.Codecs, retained).State);
            CanonicalStateTracker.Replay(setup.Codecs, retained);
        }
        finally
        {
            Close(sqlite);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }

    private static void AssertErrorAck(CommandAck ack)
    {
        Assert.False(string.IsNullOrWhiteSpace(ack.CommandId));
        Assert.Equal("error", ack.Status);
        Assert.False(string.IsNullOrWhiteSpace(ack.Error));
        Assert.NotNull(ack.Outcome);
    }

    private sealed class Fixture : IDisposable
    {
        public OmniServer Server { get; }
        public TestEventStore Store { get; }
        public EventCodecs Codecs { get; }
        public FileArtifactStore Artifacts { get; }
        public string ArtifactPath { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public TaskId Task { get; }

        public Fixture(OmniServer server, TestEventStore store, EventCodecs codecs,
            FileArtifactStore artifacts, string artifactPath, SessionId session, RunId run,
            LaneId lane, TaskId task)
        {
            Server = server;
            Store = store;
            Codecs = codecs;
            Artifacts = artifacts;
            ArtifactPath = artifactPath;
            Session = session;
            Run = run;
            Lane = lane;
            Task = task;
        }

        public void Dispose()
        {
            if (Directory.Exists(ArtifactPath)) Directory.Delete(ArtifactPath, recursive: true);
        }
    }

    private sealed class TestEventStore(IEventStore? inner = null) : IEventStore
    {
        private readonly IEventStore _inner = inner ?? new InMemoryEventStore();
        public Exception? FailNextBatchBeforeCommit { get; set; }
        public Exception? ThrowAfterNextBatchCommit { get; set; }
        public bool FailNextReadAfterCommittedBatch { get; set; }
        private bool _failNextRead;

        public void Append(SessionId sessionId, DomainEvent item, DurabilityClass durability,
            CancellationToken cancellationToken) => _inner.Append(sessionId, item, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> items,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (FailNextBatchBeforeCommit is { } before)
            {
                FailNextBatchBeforeCommit = null;
                throw before;
            }

            _inner.AppendBatch(sessionId, items, durability, cancellationToken);
            if (FailNextReadAfterCommittedBatch)
            {
                FailNextReadAfterCommittedBatch = false;
                _failNextRead = true;
            }
            if (ThrowAfterNextBatchCommit is { } after)
            {
                ThrowAfterNextBatchCommit = null;
                throw after;
            }
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        {
            if (_failNextRead)
            {
                _failNextRead = false;
                throw new IOException("controlled confirmation read failure");
            }
            return _inner.ReadFrom(sessionId, fromSequenceInclusive);
        }
    }

    private static Fixture StartWithAcceptedProposal(IEventStore? inner = null, string? statePath = null)
    {
        var artifactPath = Path.Combine(Path.GetTempPath(), "omnicore-plan-failure-outcome-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactPath);
        var artifacts = new FileArtifactStore(artifactPath);
        var store = new TestEventStore(inner);
        var codecs = EventCodecs.Create();
        var server = statePath is null
            ? new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts)
            : new OmniServer(store, codecs, new InMemoryAuditSink(), statePath, artifacts);
        var start = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "explore.start") + ","
            + JsonObj.Field("objective", "explain repository") + ","
            + JsonObj.Field("workspace", artifactPath) + "}"), CancellationToken.None);
        Assert.Equal("ok", start.Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var lane = Assert.IsType<LaneId>(server.LastLaneId());
        var projection = RunProjection.Replay(session, run, codecs, store.ReadFrom(session, 1));
        var task = new TaskId(projection.RootTask!.Value);

        var tools = OmniHost.CreateExplorerTools();
        var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Plan));
        try
        {
            var turn = new ExplorerTurn((request, _) => FakeResponses.PlanThenEnd(request), executor,
                tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("plan-approval", "h", "t", "c", "o", "test"),
                new ModelSelection(new ModelIdValue("plan-approval"), 4096, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            var result = turn.Ask("explain repository", "system", session, run, lane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            return new Fixture(server, store, codecs, artifacts, artifactPath, session, run, lane, task);
        }
        catch
        {
            if (Directory.Exists(artifactPath)) Directory.Delete(artifactPath, recursive: true);
            throw;
        }
    }
}
