using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class PreM6FrozenContractTests
{
    [Fact]
    public void Frozen_pre_m6_records_roundtrip_through_codec_event_stream_and_sqlite_without_runtime_effects()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "omnicore-prem6-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var journalPath = Path.Combine(temporaryRoot, "journal.db");
        var artifacts = new FileArtifactStore(Path.Combine(temporaryRoot, "cas"));
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journalPath);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var stream = new EventStream(store, codecs, session);

            var execution = ExecutionId.New();
            var childExecution = ExecutionId.New();
            var packet = Put("delegation packet");
            var failedPacket = Put("failed delegation packet");
            var content = Put("mailbox message content");
            var priorResult = Put("typed result revision one fixture");
            var result = Put("typed result revision two fixture");
            var evidenceArtifact = Put("fixture evidence attachment");
            var specification = Put("fixture validation specification");
            var mailbox = MailboxId.New();
            var messageId = MailboxMessageId.New();
            var toolCall = ToolCallId.New();
            var delegationId = DelegationId.New();
            var failedDelegationId = DelegationId.New();
            var resolvedJoinId = JoinId.New();
            var failedJoinId = JoinId.New();
            var bindingId = BindingId.New();
            var failedBindingId = BindingId.New();
            var wakeRequestId = WakeRequestId.New();
            var failedWakeRequestId = WakeRequestId.New();
            var debtId = ValidationDebtId.New();

            var rootProfile = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<LaneCreated>()
                .Single(evt => evt.LaneId == run.RootLane).AgentProfile;
            stream.Append(new AgentExecutionStarted(execution, run.RootLane, rootProfile, null,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed));

            var childTask = TaskId.New();
            var childLane = LaneId.New();
            var childProfile = ProfileId.New();
            stream.Append(new TaskCreated(childTask, run.RunId, "contract fixture child", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null), run.RootTask));
            stream.Append(new TaskReady(childTask));
            stream.Append(new LaneCreated(childLane, childTask, childProfile));
            stream.Append(new LaneStarted(childLane));
            stream.Append(new TaskStarted(childTask, childLane));
            stream.Append(new AgentExecutionStarted(childExecution, childLane, childProfile, execution,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed));

            var failedTask = TaskId.New();
            var failedLane = LaneId.New();
            var failedProfile = ProfileId.New();
            stream.Append(new TaskCreated(failedTask, run.RunId, "failed contract fixture child",
                Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null), run.RootTask));
            stream.Append(new TaskReady(failedTask));
            stream.Append(new LaneCreated(failedLane, failedTask, failedProfile));

            // EvidenceReceiptRef points to a real canonical success event in this private fixture.
            stream.Append(new ToolCallRequested(toolCall, "fixture-provider-call", "fixture.tool", "{}"));
            stream.Append(new ToolCallPrepared(toolCall, "{}"));
            stream.Append(new PermissionEvaluated(toolCall, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(toolCall));
            stream.Append(new ToolCallStarted(toolCall, EffectClass.None, null));
            stream.Append(new ToolCallSucceeded(toolCall, "fixture-only"));
            var receiptEvent = store.ReadFrom(session, 1)
                .Single(evt => codecs.Decode(evt) is ToolCallSucceeded succeeded && succeeded.ToolCallId == toolCall);
            var evidence = new EvidenceRef(EvidenceKind.ToolBacked, "scripted fixture receipt", evidenceArtifact,
                new EvidenceReceiptRef(session, receiptEvent.EventId, toolCall));
            var evidenceRefs = new[] { evidence };

            var joinMembers = new[] { execution, childExecution };
            var scope = new ValidationScope(session, run.RunId, childTask, childLane,
                SpecificationRef: specification);
            IPreM6ContractEvent[] records =
            [
                new DelegationCreated(execution, new Delegation(delegationId, execution, childTask,
                    childLane, childProfile, packet, ExecutionRelation.Awaited, ExecutionSupervision.Managed)),
                new DelegationAccepted(execution, delegationId, childExecution),
                new AgentResultProduced(childExecution, priorResult, 1, "core.explorer.v1"),
                new AgentResultProduced(childExecution, result, 2, "core.explorer.v1", priorResult),
                new DelegationReturned(execution, delegationId, result),
                new DelegationCreated(execution, new Delegation(failedDelegationId, execution, failedTask,
                    failedLane, failedProfile, failedPacket, ExecutionRelation.Detached,
                    ExecutionSupervision.Unmanaged)),
                new DelegationFailed(execution, failedDelegationId, "fixture failure"),
                new ExecutionJoinCreated(execution, new ExecutionJoin(resolvedJoinId, execution, joinMembers,
                    new JoinPolicy(JoinKind.All))),
                new ExecutionJoinCreated(execution, new ExecutionJoin(failedJoinId, execution, joinMembers,
                    new JoinPolicy(JoinKind.Any))),
                new ExecutionJoinCreated(execution, new ExecutionJoin(JoinId.New(), execution, joinMembers,
                    new JoinPolicy(JoinKind.Quorum, RequiredCount: 1))),
                new ExecutionJoinCreated(execution, new ExecutionJoin(JoinId.New(), execution, joinMembers,
                    new JoinPolicy(JoinKind.Explicit, RequiredExecutionIds: [childExecution]))),
                new ExecutionJoinResolved(execution, resolvedJoinId, [childExecution]),
                new ExecutionJoinFailed(execution, failedJoinId, "fixture join failure"),
                new SupervisionBindingCreated(childExecution, new SupervisionBinding(bindingId, childExecution,
                    execution, 1)),
                new SupervisionBindingAccepted(childExecution, bindingId),
                new SupervisionBindingCreated(childExecution, new SupervisionBinding(failedBindingId, childExecution,
                    execution, 1)),
                new SupervisionBindingFailed(childExecution, failedBindingId, "fixture supervision failure"),
                new ExecutionMailboxCreated(childExecution, new ExecutionMailbox(mailbox, childExecution)),
                new ExecutionMailboxMessageReceived(childExecution, new ExecutionMailboxMessage(messageId, mailbox,
                    execution, content, new EvidenceEventRef(session, receiptEvent.EventId))),
                new ExecutionMailboxMessageAcknowledged(childExecution, mailbox, messageId),
                new WakeRequestCreated(childExecution, new WakeRequest(wakeRequestId, childExecution,
                    new EvidenceEventRef(session, receiptEvent.EventId), "fixture wake", 1, messageId)),
                new WakeRequestAccepted(childExecution, wakeRequestId),
                new WakeRequestResolved(childExecution, wakeRequestId),
                new WakeRequestCreated(childExecution, new WakeRequest(failedWakeRequestId, childExecution,
                    new EvidenceEventRef(session, receiptEvent.EventId), "fixture failed wake", 1)),
                new WakeRequestFailed(childExecution, failedWakeRequestId, "fixture wake failure"),
                new ResultDispositionRecorded(childExecution, new ResultDisposition(DispositionId.New(), childExecution,
                    result, ResultDispositionOutcome.ReworkRequested, execution, "fixture disposition",
                    evidenceRefs)),
                new ValidationStateRecorded(childExecution, new ValidationState(scope, ValidationLevel.Integration,
                    ValidationStatus.Unknown, ["scripted check"], evidenceRefs)),
                new ValidationDebtCreated(childExecution, new ValidationDebt(debtId, childExecution, scope,
                    ValidationLevel.Project, ["fixture pending check"], evidenceRefs)),
                new ValidationDebtResolved(childExecution, debtId, evidenceRefs),
                new IntegrationStatusRecorded(childExecution, new IntegrationStatusRecord(scope,
                    IntegrationStatus.Unknown, evidenceRefs)),
            ];
            Assert.Equal(23, records.Select(record => record.GetType()).Distinct().Count());

            var before = store.ReadFrom(session, 1);
            var baseline = CanonicalStateTracker.Replay(codecs, before).Snapshot();
            Assert.DoesNotContain(before, evt => evt.Type.Value().StartsWith("delegation.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("execution_join.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("supervision_binding.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("execution_mailbox.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("wake_request.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("agent_result.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("result_disposition.", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("validation_", StringComparison.Ordinal)
                || evt.Type.Value().StartsWith("integration_status.", StringComparison.Ordinal));

            foreach (var record in records)
            {
                var isChildExecution = record.ExecutionId == childExecution;
                using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId,
                    isChildExecution ? childTask : run.RootTask,
                    isChildExecution ? childLane : run.RootLane,
                    ExecutionId: record.ExecutionId)))
                {
                    Assert.Equal(1, record.SchemaVersion());
                    Assert.Equal(record.SchemaVersion(), codecs.CurrentVersion(record.Type()));
                    stream.Append(record);
                }
            }

            var appended = store.ReadFrom(session, before.Count + 1);
            Assert.Equal(records.Length, appended.Count);
            for (var i = 0; i < records.Length; i++)
            {
                Assert.Equal(records[i].Type(), appended[i].Type);
                Assert.Equal(records[i].SchemaVersion(), appended[i].SchemaVersion);
                Assert.Equal(TimeSpan.Zero, appended[i].Timestamp.Offset);
                Assert.Equal("OmniCore.Engine.EventStream", appended[i].Source);
                Assert.Equal(run.RunId, appended[i].RunId);
                Assert.Equal(run.RunId, appended[i].CorrelationId);
                var isChildExecution = records[i].ExecutionId == childExecution;
                Assert.Equal(isChildExecution ? childTask : run.RootTask, appended[i].TaskId);
                Assert.Equal(isChildExecution ? childLane : run.RootLane, appended[i].LaneId);
                Assert.Equal(records[i].ExecutionId, appended[i].ExecutionId);

                var recordArtifacts = records[i].RecordArtifacts().Select(artifact => artifact.Id).Distinct().ToArray();
                Assert.Equal(recordArtifacts, appended[i].ArtifactRefs.Select(artifact => artifact.Id).ToArray());

                var decoded = Assert.IsAssignableFrom<IPreM6ContractEvent>(codecs.Decode(appended[i]));
                Assert.Equal(records[i].GetType(), decoded.GetType());
                Assert.True(JsonNode.DeepEquals(
                    JsonNode.Parse(codecs.CodecFor(records[i].Type()).Encode(records[i])),
                    JsonNode.Parse(codecs.CodecFor(decoded.Type()).Encode(decoded))),
                    "The record must preserve all durable fields across encode, append, SQLite reopen, and decode: "
                    + records[i].Type());
                decoded.Validate();
            }

            Release(store);
            store = new SqliteEventStore(journalPath);
            var reopened = store.ReadFrom(session, 1);
            var frozen = reopened.Where(evt => records.Any(record => record.Type().Equals(evt.Type))).ToArray();
            Assert.Equal(records.Length, frozen.Length);
            var tracker = CanonicalStateTracker.Replay(codecs, reopened);
            Assert.Equal(baseline, tracker.Snapshot());
            Assert.Equal(RunState.Running, tracker.Run(run.RunId));
            Assert.Equal(TaskState.Running, tracker.Task(run.RootTask));
            Assert.Equal(LaneState.Running, tracker.Lane(run.RootLane));
            Assert.DoesNotContain(reopened.Select(codecs.Decode), payload => payload is TaskCompleted
                or LaneCompleted or RunCompleted);

            var projection = PreM6RecordProjection.Replay(session, codecs, reopened);
            var histories = projection.Records.Values;
            Assert.Equal(records.Length, histories.Sum(history => history.Facts.Count));
            Assert.Contains(histories, history => history.Phase == PreM6RecordPhase.Returned
                && history.Facts.OfType<DelegationAccepted>().Single().ChildExecutionId == childExecution);
            Assert.Contains(histories, history => history.Phase == PreM6RecordPhase.Resolved
                && history.Facts.OfType<ExecutionJoinResolved>().Any());
            Assert.Contains(histories, history => history.Phase == PreM6RecordPhase.Failed
                && history.Facts.OfType<WakeRequestFailed>().Any());
            Assert.Contains(histories, history => history.Facts.OfType<AgentResultProduced>()
                .Any(fact => fact.ResultRef == result && fact.ResultRevision == 2
                    && fact.SupersedesResultRef == priorResult));
            Assert.Contains(histories, history => history.Facts.OfType<ResultDispositionRecorded>()
                .Any(fact => fact.Disposition.ResultRef == result));
        }
        finally
        {
            if (store is not null) Release(store);
            Directory.Delete(temporaryRoot, recursive: true);
        }

        ArtifactRef Put(string value) => artifacts.PutText(value, "text/plain", ArtifactKind.Other,
            Sensitivity.Normal);
    }

    [Fact]
    public void Unknown_future_version_for_a_frozen_record_is_rejected()
    {
        var codecs = EventCodecs.Create();
        var payload = new WakeRequestFailed(ExecutionId.New(), WakeRequestId.New(), "fixture failure");
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        var current = codecs.CurrentVersion(payload.Type());
        var future = DomainEvent.Create(SessionId.New(), payload.Type(), current + 1, null, null, null,
            null, null, null, null, null, Array.Empty<ArtifactRef>(), json, executionId: payload.ExecutionId);

        Assert.Throws<UnsupportedEventVersionException>(() => codecs.Decode(future));
    }

    [Fact]
    public void Invalid_shapes_are_rejected_before_any_event_is_written()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "omnicore-prem6-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(Path.Combine(temporaryRoot, "journal.db"));
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var stream = new EventStream(store, codecs, session);
            var before = store.ReadFrom(session, 1).Count;
            var execution = ExecutionId.New();
            var duplicateMembers = ExecutionId.New();
            var badArtifact = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256("00"), 1,
                "text/plain", ArtifactKind.Other, Sensitivity.Normal);
            IPreM6ContractEvent[] invalid =
            [
                new DelegationCreated(execution, new Delegation(DelegationId.New(), ExecutionId.New(),
                    TaskId.New(), LaneId.New(), ProfileId.New(), badArtifact, ExecutionRelation.Awaited,
                    ExecutionSupervision.Managed)),
                new ExecutionJoinCreated(execution, new ExecutionJoin(JoinId.New(), execution,
                    [duplicateMembers, duplicateMembers], new JoinPolicy(JoinKind.All))),
                new ExecutionJoinCreated(execution, new ExecutionJoin(JoinId.New(), execution, [execution],
                    new JoinPolicy((JoinKind)999))),
                new WakeRequestCreated(execution, new WakeRequest(WakeRequestId.New(), ExecutionId.New(),
                    new EvidenceEventRef(session, EventId.New()), "fixture target mismatch", 1)),
                new ResultDispositionRecorded(execution, new ResultDisposition(DispositionId.New(), execution,
                    badArtifact, (ResultDispositionOutcome)999, null, "unknown outcome", [])),
                new ValidationStateRecorded(execution, new ValidationState(
                    new ValidationScope(session, LaneId: LaneId.New()), (ValidationLevel)999,
                    ValidationStatus.Unknown, [], [])),
                new ValidationDebtCreated(execution, new ValidationDebt(ValidationDebtId.New(), execution,
                    new ValidationScope(session), ValidationLevel.Project, [], [])),
                new IntegrationStatusRecorded(execution, new IntegrationStatusRecord(new ValidationScope(session),
                    (IntegrationStatus)999, [])),
            ];

            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane,
                ExecutionId: execution)))
            {
                foreach (var record in invalid)
                {
                    Assert.ThrowsAny<ArgumentException>(() => stream.Append(record));
                    Assert.Equal(before, store.ReadFrom(session, 1).Count);
                }
            }
        }
        finally
        {
            if (store is not null) Release(store);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Contract_records_defensively_copy_collections_for_construction_and_with_expressions()
    {
        var memberA = ExecutionId.New();
        var memberB = ExecutionId.New();
        var sourceMembers = new List<ExecutionId> { memberA, memberB };
        var join = new ExecutionJoin(JoinId.New(), ExecutionId.New(), sourceMembers,
            new JoinPolicy(JoinKind.All));
        sourceMembers.Clear();
        Assert.Equal(new[] { memberA, memberB }, join.MemberExecutionIds);

        var replacementMembers = new List<ExecutionId> { memberB };
        var changedJoin = join with { MemberExecutionIds = replacementMembers };
        replacementMembers.Add(memberA);
        Assert.Equal(new[] { memberB }, changedJoin.MemberExecutionIds);

        var originalCheck = new List<string> { "check one" };
        var validation = new ValidationState(new ValidationScope(SessionId.New()), ValidationLevel.Local,
            ValidationStatus.Pending, originalCheck, Array.Empty<EvidenceRef>());
        originalCheck.Add("mutated later");
        Assert.Equal(new[] { "check one" }, validation.RequiredChecks);

        var replacementChecks = new List<string> { "replacement" };
        var changedValidation = validation with { RequiredChecks = replacementChecks };
        replacementChecks.Clear();
        Assert.Equal(new[] { "replacement" }, changedValidation.RequiredChecks);
    }

    private static void Release(SqliteEventStore store)
    {
        var connection = Assert.IsType<SqliteConnection>(store.Connection);
        store.Close();
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }
}
