using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Structural lineage and receipt-provenance checks; these fixtures do not execute agents.</summary>
public sealed class PreM6ExecutionIntegrityTests
{
    [Theory]
    [InlineData("missing-parent")]
    [InlineData("missing-lane")]
    [InlineData("profile-mismatch")]
    [InlineData("task-envelope-mismatch")]
    [InlineData("run-envelope-mismatch")]
    public void Invalid_execution_start_links_are_rejected_without_advancing_the_journal(string defect)
    {
        var fixture = new Fixture();
        var execution = ExecutionId.New();
        AgentExecutionStarted start;
        ExecutionScopeState scope;

        switch (defect)
        {
            case "missing-parent":
                start = new(execution, fixture.ChildLane, fixture.ChildProfile, ExecutionId.New(),
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                scope = fixture.ChildScope(execution);
                break;
            case "missing-lane":
                start = new(execution, LaneId.New(), ProfileId.New(), fixture.Owner,
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                scope = fixture.OwnerScope(execution);
                break;
            case "profile-mismatch":
                start = new(execution, fixture.ChildLane, ProfileId.New(), fixture.Owner,
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                scope = fixture.ChildScope(execution);
                break;
            case "task-envelope-mismatch":
                start = new(execution, fixture.ChildLane, fixture.ChildProfile, fixture.Owner,
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                scope = fixture.OwnerScope(execution); // valid Task and Lane IDs, but from different owners
                break;
            case "run-envelope-mismatch":
                start = new(execution, fixture.ChildLane, fixture.ChildProfile, fixture.Owner,
                    ExecutionRelation.Awaited, ExecutionSupervision.Managed);
                scope = new( RunId.New(), fixture.ChildTask, fixture.ChildLane, ExecutionId: execution);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        var before = fixture.Store.CurrentSequence(fixture.Session);
        using (ExecutionScope.Begin(scope))
            Assert.Throws<InvalidStateTransitionException>(() => fixture.Stream.Append(start));
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));

        // A failed candidate must not poison the writer's state: an exact, durable parent link can follow.
        var valid = new AgentExecutionStarted(execution, fixture.ChildLane, fixture.ChildProfile, fixture.Owner,
            ExecutionRelation.Awaited, ExecutionSupervision.Managed);
        using (ExecutionScope.Begin(fixture.ChildScope(execution))) fixture.Stream.Append(valid);
        var saved = Assert.Single(fixture.Events.Select(fixture.Codecs.Decode).OfType<AgentExecutionStarted>(),
            item => item.ExecutionId == execution);
        Assert.Equal(fixture.Owner, saved.ParentExecutionId);
        Assert.Equal(fixture.ChildLane, saved.LaneId);
        Assert.Equal(fixture.ChildProfile, saved.ProfileId);
        Assert.Equal(fixture.Run.RunId, Assert.Single(fixture.Events, evt => evt.ExecutionId == execution).RunId);
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("envelope-conflict")]
    public void Raw_replay_rejects_invalid_execution_start_lineage_and_envelope(string defect)
    {
        var fixture = new Fixture();
        var execution = ExecutionId.New();
        var start = defect == "missing-parent"
            ? new AgentExecutionStarted(execution, fixture.ChildLane, fixture.ChildProfile, ExecutionId.New(),
                ExecutionRelation.Awaited, ExecutionSupervision.Managed)
            : new AgentExecutionStarted(execution, fixture.ChildLane, fixture.ChildProfile, fixture.Owner,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed);
        var task = defect == "envelope-conflict" ? fixture.Run.RootTask : fixture.ChildTask;
        var raw = Raw(fixture.Session, fixture.Run.RunId, task, fixture.ChildLane, execution,
            fixture.Codecs, start);

        Assert.Throws<InvalidStateTransitionException>(() => PreM6RecordProjection.Replay(
            fixture.Session, fixture.Codecs, fixture.Events.Append(raw)));
    }

    [Fact]
    public void New_record_cannot_be_owned_by_an_execution_without_durable_task_lane_profile_lineage()
    {
        var fixture = new Fixture();
        var unknownOwner = ExecutionId.New();
        var result = new AgentResultProduced(unknownOwner, Ref(), 1, "fixture.result.v1");
        var before = fixture.Store.CurrentSequence(fixture.Session);

        using (ExecutionScope.Begin(fixture.OwnerScope(unknownOwner)))
            Assert.Throws<InvalidStateTransitionException>(() => fixture.Stream.Append(result));

        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        Assert.DoesNotContain(fixture.Events.Select(fixture.Codecs.Decode),
            payload => payload is AgentResultProduced produced && produced.ExecutionId == unknownOwner);
    }

    [Fact]
    public void Invalid_start_batch_is_atomic_and_can_be_retried_with_exact_parent_and_idempotent_facts()
    {
        var fixture = new Fixture();
        var execution = ExecutionId.New();
        var result = new AgentResultProduced(execution, Ref(), 1, "fixture.result.v1");
        var badStart = new AgentExecutionStarted(execution, fixture.ChildLane, fixture.ChildProfile,
            ExecutionId.New(), ExecutionRelation.Awaited, ExecutionSupervision.Managed);
        var childScope = fixture.ChildScope(execution);
        var before = fixture.Store.CurrentSequence(fixture.Session);

        Assert.Throws<InvalidStateTransitionException>(() => fixture.Stream.AppendBatch(
            [badStart, result], DurabilityClass.Barrier, [childScope, childScope]));
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));

        var exactStart = badStart with { ParentExecutionId = fixture.Owner };
        fixture.Stream.AppendBatch([exactStart, result], DurabilityClass.Barrier, [childScope, childScope]);
        Assert.Equal(before + 2, fixture.Store.CurrentSequence(fixture.Session));

        // Repeating the exact start and record is accepted; projection history does not duplicate the fact.
        using (ExecutionScope.Begin(childScope))
        {
            fixture.Stream.Append(exactStart);
            fixture.Stream.Append(result);
        }
        var projection = PreM6RecordProjection.Replay(fixture.Session, fixture.Codecs, fixture.Events);
        var history = Assert.Single(projection.Records.Values, entry => entry.Facts.Any(fact =>
            fact is AgentResultProduced produced && produced.ResultRef == result.ResultRef));
        Assert.Equal(1, Assert.Single(history.Facts.OfType<AgentResultProduced>()).ResultRevision);
        var repeatedStarts = fixture.Events.Select(fixture.Codecs.Decode).OfType<AgentExecutionStarted>()
            .Where(item => item.ExecutionId == execution).ToArray();
        Assert.Equal(2, repeatedStarts.Length);
        Assert.All(repeatedStarts, start => Assert.Equal(exactStart, start));
    }

    [Theory]
    [InlineData("isolated-success")]
    [InlineData("mixed-session")]
    [InlineData("duplicate-event")]
    public void Cross_session_tool_receipt_requires_a_canonical_success_lifecycle(string defect)
    {
        var fixture = new Fixture();
        var foreignSession = SessionId.New();
        var foreignStream = new EventStream(fixture.Store, fixture.Codecs, foreignSession);
        var foreignRun = TestRun.Open(foreignStream, foreignSession);
        var foreignExecution = ExecutionId.New();
        var foreignProfile = Assert.IsType<LaneCreated>(fixture.Codecs.Decode(fixture.Store.ReadFrom(foreignSession, 1)
            .Single(evt => fixture.Codecs.Decode(evt) is LaneCreated))).AgentProfile;
        foreignStream.Append(new AgentExecutionStarted(foreignExecution, foreignRun.RootLane, foreignProfile,
            null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));

        var call = ToolCallId.New();
        using (ExecutionScope.Begin(new(foreignRun.RunId, foreignRun.RootTask, foreignRun.RootLane,
            ExecutionId: foreignExecution)))
        {
            foreignStream.Append(new ToolCallRequested(call, "fixture-call", "fixture.tool", "{}"));
            foreignStream.Append(new ToolCallPrepared(call, "{}"));
            foreignStream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null));
            foreignStream.Append(new ToolCallAuthorized(call));
            foreignStream.Append(new ToolCallStarted(call, EffectClass.None, null));
            foreignStream.Append(new ToolCallSucceeded(call, "fixture-only"));
        }
        var foreignEvents = fixture.Store.ReadFrom(foreignSession, 1);
        var success = Assert.Single(foreignEvents, evt => fixture.Codecs.Decode(evt) is ToolCallSucceeded succeeded
            && succeeded.ToolCallId == call);
        var receipt = new EvidenceReceiptRef(foreignSession, success.EventId, call);
        var evidence = new EvidenceRef(EvidenceKind.ToolBacked, "scripted receipt", null, receipt);
        var scope = new ValidationScope(fixture.Session, fixture.Run.RunId, fixture.Run.RootTask, fixture.Run.RootLane);
        var valid = new IntegrationStatusRecorded(fixture.Owner,
            new(scope, IntegrationStatus.Verified, [evidence]));
        var localPrefix = fixture.Events;

