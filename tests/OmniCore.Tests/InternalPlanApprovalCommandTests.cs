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

public sealed class InternalPlanApprovalCommandTests
{
    [Fact]
    public void Missing_session_and_no_accepted_proposal_are_explicit_noops()
    {
        var empty = OmniHost.CreateInMemoryServer();
        var withoutSession = empty.RequestPlanApprovalCommand();
        Assert.Null(withoutSession.InteractionId);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, withoutSession.Ack.Outcome?.Kind);
        Assert.Null(withoutSession.Ack.FirstSeq);
        Assert.Null(withoutSession.Ack.LastSeq);

        using var setup = StartPlanSession();
        var before = setup.Store.CurrentSequence(setup.Session);
        var noProposal = setup.Server.RequestPlanApprovalCommand();
        Assert.Null(noProposal.InteractionId);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, noProposal.Ack.Outcome?.Kind);
        Assert.Null(noProposal.Ack.FirstSeq);
        Assert.Null(noProposal.Ack.LastSeq);
        Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
    }

    [Fact]
    public void Published_approval_has_command_causation_range_and_entity_ids()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);

        var publication = setup.Server.RequestPlanApprovalCommand();

        Assert.NotNull(publication.InteractionId);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, publication.Ack.Outcome?.Kind);
        var commandId = Guid.Parse(publication.Ack.CommandId);
        var written = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Equal(2, written.Count);
        Assert.Equal(written.Min(evt => evt.Sequence), publication.Ack.FirstSeq);
        Assert.Equal(written.Max(evt => evt.Sequence), publication.Ack.LastSeq);
        Assert.All(written, evt => Assert.Equal(commandId,
            Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value));
        var request = Assert.Single(written, evt => evt.Type.ToString() == "interaction.requested");
        Assert.Equal(setup.Run, request.RunId);
        Assert.Equal(setup.Lane, request.LaneId);
        Assert.Equal(setup.Task, request.TaskId);
        var awaiting = Assert.Single(written, evt => evt.Type.ToString() == "run.awaiting_input");
        Assert.Equal(setup.Run, awaiting.RunId);
        Assert.Equal(setup.Lane, awaiting.LaneId);
        Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(setup.Session, setup.Run,
            setup.Codecs, setup.Store.ReadFrom(setup.Session, 1)).State);
    }

    [Fact]
    public void Existing_command_scope_is_preserved_and_duplicate_request_is_noop()
    {
        using var setup = StartWithAcceptedProposal();
        var ambient = new CommandCausation(new CommandId(Guid.NewGuid()));
        var before = setup.Store.CurrentSequence(setup.Session);
        using (CausationScope.Begin(ambient))
        {
            var first = setup.Server.RequestPlanApprovalCommand();
            Assert.Equal(ambient.CommandId.Value.ToString(), first.Ack.CommandId);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, first.Ack.Outcome?.Kind);
            Assert.Equal(ambient, CausationScope.Current);

            var second = setup.Server.RequestPlanApprovalCommand();
            Assert.Equal(first.InteractionId, second.InteractionId);
            Assert.Equal(RuntimeCommandOutcomeKind.NoOp, second.Ack.Outcome?.Kind);
            Assert.Null(second.Ack.FirstSeq);
            Assert.Null(second.Ack.LastSeq);
            Assert.Equal(ambient, CausationScope.Current);
        }

        var written = setup.Store.ReadFrom(setup.Session, before + 1);
        Assert.Equal(2, written.Count);
        Assert.All(written, evt => Assert.Equal(ambient, evt.Causation));
        Assert.Null(CausationScope.Current);
    }

    [Fact]
    public void Failed_batch_restores_parent_scopes_and_retry_commits_once_with_own_ids()
    {
        using var setup = StartWithAcceptedProposal();
        var before = setup.Store.CurrentSequence(setup.Session);
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var otherRun = RunId.New();
        var otherTask = TaskId.New();
        var otherLane = LaneId.New();
        var parentExecution = new ExecutionScopeState(otherRun, otherTask, otherLane);
        setup.Store.FailNextBatch = true;

        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            var failed = setup.Server.RequestPlanApprovalCommand();
            Assert.IsType<IOException>(failed.Failure);
            Assert.Null(failed.InteractionId);
            Assert.Equal("error", failed.Ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, failed.Ack.Outcome?.Kind);
            Assert.Null(failed.Ack.FirstSeq);
            Assert.Null(failed.Ack.LastSeq);
            Assert.Equal(before, setup.Store.CurrentSequence(setup.Session));
            Assert.Empty(setup.Store.ReadFrom(setup.Session, before + 1));
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var retry = setup.Server.RequestPlanApprovalCommand();
            Assert.NotEqual(failed.Ack.CommandId, retry.Ack.CommandId);
            Assert.NotNull(retry.InteractionId);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, retry.Ack.Outcome?.Kind);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var written = setup.Store.ReadFrom(setup.Session, before + 1);
            Assert.Equal(2, written.Count);
            Assert.Equal(written.Min(evt => evt.Sequence), retry.Ack.FirstSeq);
            Assert.Equal(written.Max(evt => evt.Sequence), retry.Ack.LastSeq);
            Assert.All(written, evt => Assert.Equal(Guid.Parse(retry.Ack.CommandId),
                Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value));
            var request = Assert.Single(written, evt => evt.Type.ToString() == "interaction.requested");
            Assert.Equal(setup.Run, request.RunId);
            Assert.Equal(setup.Task, request.TaskId);
            Assert.Equal(setup.Lane, request.LaneId);
            var awaiting = Assert.Single(written, evt => evt.Type.ToString() == "run.awaiting_input");
            Assert.Equal(setup.Run, awaiting.RunId);
            Assert.Equal(setup.Lane, awaiting.LaneId);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    private sealed class Setup : IDisposable
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

        public Setup(OmniServer server, TestEventStore store, EventCodecs codecs,
            FileArtifactStore artifacts, string artifactPath, SessionId session, RunId run, LaneId lane, TaskId task)
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

    private sealed class TestEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextBatch { get; set; }

        public void Append(SessionId sessionId, DomainEvent item, DurabilityClass durability,
            CancellationToken cancellationToken) =>
            _inner.Append(sessionId, item, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> items,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (FailNextBatch)
            {
                FailNextBatch = false;
                throw new IOException("controlled failure before batch persistence");
            }

            _inner.AppendBatch(sessionId, items, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
    }

    private static Setup StartPlanSession()
    {
        var artifactPath = Path.Combine(Path.GetTempPath(), "omnicore-plan-command-"
            + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(artifactPath);
        var store = new TestEventStore();
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts);
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "explore.start")
            + "," + JsonObj.Field("objective", "explain repository") + "}"), CancellationToken.None);
        Assert.Equal("ok", ack.Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var run = Assert.IsType<RunId>(server.LastRunId());
        var lane = Assert.IsType<LaneId>(server.LastLaneId());
        var projection = RunProjection.Replay(session, run, codecs, store.ReadFrom(session, 1));
        return new Setup(server, store, codecs, artifacts, artifactPath, session, run, lane,
            new TaskId(projection.RootTask!.Value));
    }

    private static Setup StartWithAcceptedProposal()
    {
        var setup = StartPlanSession();
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
                setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(),
                new RedactionPolicy());
            var result = turn.Ask("explain repository", "system", setup.Session, setup.Run, setup.Lane, "",
                CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            return setup;
        }
        catch
        {
            setup.Dispose();
            throw;
        }
    }
}
