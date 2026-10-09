using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Client;
using OmniCore.Abstractions;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class DelegationLifecycleTests
{
    [Fact]
    public void Real_child_returns_immutable_result_and_waits_for_explicit_acceptance_across_reopen()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var child = Queue(fx);
        var ack = Execute(fx, child);
        Assert.Equal("ok", ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        var accepted = Assert.Single(fx.Payloads.OfType<DelegationAccepted>());
        var produced = Assert.Single(fx.Payloads.OfType<AgentResultProduced>());
        Assert.Equal(accepted.ChildExecutionId, produced.ExecutionId);
        Assert.Single(fx.Payloads.OfType<SupervisionBindingAccepted>());
        Assert.Single(fx.Payloads.OfType<AgentExecutionCompleted>());
        Assert.Single(fx.Payloads.OfType<DelegationReturned>());
        Assert.Empty(fx.Payloads.OfType<ResultDispositionRecorded>());
        Assert.DoesNotContain(fx.Payloads.OfType<TaskCompleted>(), e => e.TaskId == child.ChildTaskId);
        var step = fx.Store.ReadFrom(fx.Session, 1).Last(e => e.LaneId == child.ChildLaneId && fx.Codecs.Decode(e) is ModelStepStarted);
        Assert.Equal(produced.ExecutionId, step.ExecutionId);
        Assert.Equal(child.ChildTaskId, step.TaskId);
        using var snapshot = JsonDocument.Parse(fx.Artifacts.GetText(((ModelStepStarted)fx.Codecs.Decode(step)).ContextSnapshotRef!.Hash)!);
        Assert.Contains(snapshot.RootElement.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("content").GetString()!.Contains("Información española", StringComparison.Ordinal));
        var resultHash = produced.ResultRef.Hash;
        fx.Reopen();
        var before = fx.Store.CurrentSequence(fx.Session);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, Execute(fx, child).Outcome?.Kind);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Equal("ok", Disposition(fx, child, produced).Status);
        Assert.Null(fx.Payloads.OfType<ResultDispositionRecorded>().Single().Disposition.EvaluatorExecutionId);
        Assert.Equal(resultHash, fx.Payloads.OfType<AgentResultProduced>().Single().ResultRef.Hash);
        Assert.Equal(TaskState.Completed, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(child.ChildTaskId)!.State);
        Assert.Equal(LaneState.Completed, LaneProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).StateOf(child.ChildLaneId));
        before = fx.Store.CurrentSequence(fx.Session);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, Disposition(fx, child, produced).Outcome?.Kind);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
    }

    [Fact]
    public void Queue_is_FIFO_and_individual_cancellation_never_cancels_root_or_sibling()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask();
        var first = Queue(fx); var second = Queue(fx);
        Assert.Equal("QueuePredecessor", Execute(fx, second).Outcome?.Reason);
        Assert.Equal(1, fx.ProviderCalls);
        var untrusted = fx.Server.Send(Command("delegation.cancel", "\"delegationId\":\"" + first.DelegationId + "\""), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, untrusted.Outcome?.Kind);
        Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + first.DelegationId + "\"").Status);
        Assert.Equal(TaskState.Cancelled, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(first.ChildTaskId)!.State);
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
        Assert.Equal("ok", Execute(fx, second).Status);
        Assert.Equal(2, fx.ProviderCalls);
        Assert.Empty(fx.Payloads.OfType<RunCancelled>());
    }

    [Fact]
    public async System.Threading.Tasks.Task Capacity_is_real_and_cancel_owned_worker_finishes_at_safe_boundary()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx);
        using var entered = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId, (_, token) => {
            entered.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException();
        }, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            var root = fx.Server.ExecuteExplorerTurn(fx.Session, fx.Run, _ => throw new InvalidOperationException("must not execute"), CancellationToken.None);
            Assert.Equal("WaitingForCapacity", root.Ack.Outcome?.Reason);
            Assert.Equal("WaitingForCapacity", Execute(fx, child).Outcome?.Reason);
            Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + child.DelegationId + "\"").Status);
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Single(fx.Payloads.OfType<AgentExecutionFailed>());
            var intent = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), e => fx.Codecs.Decode(e) is DelegationCancellationRequested);
            Assert.IsType<CommandCausation>(intent.Causation);
            Assert.Equal(child.ParentExecutionId, intent.ExecutionId);
            Assert.Single(fx.Payloads.OfType<DelegationFailed>());
            Assert.Empty(fx.Payloads.OfType<AgentResultProduced>());
            Assert.Empty(fx.Payloads.OfType<RunCancelled>());
            Assert.Equal(TaskState.Cancelled, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(child.ChildTaskId)!.State);
        }
        finally { Send(fx, "delegation.cancel", "\"delegationId\":\"" + child.DelegationId + "\""); }
    }

    [Theory]
    [InlineData("All", 2)]
    [InlineData("Any", 1)]
    [InlineData("Quorum", 2)]
    [InlineData("Explicit", 1)]
    public void Joins_suspend_parent_until_policy_is_satisfied_by_accepted_not_merely_completed_results(string kind, int required)
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask();
        var first = Queue(fx); var second = Queue(fx);
        Assert.Equal("ok", Execute(fx, first).Status); Assert.Equal("ok", Execute(fx, second).Status);
        var starts = fx.Payloads.OfType<AgentExecutionStarted>().Where(e => e.ParentExecutionId is not null).ToArray();
        var extra = kind == "Quorum" ? ",\"requiredCount\":2" : kind == "Explicit" ? ",\"requiredExecutionIds\":[\"" + starts[0].ExecutionId + "\"]" : "";
        var ack = Send(fx, "execution.join", "\"ownerExecutionId\":\"" + first.ParentExecutionId + "\",\"kind\":\"" + kind
            + "\",\"members\":[\"" + starts[0].ExecutionId + "\",\"" + starts[1].ExecutionId + "\"]" + extra);
        Assert.Equal("ok", ack.Status);
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
        fx.Reopen();
        Assert.Equal("ok", Disposition(fx, first, fx.Payloads.OfType<AgentResultProduced>().Single(e => e.ExecutionId == starts[0].ExecutionId)).Status);
        if (required == 2)
        {
            Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
            Assert.Equal("ok", Disposition(fx, second, fx.Payloads.OfType<AgentResultProduced>().Single(e => e.ExecutionId == starts[1].ExecutionId)).Status);
        }
        var resolved = Assert.Single(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal(required, resolved.SatisfyingExecutionIds.Count);
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
        Assert.Equal(LaneState.Running, LaneProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).StateOf(fx.Lane));
        Assert.DoesNotContain(fx.Payloads.OfType<TaskCompleted>(), e => e.TaskId == fx.RootTask);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("ReworkRequested")]
    public void Negative_disposition_leaves_join_waiting_until_explicit_cancellation(string outcome)
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx); Execute(fx, child);
        var execution = fx.Payloads.OfType<DelegationAccepted>().Single().ChildExecutionId;
        Assert.Equal("ok", Send(fx, "execution.join", "\"ownerExecutionId\":\"" + child.ParentExecutionId
            + "\",\"kind\":\"All\",\"members\":[\"" + execution + "\"]").Status);
        var produced = fx.Payloads.OfType<AgentResultProduced>().Single();
        Assert.Equal("ok", Disposition(fx, child, produced, outcome).Status);
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(child.ChildTaskId)!.State);
        var join = fx.Payloads.OfType<ExecutionJoinCreated>().Single().Join;
        Assert.Equal("ok", Send(fx, "execution.join.cancel", "\"joinId\":\"" + join.JoinId + "\"").Status);
        Assert.Single(fx.Payloads.OfType<ExecutionJoinFailed>());
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
        Assert.NotEqual("ok", Disposition(fx, child, produced).Status);
    }

    [Fact]
    public void Tiny_child_budget_prevents_provider_call_and_invalid_result_identity_cannot_complete_task()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx, 1);
        var ack = Execute(fx, child);
        Assert.NotEqual("ok", ack.Status);
        Assert.Equal(1, fx.ProviderCalls);
        Assert.Empty(fx.Payloads.OfType<AgentResultProduced>());
        Assert.Single(fx.Payloads.OfType<DelegationFailed>());
    }

    [Fact]
    public void Returned_but_unaccepted_task_can_be_cancelled_without_rewriting_executor_completion()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx); Execute(fx, child);
        Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + child.DelegationId + "\"").Status);
        Assert.Single(fx.Payloads.OfType<AgentExecutionCompleted>());
        Assert.Empty(fx.Payloads.OfType<AgentExecutionFailed>());
        Assert.Equal(ResultDispositionOutcome.Rejected, fx.Payloads.OfType<ResultDispositionRecorded>().Single().Disposition.Outcome);
        Assert.Equal(TaskState.Cancelled, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1)).Get(child.ChildTaskId)!.State);
        Assert.NotEqual("ok", Disposition(fx, child, fx.Payloads.OfType<AgentResultProduced>().Single()).Status);
    }

    [Fact]
    public void Changed_profile_and_revoked_authority_block_dispatch_without_invoking_or_starting_child()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx);
        var changed = new AgentProfile(fx.Profile.Id, fx.Profile.Name, 2, fx.Profile.PermissionCeiling, fx.Profile.PreferredTools);
        fx.Server.ConfigureAgentProfiles(new(new AgentProfileRegistry([changed]), changed));
        var before = fx.Store.CurrentSequence(fx.Session);
        Assert.NotEqual("ok", Execute(fx, child).Status);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Equal(1, fx.ProviderCalls);
        fx.Server.ConfigureAgentProfiles(new(new AgentProfileRegistry([fx.Profile]), fx.Profile));
        Assert.Equal("ok", Send(fx, "run.mode.select", "\"mode\":\"plan\"").Status);
        Assert.Equal("CoordinationLimitsUnavailable", Execute(fx, child).Outcome?.Reason);
        Assert.Empty(fx.Payloads.OfType<DelegationAccepted>());
    }

    [Fact]
    public void Foreign_members_and_stale_result_identity_never_block_parent_or_accept_result()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask(); var child = Queue(fx); Execute(fx, child);
        var before = fx.Store.CurrentSequence(fx.Session);
        Assert.NotEqual("ok", Send(fx, "execution.join", "\"ownerExecutionId\":\"" + child.ParentExecutionId
            + "\",\"kind\":\"All\",\"members\":[\"" + ExecutionId.New() + "\"]").Status);
        Assert.NotEqual("ok", Send(fx, "delegation.disposition", "\"delegationId\":\"" + child.DelegationId
            + "\",\"resultId\":\"" + Guid.NewGuid() + "\",\"outcome\":\"Accepted\",\"reason\":\"Stale\"").Status);
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinCreated>());
        Assert.Empty(fx.Payloads.OfType<ResultDispositionRecorded>());
    }

    [Fact]
    public void Executor_terminal_metadata_cannot_be_rebound_before_result_acceptance()
    {
        using var fx = new DelegationAdmissionTests.Fixture(); fx.Ask();
        var root = fx.Payloads.OfType<AgentExecutionStarted>().Single();
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fx.Run, fx.RootTask, fx.Lane, ExecutionId: root.ExecutionId));
        var before = fx.Store.CurrentSequence(fx.Session);
        Assert.Throws<InvalidStateTransitionException>(() => new EventStream(fx.Store, fx.Codecs, fx.Session).Append(
            new AgentExecutionCompleted(root.ExecutionId, fx.Lane, ProfileId.New(), null, root.Relation, root.Supervision), DurabilityClass.Barrier));
        Assert.Equal(before, fx.Store.CurrentSequence(fx.Session));
    }

    private static Delegation Queue(DelegationAdmissionTests.Fixture fx, long tokens = 16384)
    {
        var ack = fx.Server.SendUserAction(DelegationCommands.Create(fx.Request() with { MaxTokens = tokens }, Ids.NewV7()), CancellationToken.None);
        Assert.Equal("ok", ack.Status); Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        return fx.Payloads.OfType<DelegationCreated>().Last().Delegation;
    }
    private static CommandAck Execute(DelegationAdmissionTests.Fixture fx, Delegation child) => fx.Server.ExecuteDelegation(child.DelegationId,
        (work, token) => fx.Explorer().Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token), CancellationToken.None);
    private static WireEnvelope Command(string name, string fields) => WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"" + name + "\"," + fields + "}");
    private static CommandAck Send(DelegationAdmissionTests.Fixture fx, string name, string fields) => fx.Server.SendUserAction(Command(name, fields), CancellationToken.None);
    private static CommandAck Disposition(DelegationAdmissionTests.Fixture fx, Delegation child, AgentResultProduced produced, string outcome = "Accepted") =>
        Send(fx, "delegation.disposition", "\"delegationId\":\"" + child.DelegationId + "\",\"resultId\":\"" + produced.ResultRef.Id
            + "\",\"outcome\":\"" + outcome + "\",\"reason\":\"Revisión explícita del usuario\"");
}
