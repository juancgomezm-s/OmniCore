using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class FanOutIntegrationTests
{
    [Theory]
    [InlineData("Direct")]
    [InlineData("Aggregate")]
    public void Fan_in_waits_for_explicit_acceptance_of_every_member_and_replays_sqlite(string policy)
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"fanout.create\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"delegationIds\":[\"" + first.DelegationId + "\",\""
            + second.DelegationId + "\"],\"policy\":\"" + policy + "\"}");
        var createAck = fx.Server.SendUserAction(create, CancellationToken.None);
        Assert.Equal("ok", createAck.Status);
        var group = Assert.Single(fx.Payloads.OfType<FanOutGroupCreated>()).Group;
        Assert.Equal(new[] { first.DelegationId, second.DelegationId }, group.MemberDelegationIds);

        var firstExecution = Execute(fx, first);
        Assert.True(firstExecution.Status == "ok", firstExecution.Error ?? firstExecution.Outcome?.Reason);
        Assert.Equal("ok", Execute(fx, second).Status);
        var results = fx.Payloads.OfType<AgentResultProduced>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Empty(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal("ok", Accept(fx, first, results.Single(item => item.ExecutionId ==
            fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == first.DelegationId).ChildExecutionId)).Status);
        Assert.Empty(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal("ok", Accept(fx, second, results.Single(item => item.ExecutionId ==
            fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == second.DelegationId).ChildExecutionId)).Status);

        var resolved = Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal(group.GroupId, resolved.GroupId);
        var expectedResults = new[] { first, second }.Select(child =>
        {
            var childId = fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == child.DelegationId).ChildExecutionId;
            return results.Single(item => item.ExecutionId == childId).ResultRef;
        }).ToArray();
        Assert.Equal(expectedResults, resolved.MemberResultRefs);
        var journal = fx.Store.ReadFrom(fx.Session, 1);
        var finalAcceptance = journal.Where(evt => fx.Codecs.Decode(evt) is ResultDispositionRecorded disposition
                && disposition.Disposition.Outcome == ResultDispositionOutcome.Accepted
                && expectedResults.Contains(disposition.Disposition.ResultRef))
            .Last();
        var resolvedEnvelope = journal.Single(evt => evt.Type.Equals(EventType.Of("fanout_group.resolved"))
            && ((FanOutGroupResolved)fx.Codecs.Decode(evt)).GroupId == group.GroupId);
        Assert.Equal(new EventCausation(finalAcceptance.EventId), resolvedEnvelope.Causation);
        var expectedEnvelopeRefs = expectedResults.Append(resolved.AggregateRef).Where(item => item is not null)
            .Select(item => item!).DistinctBy(item => item.Id).ToArray();
        Assert.Equal(expectedEnvelopeRefs, resolvedEnvelope.ArtifactRefs);
        if (policy == "Direct") Assert.Null(resolved.AggregateRef);
        else
        {
            Assert.NotNull(resolved.AggregateRef);
            Assert.True(fx.Artifacts.Verify(resolved.AggregateRef.Hash, resolved.AggregateRef.Size));
            using var aggregate = JsonDocument.Parse(fx.Artifacts.GetText(resolved.AggregateRef.Hash)!);
            Assert.Equal(group.GroupId.ToString(), aggregate.RootElement.GetProperty("groupId").GetString());
            Assert.Equal(2, aggregate.RootElement.GetProperty("members").GetArrayLength());
            Assert.Equal(new[] { first.DelegationId.ToString(), second.DelegationId.ToString() },
                aggregate.RootElement.GetProperty("members").EnumerateArray()
                    .Select(item => item.GetProperty("delegationId").GetString()).ToArray());
        }
        var snapshot = AgentLaneReader.Read(fx.Store, fx.Codecs, fx.Session, fx.Run, fx.Artifacts);
        var inspected = Assert.Single(snapshot.FanOutGroups!);
        Assert.Equal("Resolved", inspected.State);
        Assert.Equal(resolved.AggregateRef?.Id.ToString(), inspected.AggregateId);

        fx.Reopen();
        var reopened = Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal(resolved.MemberResultRefs, reopened.MemberResultRefs);
        Assert.Equal(resolved.AggregateRef, reopened.AggregateRef);
        if (reopened.AggregateRef is { } aggregateRef)
            Assert.True(fx.Artifacts.Verify(aggregateRef.Hash, aggregateRef.Size));
    }

    [Fact]
    public void Rework_keeps_group_pending_then_replacement_resolves_once_with_stable_order()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        var replacementRequest = fx.Request() with { MaxTokens = 18_432 };
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var groupAck = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"fanout.create\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"delegationIds\":[\"" + first.DelegationId + "\",\"" + second.DelegationId + "\"],\"policy\":\"Direct\"}"), CancellationToken.None);
        Assert.Equal("ok", groupAck.Status);
        var group = Assert.Single(fx.Payloads.OfType<FanOutGroupCreated>()).Group;
        Assert.Equal("ok", Execute(fx, first).Status);
        Assert.Equal("ok", Execute(fx, second).Status);
        var firstChild = fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == first.DelegationId).ChildExecutionId;
        var firstResult = fx.Payloads.OfType<AgentResultProduced>().Single(item => item.ExecutionId == firstChild);
        var rework = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
            + first.DelegationId + "\",\"resultId\":\"" + firstResult.ResultRef.Id + "\",\"outcome\":\"ReworkRequested\",\"reason\":\"Añadir evidencia\"}"),
            CancellationToken.None);
        Assert.Equal("ok", rework.Status);
        Assert.Empty(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal("WaitingForRework", Assert.Single(AgentLaneReader.Read(fx.Store, fx.Codecs, fx.Session, fx.Run, fx.Artifacts).FanOutGroups!).State);

        var replacement = Queue(fx, replacementRequest);
        var replaceAck = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"fanout.replace_member\",\"groupId\":\""
            + group.GroupId + "\",\"previousDelegationId\":\"" + first.DelegationId
            + "\",\"replacementDelegationId\":\"" + replacement.DelegationId + "\"}"), CancellationToken.None);
        Assert.Equal("ok", replaceAck.Status);
        Assert.Equal("ok", Execute(fx, replacement).Status);
        var otherChild = fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == second.DelegationId).ChildExecutionId;
        var otherResult = fx.Payloads.OfType<AgentResultProduced>().Single(item => item.ExecutionId == otherChild);
        Assert.Equal("ok", Accept(fx, second, otherResult).Status);
        Assert.Empty(fx.Payloads.OfType<FanOutGroupResolved>());
        var replacementChild = fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == replacement.DelegationId).ChildExecutionId;
        var replacementResult = fx.Payloads.OfType<AgentResultProduced>().Single(item => item.ExecutionId == replacementChild);
        Assert.Equal("ok", Accept(fx, replacement, replacementResult).Status);

        var resolved = Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Equal(new[] { replacementResult.ResultRef, otherResult.ResultRef }, resolved.MemberResultRefs);
        Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
        fx.Reopen();
        Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
    }

    [Theory]
    [InlineData("Direct", false)]
    [InlineData("Aggregate", true)]
    public void Fan_in_policy_evaluator_is_deterministic_and_waits_for_rework(string policy, bool aggregate)
    {
        var a = new DelegationId(Guid.NewGuid());
        var b = new DelegationId(Guid.NewGuid());
        var group = new FanOutGroup(FanOutGroupId.New(), new ExecutionId(Guid.NewGuid()), [a, b], Enum.Parse<FanInPolicy>(policy));
        var waiting = FanInPolicyEvaluator.Evaluate(group, [
            new(a, FanOutMemberStatus.ReworkRequested), new(b, FanOutMemberStatus.Accepted,
                new ArtifactRef(new ArtifactId(Guid.NewGuid()), ContentHash.Sha256(new string('A', 64)), 10, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive))]);
        Assert.False(waiting.Ready);
        Assert.Equal("WaitingForRework", waiting.WaitingReason);
        Assert.Equal(aggregate, waiting.RequiresAggregate);
        var ready = FanInPolicyEvaluator.Evaluate(group, [
            new(a, FanOutMemberStatus.Accepted,
                new ArtifactRef(new ArtifactId(Guid.NewGuid()), ContentHash.Sha256(new string('B', 64)), 11, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive)),
            new(b, FanOutMemberStatus.Accepted,
                new ArtifactRef(new ArtifactId(Guid.NewGuid()), ContentHash.Sha256(new string('C', 64)), 12, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive))]);
        Assert.True(ready.Ready);
        Assert.Equal("Ready", ready.WaitingReason);
        Assert.Equal(2, ready.MemberResults.Count);
    }

    [Fact]
    public void Reopen_reconciles_committed_acceptances_after_resolution_append_failure_without_provider_retry()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"fanout.create\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"delegationIds\":[\"" + first.DelegationId + "\",\"" + second.DelegationId + "\"],\"policy\":\"Direct\"}"), CancellationToken.None).Status);
        Assert.Equal("ok", Execute(fx, first).Status);
        Assert.Equal("ok", Execute(fx, second).Status);
        var executions = fx.Payloads.OfType<DelegationAccepted>().ToDictionary(item => item.DelegationId, item => item.ChildExecutionId);
        var results = fx.Payloads.OfType<AgentResultProduced>().ToDictionary(item => item.ExecutionId);
        var join = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"execution.join\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"members\":[\"" + executions[first.DelegationId] + "\",\""
            + executions[second.DelegationId] + "\"],\"kind\":\"All\"}");
        Assert.Equal("ok", fx.Server.SendUserAction(join, CancellationToken.None).Status);

        fx.UseServerStore(new FailFanOutResolutionStore(fx.Store));
        Assert.Equal("ok", Accept(fx, first, results[executions[first.DelegationId]]).Status);
        var secondAck = Accept(fx, second, results[executions[second.DelegationId]]);
        Assert.NotEqual("ok", secondAck.Status);
        Assert.Equal(2, fx.Payloads.OfType<ResultDispositionRecorded>().Count(item => item.Disposition.Outcome == ResultDispositionOutcome.Accepted));
        Assert.Empty(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());

        var callsBeforeRecovery = fx.ProviderCalls;
        fx.Reopen();
        Assert.Equal(callsBeforeRecovery, fx.ProviderCalls);
        Assert.Single(fx.Payloads.OfType<FanOutGroupResolved>());
        Assert.Single(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal(2, fx.Payloads.OfType<ResultDispositionRecorded>().Count(item => item.Disposition.Outcome == ResultDispositionOutcome.Accepted));
        var journal = fx.Store.ReadFrom(fx.Session, 1);
        var acceptedEvent = journal.Where(evt => fx.Codecs.Decode(evt) is ResultDispositionRecorded disposition
                && disposition.Disposition.Outcome == ResultDispositionOutcome.Accepted)
            .Last();
        var joinResolvedEnvelope = journal.Single(evt => evt.Type.Equals(EventType.Of("execution_join.resolved")));
        Assert.Equal(new EventCausation(acceptedEvent.EventId), joinResolvedEnvelope.Causation);
    }

    [Fact]
    public void Accepted_join_does_not_unblock_owner_after_its_execution_terminated()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        Assert.Equal("ok", Execute(fx, first).Status);
        Assert.Equal("ok", Execute(fx, second).Status);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var children = fx.Payloads.OfType<DelegationAccepted>().Select(item => item.ChildExecutionId).ToArray();
        var join = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"execution.join\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"members\":[\"" + children[0] + "\",\"" + children[1] + "\"],\"kind\":\"All\"}");
        Assert.Equal("ok", fx.Server.SendUserAction(join, CancellationToken.None).Status);
        var rootLane = fx.Payloads.OfType<LaneCreated>().Single(item => item.LaneId == root.LaneId);
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run, rootLane.TaskId, rootLane.LaneId,
            ExecutionId: root.ExecutionId)))
            new EventStream(fx.Store, fx.Codecs, fx.Session).Append(new AgentExecutionCompleted(root.ExecutionId,
                root.LaneId, rootLane.AgentProfile, null, root.Relation, root.Supervision), DurabilityClass.Barrier);

        foreach (var delegation in new[] { first, second })
        {
            var execution = fx.Payloads.OfType<DelegationAccepted>().Single(item => item.DelegationId == delegation.DelegationId).ChildExecutionId;
            var result = fx.Payloads.OfType<AgentResultProduced>().Single(item => item.ExecutionId == execution);
            Assert.Equal("ok", Accept(fx, delegation, result).Status);
        }
        Assert.Single(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1).Where(item => item.RunId == fx.Run).ToArray()).Get(fx.RootTask)!.State);
        Assert.Equal(LaneState.Blocked, LaneProjection.Replay(fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1).Where(item => item.RunId == fx.Run).ToArray()).StateOf(rootLane.LaneId));
        Assert.Empty(fx.Payloads.OfType<TaskUnblocked>());
        Assert.Empty(fx.Payloads.OfType<LaneUnblocked>());
    }

    [Fact]
    public void Cancelling_last_join_recovers_unblock_window_with_join_event_causation()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        Assert.Equal("ok", Execute(fx, first).Status);
        Assert.Equal("ok", Execute(fx, second).Status);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var executions = fx.Payloads.OfType<DelegationAccepted>().Select(item => item.ChildExecutionId).ToArray();
        Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"execution.join\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"members\":[\"" + executions[0] + "\",\"" + executions[1] + "\"],\"kind\":\"All\"}"), CancellationToken.None).Status);
        var join = Assert.Single(fx.Payloads.OfType<ExecutionJoinCreated>()).Join;
        fx.UseServerStore(new FailJoinUnblockStore(fx.Store));
        var cancel = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"execution.join.cancel\",\"joinId\":\""
            + join.JoinId + "\"}"), CancellationToken.None);
        Assert.NotEqual("ok", cancel.Status);
        Assert.Single(fx.Payloads.OfType<ExecutionJoinFailed>());
        Assert.Empty(fx.Payloads.OfType<TaskUnblocked>());

        fx.Reopen();
        Assert.Single(fx.Payloads.OfType<TaskUnblocked>());
        Assert.Single(fx.Payloads.OfType<LaneUnblocked>());
        var failedEnvelope = fx.Store.ReadFrom(fx.Session, 1).Single(evt => fx.Codecs.Decode(evt) is ExecutionJoinFailed);
        foreach (var evt in fx.Store.ReadFrom(fx.Session, 1).Where(item =>
            fx.Codecs.Decode(item) is TaskUnblocked or LaneUnblocked))
            Assert.Equal(new EventCausation(failedEnvelope.EventId), evt.Causation);
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1).Where(item => item.RunId == fx.Run).ToArray()).Get(fx.RootTask)!.State);
    }

    [Fact]
    public void Fan_out_rejects_untrusted_origin_and_membership_reuse()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        static WireEnvelope Create(ExecutionId owner, DelegationId a, DelegationId b) => WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"fanout.create\",\"ownerExecutionId\":\"" + owner + "\",\"delegationIds\":[\""
            + a + "\",\"" + b + "\"],\"policy\":\"Direct\"}");

        var untrusted = fx.Server.Send(Create(root.ExecutionId, first.DelegationId, second.DelegationId), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, untrusted.Outcome?.Kind);
        Assert.Empty(fx.Payloads.OfType<FanOutGroupCreated>());
        Assert.Equal("ok", fx.Server.SendUserAction(Create(root.ExecutionId, first.DelegationId, second.DelegationId), CancellationToken.None).Status);
        var reused = fx.Server.SendUserAction(Create(root.ExecutionId, first.DelegationId, second.DelegationId), CancellationToken.None);
        Assert.Equal("error", reused.Status);
        Assert.Single(fx.Payloads.OfType<FanOutGroupCreated>());
    }

    [Fact]
    public void Fan_out_replay_rejects_bad_scope_replacement_and_early_resolution_without_appending()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var first = Queue(fx);
        var second = Queue(fx);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var groupAck = fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"fanout.create\",\"ownerExecutionId\":\""
            + root.ExecutionId + "\",\"delegationIds\":[\"" + first.DelegationId + "\",\"" + second.DelegationId + "\"],\"policy\":\"Direct\"}"), CancellationToken.None);
        Assert.Equal("ok", groupAck.Status);
        var group = Assert.Single(fx.Payloads.OfType<FanOutGroupCreated>()).Group;
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);

        var beforeScope = fx.Store.CurrentSequence(fx.Session);
        using (ExecutionScope.Begin(new ExecutionScopeState(new RunId(Guid.NewGuid()), ExecutionId: root.ExecutionId)))
            Assert.Throws<InvalidStateTransitionException>(() => stream.Append(new FanOutGroupMemberReplaced(root.ExecutionId,
                group.GroupId, first.DelegationId, new DelegationId(Guid.NewGuid())), DurabilityClass.Barrier));
        Assert.Equal(beforeScope, fx.Store.CurrentSequence(fx.Session));

        var beforeReplacement = fx.Store.CurrentSequence(fx.Session);
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run, root.LaneId is null ? null : fx.RootTask,
            root.LaneId, ExecutionId: root.ExecutionId)))
            Assert.Throws<InvalidStateTransitionException>(() => stream.Append(new FanOutGroupMemberReplaced(root.ExecutionId,
                group.GroupId, new DelegationId(Guid.NewGuid()), new DelegationId(Guid.NewGuid())), DurabilityClass.Barrier));
        Assert.Equal(beforeReplacement, fx.Store.CurrentSequence(fx.Session));

        static ArtifactRef FakeRef(char hash) => new(new ArtifactId(Guid.NewGuid()), ContentHash.Sha256(new string(char.ToLowerInvariant(hash), 64)),
            1, "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        var beforeResolution = fx.Store.CurrentSequence(fx.Session);
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run, fx.RootTask, fx.Lane, ExecutionId: root.ExecutionId)))
            Assert.Throws<InvalidStateTransitionException>(() => stream.Append(new FanOutGroupResolved(root.ExecutionId,
                group.GroupId, [FakeRef('A'), FakeRef('B')], null), DurabilityClass.Barrier));
        Assert.Equal(beforeResolution, fx.Store.CurrentSequence(fx.Session));
    }

    private static Delegation Queue(DelegationAdmissionTests.Fixture fx)
        => Queue(fx, fx.Request() with { MaxTokens = 18_432 });

    private static Delegation Queue(DelegationAdmissionTests.Fixture fx, DelegationCreateRequest request)
    {
        var ack = fx.Server.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), CancellationToken.None);
        Assert.True(ack.Status == "ok", ack.Error ?? ack.Outcome?.Reason);
        return fx.Payloads.OfType<DelegationCreated>().Last().Delegation;
    }

    private static CommandAck Execute(DelegationAdmissionTests.Fixture fx, Delegation child) => fx.Server.ExecuteDelegation(
        child.DelegationId, (work, token) => fx.Explorer((_, _) =>
        {
            fx.ProviderCalls++;
            return new ModelResponse([new TextBlock("Respuesta de " + work.Delegation.DelegationId)], StopReason.EndTurn,
                new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
        }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token), CancellationToken.None);

    private static CommandAck Accept(DelegationAdmissionTests.Fixture fx, Delegation child, AgentResultProduced result)
        => fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"delegation.disposition\",\"delegationId\":\""
            + child.DelegationId + "\",\"resultId\":\"" + result.ResultRef.Id
            + "\",\"outcome\":\"Accepted\",\"reason\":\"Resultado verificado\"}"), CancellationToken.None);

    private sealed class FailFanOutResolutionStore(IEventStore inner) : IEventStore
    {
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (evt.Type.Equals(EventType.Of("fanout_group.resolved"))) throw new IOException("simulated resolution append failure");
            inner.Append(session, evt, durability, cancellationToken);
        }
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.AppendBatch(session, events, durability, cancellationToken);
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }

    private sealed class FailJoinUnblockStore(IEventStore inner) : IEventStore
    {
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken)
            => inner.Append(session, evt, durability, cancellationToken);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (events.Any(evt => evt.Type.Equals(EventType.Of("task.unblocked"))))
                throw new IOException("simulated join-unblock append failure");
            inner.AppendBatch(session, events, durability, cancellationToken);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long from) => inner.ReadFrom(session, from);
    }
}