        using (ExecutionScope.Begin(fixture.OwnerScope(fixture.Owner))) fixture.Stream.Append(valid);
        var projection = PreM6RecordProjection.Replay(fixture.Session, fixture.Codecs, fixture.Events,
            session => session == foreignSession ? foreignEvents : []);
        Assert.Contains(projection.Records.Values, history => history.Facts.Any(fact =>
            fact is IntegrationStatusRecorded status && status.Status.Status == IntegrationStatus.Verified));

        // Same success payload and ToolCallId, but no Requested→Prepared→Authorized→Started prefix.
        var isolated = success; // exact receipt identity; only the canonical lifecycle prefix is absent
        var badLocal = Raw(fixture.Session, fixture.Run.RunId, fixture.Run.RootTask, fixture.Run.RootLane,
            fixture.Owner, fixture.Codecs, valid);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        IReadOnlyList<DomainEvent> invalidJournal = defect switch
        {
            "isolated-success" => [isolated],
            "mixed-session" => [.. foreignEvents, localPrefix[0]],
            "duplicate-event" => [.. foreignEvents, success],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        Assert.Throws<InvalidStateTransitionException>(() => PreM6RecordProjection.Replay(
            fixture.Session, fixture.Codecs, localPrefix.Append(badLocal),
            session => session == foreignSession ? invalidJournal : []));
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
    }

