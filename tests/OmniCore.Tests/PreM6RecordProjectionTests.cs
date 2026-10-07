using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Deterministic journal fixtures, not worker execution or authenticated provider acceptance.</summary>
public sealed class PreM6RecordProjectionTests
{
    [Fact]
    public void Execution_identity_cannot_be_rebound_even_before_new_record_families_are_used()
    {
        var f = new Fixture();
        var original = Assert.Single(f.Events.Select(f.Codecs.Decode).OfType<AgentExecutionStarted>(), e => e.ExecutionId == f.Owner);
        f.Stream.Append(original); // exact repeated fact remains readable
        var conflicting = original with { LaneId = f.ChildLane, ProfileId = f.ChildProfile };
        var count = f.Events.Count;
        Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(conflicting));
        Assert.Equal(count, f.Events.Count);

        var raw = DomainEvent.Create(f.Session, conflicting.Type(), conflicting.SchemaVersion(), null,
            f.Run.RunId, f.Run.RunId, f.ChildTask, f.ChildLane, null, null, null, [],
            f.Codecs.CodecFor(conflicting.Type()).Encode(conflicting), executionId: f.Owner);
        Assert.Throws<InvalidStateTransitionException>(() =>
            PreM6RecordProjection.Replay(f.Session, f.Codecs, f.Events.Append(raw)));
    }

    [Theory]
    [InlineData("sha256", "00")]
    [InlineData("sha512", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha256", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Invalid_artifact_hash_is_rejected_independently_of_other_record_fields(string algorithm, string hash)
    {
        var f = new Fixture();
        var packet = Ref() with { Hash = new ContentHash(algorithm, hash) };
        var created = new DelegationCreated(f.Owner, new(DelegationId.New(), f.Owner, f.ChildTask,
            f.ChildLane, f.ChildProfile, packet, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        using (f.OwnerScope())
        {
            var count = f.Events.Count;
            Assert.Throws<ArgumentException>(() => f.Stream.Append(created));
            Assert.Equal(count, f.Events.Count);
        }
    }

    [Fact]
    public void Delegation_requires_exact_child_and_produced_result_and_replays_without_completing_tasks()
    {
        var f = new Fixture();
        var id = DelegationId.New();
        var created = new DelegationCreated(f.Owner, new(id, f.Owner, f.ChildTask, f.ChildLane,
            f.ChildProfile, Ref(), ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        using (f.OwnerScope())
        {
            f.Stream.Append(created);
            var count = f.Events.Count;
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new DelegationAccepted(f.Owner, id, f.Owner)));
            Assert.Equal(count, f.Events.Count);
            f.Stream.Append(new DelegationAccepted(f.Owner, id, f.Child));
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new DelegationReturned(f.Owner, id, Ref())));
        }
        var result = Ref();
        using (f.ChildScope()) f.Stream.Append(new AgentResultProduced(f.Child, result, 1, "fixture.result.v1"));
        using (f.OwnerScope()) f.Stream.Append(new DelegationReturned(f.Owner, id, result));
        var projection = PreM6RecordProjection.Replay(f.Session, f.Codecs, f.Events);
        var delegation = projection.Records["delegation:" + id];
        Assert.Equal(PreM6RecordPhase.Returned, delegation.Phase);
        Assert.Equal(3, delegation.Facts.Count);
        Assert.Equal(result, Assert.IsType<DelegationReturned>(delegation.Facts[2]).ResultRef);
        var canonical = CanonicalStateTracker.Replay(f.Codecs, f.Events);
        Assert.Equal(TaskState.Running, canonical.Task(f.Run.RootTask));
        Assert.Equal(TaskState.Pending, canonical.Task(f.ChildTask));
        Assert.DoesNotContain(f.Events, evt => f.Codecs.Decode(evt) is TaskCompleted or LaneCompleted or RunCompleted);
    }

    [Fact]
    public void Record_identity_is_idempotent_but_conflicting_content_and_uncreated_transition_are_rejected()
    {
        var f = new Fixture();
        var id = MailboxId.New();
        var created = new ExecutionMailboxCreated(f.Owner, new(id, f.Owner));
        using (f.OwnerScope())
        {
            f.Stream.Append(created);
            f.Stream.Append(created);
            var messageId = MailboxMessageId.New();
            var message = new ExecutionMailboxMessageReceived(f.Owner, new(messageId, id, f.Child, Ref()));
            f.Stream.Append(message);
            f.Stream.Append(message);
            f.Stream.Append(new ExecutionMailboxMessageAcknowledged(f.Owner, id, messageId));
            var count = f.Events.Count;
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(message with
            { Message = message.Message with { ContentRef = Ref() } }));
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new WakeRequestAccepted(f.Owner, WakeRequestId.New())));
            Assert.Equal(count, f.Events.Count);
            f.Stream.Append(new ExecutionMailboxMessageAcknowledged(f.Owner, id, messageId));
        }
        var records = PreM6RecordProjection.Replay(f.Session, f.Codecs, f.Events).Records;
        Assert.Single(records["mailbox:" + id].Facts);
        Assert.Equal(2, Assert.Single(records, pair => pair.Key.StartsWith("message:", StringComparison.Ordinal)).Value.Facts.Count);
    }

    [Fact]
    public void Invalid_batch_does_not_consume_result_revision_and_foreign_envelope_never_persists()
    {
        var f = new Fixture();
        var result = Ref();
        var produced = new AgentResultProduced(f.Owner, result, 1, "fixture.result.v1");
        using (f.OwnerScope())
        {
            var count = f.Events.Count;
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.AppendBatch(
                [produced, new ResultDispositionRecorded(f.Owner, new(DispositionId.New(), f.Owner, Ref(),
                    ResultDispositionOutcome.Rejected, null, "fixture rejection", []))], DurabilityClass.Barrier));
            Assert.Equal(count, f.Events.Count);
            f.Stream.Append(produced);
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(produced with { ResultRevision = 2 }));
            f.Stream.Append(new AgentResultProduced(f.Owner, Ref(), 2, "fixture.result.v1", result));
        }
        using (ExecutionScope.Begin(new(f.Run.RunId, f.ChildTask, f.ChildLane, ExecutionId: f.Owner)))
        {
            var count = f.Events.Count;
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new ExecutionMailboxCreated(f.Owner, new(MailboxId.New(), f.Owner))));
            Assert.Equal(count, f.Events.Count);
        }
        Assert.Equal(2, PreM6RecordProjection.Replay(f.Session, f.Codecs, f.Events).Records.Count);
    }

    [Fact]
    public void Passed_claim_and_foreign_or_non_success_receipt_cannot_become_verified_evidence()
    {
        var f = new Fixture();
        var scope = new ValidationScope(f.Session, f.Run.RunId, f.Run.RootTask, f.Run.RootLane);
        using (f.OwnerScope())
        {
            var count = f.Events.Count;
            Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new ValidationStateRecorded(f.Owner,
                new(scope, ValidationLevel.Local, ValidationStatus.Passed, ["fixture check"], [new(EvidenceKind.Claim, "model says pass")]))));
            foreach (var session in new[] { f.Session, SessionId.New() })
            {
                var eventId = session == f.Session ? f.Events[0].EventId : EventId.New();
                var evidence = new EvidenceRef(EvidenceKind.ToolBacked, "unverified fixture receipt", null,
                    new(session, eventId, ToolCallId.New()));
                Assert.Throws<InvalidStateTransitionException>(() => f.Stream.Append(new IntegrationStatusRecorded(f.Owner,
                    new(scope, IntegrationStatus.Verified, [evidence]))));
            }
            Assert.Equal(count, f.Events.Count);
            f.Stream.Append(new ValidationStateRecorded(f.Owner, new(scope, ValidationLevel.Local,
                ValidationStatus.Unknown, [], [new(EvidenceKind.Claim, "not measured")])));
        }
    }

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
            var profile = ProfileId.New();
            Run = TestRun.Open(Stream, Session, agentProfile: profile);
            Stream.Append(new AgentExecutionStarted(Owner, Run.RootLane, profile, null, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
            Stream.Append(new TaskCreated(ChildTask, Run.RunId, "child fixture", [], new(null, null, null, null), Run.RootTask));
            Stream.Append(new LaneCreated(ChildLane, ChildTask, ChildProfile));
            Stream.Append(new AgentExecutionStarted(Child, ChildLane, ChildProfile, Owner, ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        }
        internal IDisposable OwnerScope() => ExecutionScope.Begin(new(Run.RunId, Run.RootTask, Run.RootLane, ExecutionId: Owner));
        internal IDisposable ChildScope() => ExecutionScope.Begin(new(Run.RunId, ChildTask, ChildLane, ExecutionId: Child));
    }
}
