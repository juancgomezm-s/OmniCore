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
            using var rootWriterEntered = new ManualResetEventSlim();
            var queuedRootWriter = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteExplorerTurn(fx.Session, fx.Run,
                _ =>
                {
                    rootWriterEntered.Set();
                    return new ExplorerTurn.TurnResult("writer after readers", StopReason.EndTurn, 0,
                        new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
                }, CancellationToken.None, readOnlyLane: false, waitForCapacity: true));
            await System.Threading.Tasks.Task.Delay(150, TestContext.Current.CancellationToken);
            Assert.False(rootWriterEntered.IsSet, "el root writer espera mientras la lane lectora conserva su lease");
            Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + child.DelegationId + "\"").Status);
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var rootWrite = await queuedRootWriter.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(rootWriterEntered.IsSet);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, rootWrite.Ack.Outcome?.Kind);
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

    [Fact]
    public void Scoped_supervisor_uses_only_IOmniClient_handshake_and_cannot_be_forged_by_wire_origin()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var child = Queue(fx);
        Assert.Equal("ok", Execute(fx, child).Status);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), e => e.ParentExecutionId is null);
        var produced = Assert.Single(fx.Payloads.OfType<AgentResultProduced>());
        var forged = fx.Server.Send(Command("supervisor.result.disposition",
            "\"executionId\":\"" + produced.ExecutionId + "\",\"resultId\":\"" + produced.ResultRef.Id
            + "\",\"outcome\":\"Accepted\",\"reason\":\"forged\",\"origin\":\"Supervisor\""), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, forged.Outcome?.Kind);
        Assert.Empty(fx.Payloads.OfType<ResultDispositionRecorded>());

        var client = SupervisorClientFactory.Create(fx.Server, root.ExecutionId);
        Assert.IsAssignableFrom<IOmniClient>(client);
        Assert.False(client is ITrustedUserActionClient);
        var reviewed = new InProcessSupervisor(client, root.ExecutionId).ReviewReturnedResults(CancellationToken.None);
        Assert.Equal(1, reviewed);
        var disposition = Assert.Single(fx.Payloads.OfType<ResultDispositionRecorded>()).Disposition;
        Assert.Equal(root.ExecutionId, disposition.EvaluatorExecutionId);
        Assert.Equal(ResultDispositionOutcome.ReworkRequested, disposition.Outcome);
        Assert.Contains("faltan integración verificada", disposition.Reason);
        Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1))
            .Get(child.ChildTaskId)!.State);
    }

    [Fact]
    public async System.Threading.Tasks.Task Lost_supervisor_after_handshake_waits_for_live_child_and_preserves_pending_join_across_reopen()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        var providerCallsBeforeChild = fx.ProviderCalls;
        var child = Queue(fx);
        using var providerEntered = new ManualResetEventSlim();
        using var releaseProvider = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId,
            (work, token) => fx.Explorer((_, providerToken) => {
                Interlocked.Increment(ref fx.ProviderCalls);
                providerEntered.Set();
                releaseProvider.Wait(providerToken);
                return new ModelResponse([new TextBlock("Respuesta española")], StopReason.EndTurn,
                    new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(providerEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), e => e.ParentExecutionId is null);
            var childExecution = Assert.Single(fx.Payloads.OfType<DelegationAccepted>()).ChildExecutionId;
            Assert.Equal("ok", Send(fx, "execution.join", "\"ownerExecutionId\":\"" + root.ExecutionId
                + "\",\"kind\":\"All\",\"members\":[\"" + childExecution + "\"]").Status);
            var terminal = new AgentExecutionFailed(root.ExecutionId, root.LaneId, root.ProfileId,
                root.ParentExecutionId, root.Relation, root.Supervision);
            using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run, fx.RootTask, fx.Lane,
                ExecutionId: root.ExecutionId)))
                new EventStream(fx.Store, fx.Codecs, fx.Session).Append(terminal, DurabilityClass.Barrier);
            var terminalEvent = fx.Store.ReadFrom(fx.Session, 1).Last(e => fx.Codecs.Decode(e) is AgentExecutionFailed failed && failed == terminal);

            releaseProvider.Set();
            var ack = await dispatch.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal("error", ack.Status);
            Assert.Equal(providerCallsBeforeChild + 1, fx.ProviderCalls);
            var bindingFailure = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), e => fx.Codecs.Decode(e) is SupervisionBindingFailed);
            Assert.Equal(new EventCausation(terminalEvent.EventId), bindingFailure.Causation);
            Assert.DoesNotContain(fx.Payloads.OfType<AgentExecutionFailed>(), e => e.ExecutionId == childExecution);
            Assert.Empty(fx.Payloads.OfType<DelegationFailed>());
            Assert.Empty(fx.Payloads.OfType<DelegationReturned>());
            Assert.Single(fx.Payloads.OfType<ExecutionJoinCreated>());
            Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
            Assert.Empty(fx.Payloads.OfType<ExecutionJoinFailed>());
            Assert.Equal(PreM6RecordPhase.Accepted, PreM6RecordProjection.Replay(fx.Session, fx.Codecs,
                fx.Store.ReadFrom(fx.Session, 1)).Records["delegation:" + child.DelegationId].Phase);
            Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1))
                .Get(child.ChildTaskId)!.State);
            Assert.Equal(LaneState.Blocked, LaneProjection.Replay(fx.Codecs, fx.Store.ReadFrom(fx.Session, 1))
                .StateOf(child.ChildLaneId));

            fx.Reopen();
            Assert.Throws<InvalidOperationException>(() => SupervisorClientFactory.Create(fx.Server, root.ExecutionId));
            Assert.Single(fx.Payloads.OfType<SupervisionBindingFailed>());
            Assert.Empty(fx.Payloads.OfType<DelegationFailed>());
            Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
            Assert.Equal(providerCallsBeforeChild + 1, fx.ProviderCalls);
        }
        finally
        {
            releaseProvider.Set();
            if (!dispatch.IsCompleted) await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Mailbox_receive_setup_failure_fails_the_wake_and_removes_the_registered_waiter()
    {
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true);
        fx.Ask();
        var child = Queue(fx);
        var waiterRemoved = false;
        ToolCallId? receiveCall = null;
        var ack = fx.Server.ExecuteDelegation(child.DelegationId, (work, token) =>
        {
            var execution = work.Scope.ExecutionId!;
            var rootExecution = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null)
                .ExecutionId;
            var mailbox = Assert.Single(fx.Payloads.OfType<ExecutionMailboxCreated>(), item => item.ExecutionId == execution)
                .Mailbox;
            var source = fx.Source();
            var messageId = MailboxMessageId.New();
            var absentContent = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('0', 64)), 4,
                "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            var turn = TurnId.New();
            var toolCall = ToolCallId.New();
            receiveCall = toolCall;
            var scope = work.Scope with { TurnId = turn, ToolCallId = toolCall };
            using (ExecutionScope.Begin(scope))
            {
                var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
                stream.Append(new ExecutionMailboxMessageReceived(execution, new ExecutionMailboxMessage(messageId,
                    mailbox.MailboxId, rootExecution, absentContent, new EvidenceEventRef(fx.Session, source.EventId))),
                    DurabilityClass.Barrier);
                stream.AppendBatch(new DomainEventPayload[] {
                    new TurnStarted(turn, child.ChildLaneId),
                    new ToolCallRequested(toolCall, "receive-fixture", "core.agents.mailbox.receive", "{}"),
                    new ToolCallPrepared(toolCall, "{}"),
                    new PermissionEvaluated(toolCall, PermissionDecision.Allow, "{}", null),
                    new ToolCallAuthorized(toolCall),
                    new ToolCallStarted(toolCall, EffectClass.None, null),
                }, DurabilityClass.Barrier);
                try
                {
                    fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run, fx.MailboxRoute, BillingMode.Local,
                        toolCall, token).GetAwaiter().GetResult();
                    throw new Xunit.Sdk.XunitException("Expected the missing content artifact to fail setup.");
                }
                catch (InvalidDataException)
                {
                    waiterRemoved = !fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, execution);
                    throw;
                }
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal("error", ack.Status);
        Assert.True(waiterRemoved);
        var accepted = Assert.Single(fx.Payloads.OfType<WakeRequestAccepted>());
        var failedEvent = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is WakeRequestFailed);
        Assert.Equal(accepted.WakeRequestId, ((WakeRequestFailed)fx.Codecs.Decode(failedEvent)).WakeRequestId);
        var acceptedEvent = fx.Store.ReadFrom(fx.Session, 1).Single(evt => fx.Codecs.Decode(evt) is WakeRequestAccepted item
            && item.WakeRequestId == accepted.WakeRequestId);
        Assert.Equal(new EventCausation(acceptedEvent.EventId), failedEvent.Causation);
        Assert.NotNull(receiveCall);
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
    }

    [Fact]
    public async System.Threading.Tasks.Task Scoped_mailbox_send_wakes_the_live_receive_and_acknowledges_after_tool_success_once()
    {
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true);
        fx.Ask();
        var child = Queue(fx);
        var providerCalls = 0;
        var mailboxToolCalls = 0;
        using var receiveEntered = new ManualResetEventSlim();
        using var deliveryReady = new ManualResetEventSlim();
        using var allowSuccessReceipt = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId,
            (work, token) => fx.ExplorerWithMailbox((_, _) =>
            {
                var call = Interlocked.Increment(ref providerCalls);
                return call <= 2
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "mailbox-call-" + call,
                        "core.agents.mailbox.receive", "{}")],
                        StopReason.ToolUse, new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("mensaje recibido")], StopReason.EndTurn,
                        new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }, async (toolCall, receiveToken) =>
            {
                receiveEntered.Set();
                var result = await fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run, fx.MailboxRoute,
                    BillingMode.Local, toolCall, receiveToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref mailboxToolCalls) == 1)
                    throw new IOException("simulated interruption after wake but before ToolCallSucceeded");
                deliveryReady.Set();
                Assert.True(allowSuccessReceipt.Wait(TimeSpan.FromMinutes(2), receiveToken));
                return result;
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(receiveEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var childExecution = Assert.Single(fx.Payloads.OfType<DelegationAccepted>()).ChildExecutionId;
        var hasWaiter = SpinWait.SpinUntil(() => fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, childExecution),
            TimeSpan.FromSeconds(10));
        Assert.True(hasWaiter, "dispatch=" + dispatch.Status + "; failures=" + string.Join(" | ", fx.Payloads.OfType<ToolCallFailed>().Select(item => item.Cause))
            + "; calls=" + string.Join(" | ", fx.Store.ReadFrom(fx.Session, 1).Where(evt => evt.ToolCallId is not null)
                .Select(evt => evt.Type + ":" + evt.ToolCallId + ":" + evt.RunId + ":" + evt.TaskId + ":" + evt.LaneId + ":" + evt.TurnId + ":" + evt.ExecutionId))
            + "; events=" + string.Join(',', fx.Payloads.Select(item => item.GetType().Name)));
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var source = fx.Source();
        var supervisor = SupervisorClientFactory.Create(fx.Server, root.ExecutionId);
        var sent = supervisor.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"supervisor.mailbox.send\","
            + "\"targetExecutionId\":\"" + childExecution + "\",\"sourceEventId\":\"" + source.EventId
            + "\",\"content\":\"continuar desde el mensaje\"}"), TestContext.Current.CancellationToken);
        Assert.Equal("ok", sent.Status);

        Assert.True(deliveryReady.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var (recoveryStore, recoveryServer) = fx.OpenSnapshotServer();
        try
        {
            var journalAtUncertainBoundary = recoveryStore.ReadFrom(fx.Session, 1);
            var receiveRequestsAtBoundary = journalAtUncertainBoundary.Where(evt => fx.Codecs.Decode(evt) is ToolCallRequested request
                && request.ToolName == "core.agents.mailbox.receive").ToArray();
            Assert.Equal(2, receiveRequestsAtBoundary.Length);
            var secondCallId = receiveRequestsAtBoundary[1].ToolCallId;
            Assert.Contains(journalAtUncertainBoundary, evt => evt.ToolCallId == secondCallId
                && fx.Codecs.Decode(evt) is ToolCallStarted);
            Assert.DoesNotContain(journalAtUncertainBoundary, evt => evt.ToolCallId == secondCallId
                && fx.Codecs.Decode(evt) is ToolCallSucceeded or ToolCallEffectUnknown);
            Assert.Contains(journalAtUncertainBoundary, evt => evt.ToolCallId == secondCallId
                && fx.Codecs.Decode(evt) is ToolCallFailed);
            Assert.Empty(journalAtUncertainBoundary.Select(fx.Codecs.Decode).OfType<ExecutionMailboxMessageAcknowledged>());
            Assert.Empty(journalAtUncertainBoundary.Select(fx.Codecs.Decode).OfType<WakeRequestResolved>());
            Assert.Equal(2, journalAtUncertainBoundary.Select(fx.Codecs.Decode).OfType<WakeRequestFailed>().Count());
            var recoveryLane = Assert.Single(AgentsJson.Decode(recoveryServer.Query("agents", CancellationToken.None)!.Json)!.Lanes,
                item => item.LaneId == child.ChildLaneId.ToString());
            Assert.Equal(1, recoveryLane.PendingMailboxMessages);
            Assert.Equal(0, recoveryLane.PendingWakeRequests);
            Assert.Equal(2, Volatile.Read(ref providerCalls));
        }
        finally
        {
            allowSuccessReceipt.Set();
            DelegationAdmissionTests.Fixture.CloseAdditionalStore(recoveryStore);
        }

        var completed = await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal("ok", completed.Status);
        Assert.True(deliveryReady.IsSet);
        Assert.Equal(3, Volatile.Read(ref providerCalls));
        Assert.Equal(2, Volatile.Read(ref mailboxToolCalls));
        var receiveRequests = fx.Store.ReadFrom(fx.Session, 1).Where(evt => fx.Codecs.Decode(evt) is ToolCallRequested request
            && request.ToolName == "core.agents.mailbox.receive").ToArray();
        Assert.Equal(2, receiveRequests.Length);
        Assert.NotEqual(receiveRequests[0].ToolCallId, receiveRequests[1].ToolCallId);
        var failedCall = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is ToolCallFailed);
        var message = Assert.Single(fx.Payloads.OfType<ExecutionMailboxMessageReceived>());
        var acknowledgement = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is ExecutionMailboxMessageAcknowledged);
        var ackPayload = (ExecutionMailboxMessageAcknowledged)fx.Codecs.Decode(acknowledgement);
        Assert.Equal(message.Message.MessageId, ackPayload.MessageId);
        var wakeEvents = fx.Store.ReadFrom(fx.Session, 1).Where(evt => fx.Codecs.Decode(evt) is WakeRequestCreated).ToArray();
        Assert.Equal(2, wakeEvents.Length);
        var firstWakeId = ((WakeRequestCreated)fx.Codecs.Decode(wakeEvents[0])).Request.WakeRequestId;
        var secondWakeId = ((WakeRequestCreated)fx.Codecs.Decode(wakeEvents[1])).Request.WakeRequestId;
        var failedWake = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is WakeRequestFailed);
        Assert.Equal(firstWakeId, ((WakeRequestFailed)fx.Codecs.Decode(failedWake)).WakeRequestId);
        Assert.Equal(new EventCausation(failedCall.EventId), failedWake.Causation);
        var acceptedEvents = fx.Store.ReadFrom(fx.Session, 1).Where(evt => fx.Codecs.Decode(evt) is WakeRequestAccepted).ToArray();
        Assert.Equal(2, acceptedEvents.Length);
        var wakeResolved = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is WakeRequestResolved);
        Assert.Contains(acceptedEvents, evt => ((WakeRequestAccepted)fx.Codecs.Decode(evt)).WakeRequestId == firstWakeId);
        Assert.Contains(acceptedEvents, evt => ((WakeRequestAccepted)fx.Codecs.Decode(evt)).WakeRequestId == secondWakeId);
        Assert.Equal(secondWakeId, ((WakeRequestResolved)fx.Codecs.Decode(wakeResolved)).WakeRequestId);
        var succeeded = fx.Store.ReadFrom(fx.Session, 1).Single(evt => evt.ToolCallId == receiveRequests[1].ToolCallId
            && fx.Codecs.Decode(evt) is ToolCallSucceeded);
        Assert.Equal(new EventCausation(succeeded.EventId), acknowledgement.Causation);
        Assert.Equal(new EventCausation(succeeded.EventId), wakeResolved.Causation);

        fx.Reopen();
        var beforeRetry = fx.Store.CurrentSequence(fx.Session);
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, Execute(fx, child).Outcome?.Kind);
        Assert.Equal(beforeRetry, fx.Store.CurrentSequence(fx.Session));
        Assert.Equal(3, Volatile.Read(ref providerCalls));
        var lane = Assert.Single(AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!.Lanes,
            item => item.LaneId == child.ChildLaneId.ToString());
        Assert.Equal(0, lane.PendingMailboxMessages);

    }

    [Fact]
    public async System.Threading.Tasks.Task User_mailbox_send_reaches_the_running_child_through_the_supervisor_path_and_is_delivered_once()
    {
        // A5: el usuario escribe al buzón de una delegación en curso (`/agent send`); el mensaje entra
        // por el mismo camino durable que el del supervisor y despierta el receive del hijo.
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true);
        fx.Ask();
        var child = Queue(fx);
        var providerCalls = 0;
        string? delivered = null;
        using var receiveEntered = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId,
            (work, token) => fx.ExplorerWithMailbox((_, _) =>
            {
                return Interlocked.Increment(ref providerCalls) == 1
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "mailbox-user",
                        "core.agents.mailbox.receive", "{}")],
                        StopReason.ToolUse, new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("mensaje recibido")], StopReason.EndTurn,
                        new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }, async (toolCall, receiveToken) =>
            {
                receiveEntered.Set();
                delivered = await fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run, fx.MailboxRoute,
                    BillingMode.Local, toolCall, receiveToken).ConfigureAwait(false);
                return delivered;
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(receiveEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var childExecution = Assert.Single(fx.Payloads.OfType<DelegationAccepted>()).ChildExecutionId;
        Assert.True(SpinWait.SpinUntil(() => fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, childExecution),
            TimeSpan.FromSeconds(10)));

        var sent = Send(fx, "delegation.mailbox.send", "\"delegationId\":\"" + child.DelegationId
            + "\",\"content\":\"prioriza el módulo B\"");
        Assert.True(sent.Status == "ok", sent.Error);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, sent.Outcome?.Kind);

        var completed = await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal("ok", completed.Status);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var message = Assert.Single(fx.Payloads.OfType<ExecutionMailboxMessageReceived>());
        Assert.Equal(root.ExecutionId, message.Message.SenderExecutionId);
        Assert.Contains("prioriza el módulo B", delivered);
        var acknowledged = Assert.Single(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
        Assert.Equal(message.Message.MessageId, acknowledged.MessageId);
        Assert.Equal(2, Volatile.Read(ref providerCalls));
        var lane = Assert.Single(AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!.Lanes,
            item => item.LaneId == child.ChildLaneId.ToString());
        Assert.Equal(0, lane.PendingMailboxMessages);
    }

    [Fact]
    public void User_mailbox_send_is_rejected_without_a_running_child_trusted_action_or_content()
    {
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true);
        fx.Ask();
        var child = Queue(fx);
        var fields = "\"delegationId\":\"" + child.DelegationId + "\",\"content\":\"hola\"";

        // Encolada: aún no hay ejecución ni buzón a quien escribir.
        var queued = Send(fx, "delegation.mailbox.send", fields);
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, queued.Outcome?.Kind);
        // Solo una acción de usuario de confianza puede escribir al buzón.
        var untrusted = fx.Server.Send(Command("delegation.mailbox.send", fields), CancellationToken.None);
        Assert.NotEqual("ok", untrusted.Status);
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageReceived>());
    }

    [Fact]
    public async System.Threading.Tasks.Task Durable_receive_success_reconciles_failed_ack_batch_after_reopen_once()
    {
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true, failMailboxAckBatches: true);
        fx.Ask();
        var child = Queue(fx);
        var providerCalls = 0;
        using var receiveEntered = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId,
            (work, token) => fx.ExplorerWithMailbox((_, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "receive-success",
                    "core.agents.mailbox.receive", "{}")], StopReason.ToolUse,
                    new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }, (toolCall, receiveToken) =>
            {
                receiveEntered.Set();
                return fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run, fx.MailboxRoute,
                    BillingMode.Local, toolCall, receiveToken);
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(receiveEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var childExecution = Assert.Single(fx.Payloads.OfType<DelegationAccepted>()).ChildExecutionId;
        Assert.True(SpinWait.SpinUntil(() => fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, childExecution),
            TimeSpan.FromSeconds(10)));
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var source = fx.Source();
        var supervisor = SupervisorClientFactory.Create(fx.Server, root.ExecutionId);
        var sent = supervisor.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"supervisor.mailbox.send\","
            + "\"targetExecutionId\":\"" + childExecution + "\",\"sourceEventId\":\"" + source.EventId
            + "\",\"content\":\"receipt durable\"}"), TestContext.Current.CancellationToken);
        Assert.Equal("ok", sent.Status);
        var failedAck = await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.NotEqual("ok", failedAck.Status);
        Assert.Equal(1, Volatile.Read(ref providerCalls));

        var request = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is ToolCallRequested tool
            && tool.ToolName == "core.agents.mailbox.receive");
        var success = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => evt.ToolCallId == request.ToolCallId
            && fx.Codecs.Decode(evt) is ToolCallSucceeded);
        var message = Assert.Single(fx.Payloads.OfType<ExecutionMailboxMessageReceived>());
        var wake = Assert.Single(fx.Payloads.OfType<WakeRequestCreated>());
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
        Assert.Empty(fx.Payloads.OfType<WakeRequestResolved>());

        // Simulate process loss after ToolCallSucceeded but before the atomic ACK + WakeResolved batch.
        // Recovery runs against a newly opened Host on the same SQLite only after the prior one is done.
        fx.MailboxAckFault!.FailAcknowledgementBatches = false;
        fx.Reopen();
        var ackEvent = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is ExecutionMailboxMessageAcknowledged);
        var resolvedEvent = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt => fx.Codecs.Decode(evt) is WakeRequestResolved);
        Assert.Equal(message.Message.MessageId, ((ExecutionMailboxMessageAcknowledged)fx.Codecs.Decode(ackEvent)).MessageId);
        Assert.Equal(wake.Request.WakeRequestId, ((WakeRequestResolved)fx.Codecs.Decode(resolvedEvent)).WakeRequestId);
        Assert.Equal(new EventCausation(success.EventId), ackEvent.Causation);
        Assert.Equal(new EventCausation(success.EventId), resolvedEvent.Causation);
        Assert.Equal(1, Volatile.Read(ref providerCalls));
        var recoveredSequence = fx.Store.CurrentSequence(fx.Session);
        fx.Reopen();
        Assert.Equal(recoveredSequence, fx.Store.CurrentSequence(fx.Session));
        Assert.Single(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
        Assert.Single(fx.Payloads.OfType<WakeRequestResolved>());
        Assert.Equal(1, Volatile.Read(ref providerCalls));
    }

    [Fact]
    public async System.Threading.Tasks.Task Orchestration_revoke_is_deferred_during_receive_then_cancel_closes_wait_fail_closed()
    {
        using var fx = new DelegationAdmissionTests.Fixture(authorizeMailboxRoute: true);
        fx.Ask();
        var child = Queue(fx);
        var providerCalls = 0;
        using var waiting = new ManualResetEventSlim();
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(child.DelegationId,
            (work, token) => fx.ExplorerWithMailbox((_, _) =>
            {
                Interlocked.Increment(ref providerCalls);
                return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "receive", "core.agents.mailbox.receive", "{}")],
                    StopReason.ToolUse, new(2, 1, 0, 0, 0), null, new("fixture", "fixture", null));
            }, (toolCall, receiveToken) =>
            {
                waiting.Set();
                return fx.Server.ReceiveMailboxMessageAsync(fx.Session, fx.Run, fx.MailboxRoute, BillingMode.Local,
                    toolCall, receiveToken);
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(waiting.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var childExecution = Assert.Single(fx.Payloads.OfType<DelegationAccepted>()).ChildExecutionId;
        Assert.True(SpinWait.SpinUntil(() => fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, childExecution),
            TimeSpan.FromSeconds(10)));
        var revokeWhileReceiving = Send(fx, "run.mode.revoke", "\"reason\":\"test\"");
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, revokeWhileReceiving.Outcome?.Kind);
        Assert.Equal("ToolCallActive", revokeWhileReceiving.Outcome?.Reason);
        var root = Assert.Single(fx.Payloads.OfType<AgentExecutionStarted>(), item => item.ParentExecutionId is null);
        var source = fx.Source();
        var supervisor = SupervisorClientFactory.Create(fx.Server, root.ExecutionId);
        Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + child.DelegationId + "\"").Status);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(fx.Server.HasActiveMailboxWaiter(fx.Session, fx.Run, childExecution));
        Assert.Equal("ok", Send(fx, "run.mode.revoke", "\"reason\":\"test\"").Status);
        var result = supervisor.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"supervisor.mailbox.send\",\"targetExecutionId\":\"" + childExecution
            + "\",\"sourceEventId\":\"" + source.EventId + "\",\"content\":\"no debe saltar autoridad\"}"),
            TestContext.Current.CancellationToken);
        Assert.NotEqual("ok", result.Status);
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageReceived>());
        Assert.Empty(fx.Payloads.OfType<WakeRequestCreated>());
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
        Assert.Equal(1, Volatile.Read(ref providerCalls));
        Assert.Empty(fx.Payloads.OfType<WakeRequestCreated>());
        Assert.Empty(fx.Payloads.OfType<ExecutionMailboxMessageAcknowledged>());
    }

    [Fact]
    public async System.Threading.Tasks.Task Full_capacity_keeps_child_queued_and_cancellation_prevents_later_dispatch()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        fx.Grant(maxAgents: 2);
        var running = Queue(fx);
        var waiting = Queue(fx);
        using var entered = new ManualResetEventSlim();
        using var waitStarted = new ManualResetEventSlim();
        var waitingProviderCalls = 0;
        var dispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(running.DelegationId,
            (_, token) => {
                entered.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("cancellation expected");
            }, CancellationToken.None), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var waitingDispatch = System.Threading.Tasks.Task.Run(() => {
            waitStarted.Set();
            return fx.Server.ExecuteDelegation(waiting.DelegationId, (work, token) => {
                Interlocked.Increment(ref waitingProviderCalls);
                return fx.Explorer((_, _) => new ModelResponse([new TextBlock("should not run")],
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture", null))).Ask(work.Objective, "system",
                    work.Session, work.Run, work.Delegation.ChildLaneId, "", token);
            }, CancellationToken.None, waitForCapacity: true);
        }, TestContext.Current.CancellationToken);
        Assert.True(waitStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => AgentCapacity.For(fx.Store)
                .ReadSnapshot(fx.Session, fx.Run).Waiting == 1, TimeSpan.FromSeconds(10)));
            var deferred = Execute(fx, waiting);
            Assert.Equal("WaitingForCapacity", deferred.Outcome?.Reason);
            Assert.DoesNotContain(fx.Payloads.OfType<DelegationAccepted>(), item => item.DelegationId == waiting.DelegationId);
            Assert.Equal(LaneState.Queued, LaneProjection.Replay(fx.Codecs,
                fx.Store.ReadFrom(fx.Session, 1)).StateOf(waiting.ChildLaneId));
            Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + waiting.DelegationId + "\"").Status);
            var cancelledWait = await waitingDispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("WaitingForCapacityCancelled", cancelledWait.Outcome?.Reason);
            Assert.Equal(0, waitingProviderCalls);
            Assert.Equal("ok", Send(fx, "delegation.cancel", "\"delegationId\":\"" + running.DelegationId + "\"").Status);
            await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(fx.Payloads.OfType<DelegationAccepted>(), item => item.DelegationId == waiting.DelegationId);
            Assert.DoesNotContain(fx.Store.ReadFrom(fx.Session, 1).Where(e => e.LaneId == waiting.ChildLaneId),
                evt => fx.Codecs.Decode(evt) is ModelStepStarted);
            Assert.Equal(TaskState.Cancelled, TaskGraphProjection.Replay(fx.Codecs,
                fx.Store.ReadFrom(fx.Session, 1)).Get(waiting.ChildTaskId)!.State);
        }
        finally
        {
            Send(fx, "delegation.cancel", "\"delegationId\":\"" + running.DelegationId + "\"");
            await System.Threading.Tasks.Task.WhenAll(dispatch, waitingDispatch)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Distinct_children_hold_parallel_leases_at_real_provider_boundaries()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        fx.Grant(maxAgents: 3);
        var first = Queue(fx, tokens: 16_384);
        var second = Queue(fx, tokens: 16_384);
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ExecutionId[] members = [];
        var usage = new TokenUsage(2, 1, 0, 0, 0);
        ModelResponse Response() => new([new TextBlock("respuesta paralela")], StopReason.EndTurn,
            usage, null, new ProviderMetadata("fixture", "fixture", null));
        var firstDispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(first.DelegationId,
            (work, token) => fx.Explorer((_, providerToken) => {
                firstEntered.Set();
                release.Wait(providerToken);
                return Response();
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            CancellationToken.None), TestContext.Current.CancellationToken);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var secondDispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(second.DelegationId,
            (work, token) => fx.Explorer((_, providerToken) => {
                secondEntered.Set();
                release.Wait(providerToken);
                return Response();
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId, "", token),
            CancellationToken.None), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(2, fx.Payloads.OfType<DelegationAccepted>().Count());
            Assert.Equal(2, fx.Store.ReadFrom(fx.Session, 1).Count(evt =>
                (evt.LaneId == first.ChildLaneId || evt.LaneId == second.ChildLaneId)
                && fx.Codecs.Decode(evt) is ModelStepStarted));
            var owner = fx.Payloads.OfType<AgentExecutionStarted>().Single(e => e.ParentExecutionId is null);
            members = fx.Payloads.OfType<DelegationAccepted>().Select(e => e.ChildExecutionId).ToArray();
            var join = Send(fx, "execution.join", "\"ownerExecutionId\":\"" + owner.ExecutionId
                + "\",\"kind\":\"All\",\"members\":[\"" + members[0] + "\",\"" + members[1] + "\"]");
            Assert.Equal("ok", join.Status);
            Assert.Equal(TaskState.Blocked, TaskGraphProjection.Replay(fx.Codecs,
                fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
        }
        finally { release.Set(); }
        var completed = await System.Threading.Tasks.Task.WhenAll(firstDispatch, secondDispatch)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(completed, ack => Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind));
        Assert.Equal(2, fx.Payloads.OfType<AgentExecutionCompleted>().Count());
        Assert.Equal(2, fx.Payloads.OfType<AgentResultProduced>().Count());
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
        fx.Reopen();
        var firstResult = fx.Payloads.OfType<AgentResultProduced>().Single(e => e.ExecutionId == members[0]);
        var secondResult = fx.Payloads.OfType<AgentResultProduced>().Single(e => e.ExecutionId == members[1]);
        Assert.Equal("ok", Disposition(fx, first, firstResult).Status);
        Assert.Empty(fx.Payloads.OfType<ExecutionJoinResolved>());
        Assert.Equal("ok", Disposition(fx, second, secondResult).Status);
        Assert.Equal(2, Assert.Single(fx.Payloads.OfType<ExecutionJoinResolved>()).SatisfyingExecutionIds.Count);
        Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs,
            fx.Store.ReadFrom(fx.Session, 1)).Get(fx.RootTask)!.State);
    }

    [Fact]
    public async System.Threading.Tasks.Task Cancelling_a_child_without_an_open_step_finishes_it_while_sibling_step_stays_open()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        fx.Ask();
        fx.Grant(maxAgents: 3);
        var cancelled = Queue(fx);
        var sibling = Queue(fx);
        using var cancelledEntered = new ManualResetEventSlim();
        using var siblingStepEntered = new ManualResetEventSlim();
        var cancelledDispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(cancelled.DelegationId,
            (_, token) =>
            {
                cancelledEntered.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was expected.");
            }, CancellationToken.None), TestContext.Current.CancellationToken);
        Assert.True(cancelledEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        using var releaseSibling = new ManualResetEventSlim();
        var siblingDispatch = System.Threading.Tasks.Task.Run(() => fx.Server.ExecuteDelegation(sibling.DelegationId,
            (work, token) =>
            {
                var turn = TurnId.New();
                using var scope = ExecutionScope.Begin(work.Scope with { TurnId = turn });
                var stream = new EventStream(fx.Store, fx.Codecs, work.Session);
                stream.Append(new TurnStarted(turn, work.Scope.LaneId!), DurabilityClass.Barrier);
                stream.Append(new ModelStepStarted(turn, 0, "fixture-model", 4096, "None", null, null, null),
                    DurabilityClass.Barrier);
                siblingStepEntered.Set();
                releaseSibling.Wait(token);
                token.ThrowIfCancellationRequested();
                return new ExplorerTurn.TurnResult("unexpected", StopReason.EndTurn, 1,
                    new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ExplorerTurn.ToolUseTrace>(), null);
            }, CancellationToken.None), TestContext.Current.CancellationToken);
        Assert.True(siblingStepEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        try
        {
            var cancellation = Send(fx, "delegation.cancel", "\"delegationId\":\"" + cancelled.DelegationId + "\"");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, cancellation.Outcome?.Kind);
            await cancelledDispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var journal = fx.Store.ReadFrom(fx.Session, 1);
            var payloads = journal.Select(fx.Codecs.Decode).ToArray();
            Assert.Contains(payloads.OfType<AgentExecutionFailed>(), item => item.LaneId == cancelled.ChildLaneId);
            Assert.Contains(payloads.OfType<TaskCancelled>(), item => item.TaskId == cancelled.ChildTaskId);
            Assert.Contains(payloads.OfType<LaneCancelled>(), item => item.LaneId == cancelled.ChildLaneId);
            Assert.Contains(payloads.OfType<DelegationFailed>(), item => item.DelegationId == cancelled.DelegationId);

            Assert.DoesNotContain(payloads.OfType<AgentExecutionFailed>(), item => item.LaneId == sibling.ChildLaneId);
            Assert.DoesNotContain(payloads.OfType<TaskCancelled>(), item => item.TaskId == sibling.ChildTaskId);
            Assert.DoesNotContain(payloads.OfType<LaneCancelled>(), item => item.LaneId == sibling.ChildLaneId);
            Assert.DoesNotContain(payloads.OfType<DelegationFailed>(), item => item.DelegationId == sibling.DelegationId);
            Assert.Contains(journal, evt => evt.LaneId == sibling.ChildLaneId
                && fx.Codecs.Decode(evt) is ModelStepStarted);
            Assert.Equal(TaskState.Running, TaskGraphProjection.Replay(fx.Codecs, journal).Get(sibling.ChildTaskId)!.State);
            Assert.Equal(LaneState.Running, LaneProjection.Replay(fx.Codecs, journal).StateOf(sibling.ChildLaneId));
        }
        finally
        {
            releaseSibling.Set();
            Send(fx, "delegation.cancel", "\"delegationId\":\"" + sibling.DelegationId + "\"");
            try { await siblingDispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
            catch (Exception) { }
        }
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
        Assert.True(ack.Status == "ok", ack.Error); Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
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