    [Fact]
    public void A_failed_store_commit_does_not_consume_execution_identity_or_result_revision()
    {
        var fixture = new Fixture();
        var failingStore = new FailNextBatchStore(fixture.Store);
        var stream = new EventStream(failingStore, fixture.Codecs, fixture.Session);
        var execution = ExecutionId.New();
        var start = new AgentExecutionStarted(execution, fixture.ChildLane, fixture.ChildProfile, fixture.Owner,
            ExecutionRelation.Detached, ExecutionSupervision.Unmanaged);
        var produced = new AgentResultProduced(execution, Ref(), 1, "fixture.result.v1");
        var childScope = fixture.ChildScope(execution);
        var before = fixture.Store.CurrentSequence(fixture.Session);
        Assert.Throws<IOException>(() => stream.AppendBatch([start, produced], DurabilityClass.Barrier,
            [childScope, childScope]));
        Assert.Equal(before, fixture.Store.CurrentSequence(fixture.Session));
        stream.AppendBatch([start, produced], DurabilityClass.Barrier, [childScope, childScope]);
        Assert.Equal(before + 2, fixture.Store.CurrentSequence(fixture.Session));
        var projection = PreM6RecordProjection.Replay(fixture.Session, fixture.Codecs, fixture.Events);
        Assert.Equal(produced, Assert.Single(Assert.Single(projection.Records.Values).Facts));
    }

    [Fact]
    public void New_record_family_has_no_implicit_schema_zero_upcast()
    {
        var codecs = EventCodecs.Create();
        var payload = new WakeRequestFailed(ExecutionId.New(), WakeRequestId.New(), "fixture failure");
        var current = Raw(SessionId.New(), null, null, null, payload.ExecutionId, codecs, payload);
        var zero = DomainEvent.Stored(current.EventId, current.SessionId, 1, current.Type, 0,
            current.Timestamp, current.Causation, current.CorrelationId, current.RunId, current.TaskId,
            current.LaneId, current.TurnId, current.PlanItemId, current.ToolCallId, current.ArtifactRefs,
            current.PayloadJson, current.ExecutionId, current.Source);
        Assert.Throws<EventParseException>(() => codecs.Decode(zero));
    }

    private sealed class FailNextBatchStore(IEventStore inner) : IEventStore
    {
        private bool _fail = true;
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token) =>
            inner.Append(session, evt, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken token)
        {
            if (_fail) { _fail = false; throw new IOException("fixture precommit failure"); }
            inner.AppendBatch(session, events, durability, token);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }

    private static DomainEvent Raw(SessionId session, RunId? run, TaskId? task, LaneId? lane,
        ExecutionId? execution, EventCodecs codecs, DomainEventPayload payload) =>
        DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, run, run, task, lane,
            null, null, (payload as ToolCallSucceeded) is { } succeeded ? succeeded.ToolCallId : null,
            [], codecs.CodecFor(payload.Type()).Encode(payload), execution, "PreM6ExecutionIntegrityTests");

    private static ArtifactRef Ref() => new(ArtifactId.New(), ContentHash.Sha256(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
        1, "text/plain", ArtifactKind.Other, Sensitivity.Normal);

    private sealed class Fixture
    {
        internal InMemoryEventStore Store { get; } = new();
        internal EventCodecs Codecs { get; } = EventCodecs.Create();
        internal SessionId Session { get; } = SessionId.New();
        internal EventStream Stream { get; }
        internal TestRun.Opened Run { get; }
        internal ExecutionId Owner { get; } = ExecutionId.New();
        internal ExecutionId Child { get; } = ExecutionId.New();
        internal TaskId ChildTask { get; } = TaskId.New();
        internal LaneId ChildLane { get; } = LaneId.New();
        internal ProfileId ChildProfile { get; } = ProfileId.New();
        internal IReadOnlyList<DomainEvent> Events => Store.ReadFrom(Session, 1);

        internal Fixture()
        {
            Stream = new(Store, Codecs, Session);
            Run = TestRun.Open(Stream, Session, agentProfile: ProfileId.New());
            var rootProfile = Events.Select(Codecs.Decode).OfType<LaneCreated>()
                .Single(item => item.LaneId == Run.RootLane).AgentProfile;
            Stream.Append(new AgentExecutionStarted(Owner, Run.RootLane, rootProfile, null,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed));
            Stream.Append(new TaskCreated(ChildTask, Run.RunId, "child fixture", [], new(null, null, null, null),
                Run.RootTask));
            Stream.Append(new LaneCreated(ChildLane, ChildTask, ChildProfile));
            Stream.Append(new AgentExecutionStarted(Child, ChildLane, ChildProfile, Owner,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        }

        internal ExecutionScopeState OwnerScope(ExecutionId execution) =>
            new(Run.RunId, Run.RootTask, Run.RootLane, ExecutionId: execution);
        internal ExecutionScopeState ChildScope(ExecutionId execution) =>
            new(Run.RunId, ChildTask, ChildLane, ExecutionId: execution);
    }
}
