using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    internal async Task<string?> ReceiveMailboxMessageAsync(SessionId session, RunId run, ModelRoute route,
        BillingMode billing, ToolCallId toolCallId, CancellationToken cancellationToken)
    {
        var scope = ExecutionScope.Current
            ?? throw new InvalidOperationException("Mailbox receive requires an attributed execution.");
        if (scope.RunId != run || scope.ExecutionId is not { } executionId || scope.TaskId is not { } taskId
            || scope.LaneId is not { } laneId || scope.TurnId is not { } turnId)
            throw new InvalidOperationException("Mailbox receive scope does not match the current Run/Lane/Execution.");
        var registry = MailboxWaitRegistry.For(_store);
        MailboxWaitRegistry.Waiter? waiter = null;
        try
        {
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var journal = _store.ReadFrom(session, 1);
                var own = journal.Where(evt => evt.RunId == run).ToArray();
                var facts = own.Select(_codecs.Decode).ToArray();
                ValidateMailboxReceiveAuthority(session, run, route, billing, taskId, laneId, turnId,
                    executionId, toolCallId, own, facts);
                waiter = registry.Register(session, run, executionId, toolCallId, route, billing);
                var pending = FindUnacknowledgedMailboxMessage(session, run, executionId, journal);
                if (pending is not null)
                {
                    var wake = CreateMailboxWake(session, run, executionId, toolCallId, pending.Value.MessageEvent,
                        pending.Value.Message, pending.Value.Binding, new EventStream(_store, _codecs, session));
                    try
                    {
                        var deliveryPayload = EncodeMailboxDelivery(pending.Value.Message, wake, ReadMailboxText(pending.Value.Message));
                        if (!registry.Signal(session, run, executionId, toolCallId, deliveryPayload))
                            FailMailboxWake(session, run, executionId, wake, "Mailbox receive ended before delivery.");
                    }
                    catch
                    {
                        FailMailboxWake(session, run, executionId, wake, "Mailbox delivery preparation failed.");
                        throw;
                    }
                }
            }

            var delivery = await waiter.Signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_modeAuthorityMutationGate)
            {
                var current = _store.ReadFrom(session, 1);
                var own = current.Where(evt => evt.RunId == run).ToArray();
                ValidateMailboxReceiveAuthority(session, run, route, billing, taskId, laneId, turnId,
                    executionId, toolCallId, own, own.Select(_codecs.Decode).ToArray());
                using var document = JsonDocument.Parse(delivery);
                var wakeId = new WakeRequestId(Guid.Parse(document.RootElement.GetProperty("wakeRequestId").GetString()!));
                var wakeEvent = own.SingleOrDefault(evt => _codecs.Decode(evt) is WakeRequestCreated created
                    && created.ExecutionId == executionId && created.Request.WakeRequestId == wakeId);
                if (wakeEvent is null || wakeEvent.Causation is not EventCausation { EventId: var causedBy }
                    || !own.Any(evt => evt.EventId == causedBy && evt.ToolCallId == toolCallId
                        && evt.ExecutionId == executionId && _codecs.Decode(evt) is ToolCallStarted))
                    throw new InvalidOperationException("Mailbox wake is not attributed to this live receive ToolCall.");
            }
            return delivery;
        }
        finally
        {
            if (waiter is not null) registry.Remove(session, run, executionId, waiter);
        }
    }

    internal bool HasActiveMailboxWaiter(SessionId session, RunId run, ExecutionId execution)
        => MailboxWaitRegistry.For(_store).TryGet(session, run, execution, out _);

    internal void ResolveMailboxDelivery(ToolCallId toolCallId)
    {
        var scope = ExecutionScope.Current;
        if (scope?.RunId is not { } run || scope.ExecutionId is not { } executionId
            || scope.TaskId is not { } taskId || scope.LaneId is not { } laneId)
            throw new InvalidOperationException("Mailbox receipt resolution requires the owning execution scope.");
        if (_lastSessionId is not { } session || _lastRunId != run)
            throw new InvalidOperationException("Mailbox receipt session is unavailable.");
        lock (_modeAuthorityMutationGate)
        {
            var journal = _store.ReadFrom(session, 1);
            var own = journal.Where(evt => evt.RunId == run).ToArray();
            var payloads = own.Select(_codecs.Decode).ToArray();
            var requestedEvent = own.SingleOrDefault(evt => evt.ToolCallId == toolCallId
                && evt.ExecutionId == executionId && evt.TaskId == taskId && evt.LaneId == laneId
                && _codecs.Decode(evt) is ToolCallRequested requested
                && requested.ToolName == "core.agents.mailbox.receive");
            if (requestedEvent is null) return;
            var succeededEvent = own.SingleOrDefault(evt => evt.ToolCallId == toolCallId
                && evt.ExecutionId == executionId && evt.TaskId == taskId && evt.LaneId == laneId
                && _codecs.Decode(evt) is ToolCallSucceeded);
            if (succeededEvent is null)
            {
                ReconcileFailedMailboxReceipts(session, run);
                return;
            }
            var succeeded = (ToolCallSucceeded)_codecs.Decode(succeededEvent);
            if (!TryParseMailboxSummary(succeeded.ResultJson, out var messageId, out var wakeId)) return;
            var messageEvent = own.SingleOrDefault(evt => _codecs.Decode(evt) is ExecutionMailboxMessageReceived received
                && received.ExecutionId == executionId && received.Message.MessageId == messageId);
            var requestStart = own.SingleOrDefault(evt => evt.ToolCallId == toolCallId && evt.ExecutionId == executionId
                && evt.TaskId == taskId && evt.LaneId == laneId && evt.TurnId == scope.TurnId
                && _codecs.Decode(evt) is ToolCallStarted);
            var wakeCreatedEvent = own.SingleOrDefault(evt => _codecs.Decode(evt) is WakeRequestCreated created
                && created.ExecutionId == executionId && created.Request.WakeRequestId == wakeId
                && created.Request.MessageId == messageId);
            if (messageEvent is null || requestStart is null || wakeCreatedEvent is null
                || wakeCreatedEvent.Causation is not EventCausation wakeCause
                || wakeCause.EventId != requestStart.EventId) return;
            var message = (ExecutionMailboxMessageReceived)_codecs.Decode(messageEvent);
            var wake = (WakeRequestCreated)_codecs.Decode(wakeCreatedEvent);
            var records = PreM6RecordProjection.Replay(session, _codecs, journal);
            if (records.Records["message:" + messageId].Facts.OfType<ExecutionMailboxMessageAcknowledged>().Any()
                || records.Records["wake:" + wakeId].Phase != PreM6RecordPhase.Accepted
                || !payloads.OfType<WakeRequestAccepted>().Any(item => item.ExecutionId == executionId
                    && item.WakeRequestId == wakeId)) return;
            var mailbox = message.Message.MailboxId;
            var executionScope = new ExecutionScopeState(run, taskId, laneId, scope.TurnId, toolCallId, executionId);
            using var attributed = ExecutionScope.Begin(executionScope);
            var cause = new EventCausation(succeededEvent.EventId);
            new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[] {
                new ExecutionMailboxMessageAcknowledged(executionId, mailbox, messageId),
                new WakeRequestResolved(executionId, wakeId),
            }, DurabilityClass.Barrier,
                [executionScope, executionScope], [cause, cause]);
        }
    }

    private void ValidateMailboxReceiveAuthority(SessionId session, RunId run, ModelRoute route,
        BillingMode billing, TaskId taskId, LaneId laneId, TurnId turnId, ExecutionId executionId,
        ToolCallId toolCallId, DomainEvent[] own, DomainEventPayload[] facts)
    {
        var task = TaskGraphProjection.Replay(_codecs, own).Get(taskId);
        var lane = LaneProjection.Replay(_codecs, own).StateOf(laneId);
        if (task?.State != TaskState.Running || lane != LaneState.Running)
            throw new InvalidOperationException("Mailbox receive cannot wake a blocked, completed, or user-interaction Task.");
        if (IsExecutionTerminal(facts, executionId)) throw new InvalidOperationException("Mailbox receive execution is terminal.");
        var start = facts.OfType<AgentExecutionStarted>().SingleOrDefault(item => item.ExecutionId == executionId)
            ?? throw new InvalidOperationException("Mailbox receive execution is unknown.");
        if (start.ParentExecutionId is not { } supervisor || IsExecutionTerminal(facts, supervisor))
            throw new InvalidOperationException("Mailbox receive requires its live bound supervisor.");
        var binding = facts.OfType<SupervisionBindingCreated>().SingleOrDefault(item => item.Binding.SubjectExecutionId == executionId
            && item.Binding.SupervisorExecutionId == supervisor)?.Binding
            ?? throw new InvalidOperationException("Mailbox binding is missing.");
        var records = PreM6RecordProjection.Replay(session, _codecs, _store.ReadFrom(session, 1));
        if (!records.Records.TryGetValue("binding:" + binding.BindingId, out var bindingHistory)
            || bindingHistory.Phase != PreM6RecordPhase.Accepted
            || !facts.OfType<SupervisionBindingAccepted>().Any(item => item.ExecutionId == executionId
                && item.BindingId == binding.BindingId))
            throw new InvalidOperationException("Mailbox binding handshake is not active.");
        var startedEvent = own.SingleOrDefault(evt => evt.ToolCallId == toolCallId && evt.ExecutionId == executionId
            && evt.TaskId == taskId && evt.LaneId == laneId && evt.TurnId == turnId
            && _codecs.Decode(evt) is ToolCallStarted);
        var requestEvent = own.SingleOrDefault(evt => evt.ToolCallId == toolCallId && evt.ExecutionId == executionId
            && evt.TaskId == taskId && evt.LaneId == laneId && evt.TurnId == turnId
            && _codecs.Decode(evt) is ToolCallRequested requested
            && requested.ToolName == "core.agents.mailbox.receive");
        if (startedEvent is null || requestEvent is null
            || own.Any(evt => evt.ToolCallId == toolCallId && evt.Sequence > startedEvent.Sequence
                && _codecs.Decode(evt) is ToolCallSucceeded or ToolCallFailed or ToolCallRejected or ToolCallEffectUnknown))
            throw new InvalidOperationException("Mailbox receive has no exact live ToolCall receipt.");
        var callTerminal = own.Any(evt => evt.ToolCallId == toolCallId && evt.Sequence > startedEvent.Sequence
            && _codecs.Decode(evt) is ToolCallSucceeded or ToolCallFailed or ToolCallRejected or ToolCallEffectUnknown);
        if (HasOpenModelStep(own, laneId) || callTerminal)
            throw new InvalidOperationException("Mailbox receive is not at its supported settled provider boundary.");
        var unresolvedInteractions = own.Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .Where(item => item.Event.ExecutionId == executionId && item.Event.LaneId == laneId
                && item.Payload is InteractionRequested).ToArray();
        var resolvedInteractions = facts.OfType<InteractionResolved>().Select(item => item.InteractionId).ToHashSet();
        if (unresolvedInteractions.Any(item => !resolvedInteractions.Contains(
                ((InteractionRequested)item.Payload).InteractionId)))
            throw new InvalidOperationException("Mailbox receive cannot supersede an unresolved human interaction.");
        var projection = RunProjection.Replay(session, run, _codecs, own);
        if (projection.State != RunState.Running || projection.ModeAuthority?.Mode != RunMode.Orchestrate
            || projection.ModeAuthority.Authorization is not { } authorization
            || !projection.ModeAuthority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Mailbox receive requires active orchestration authority.");
        var routing = SessionRoutingAuthorization.Read(_store.ReadFrom(session, 1), _codecs, session);
        if (routing?.Allows(route, billing) != true)
            throw new InvalidOperationException("Mailbox receive routing authorization was revoked.");
        if (_artifacts is null) throw new InvalidOperationException("Mailbox receive budget accounting is unavailable.");
        var reservations = _userSpendReader is null ? null : new SqliteSpendReservationStore(
            Path.Combine(_userSpendReader.UserDataDirectory, "spend-reservations.db"));
        DelegationBudgetGuard.Validate(_store.ReadFrom(session, 1), _codecs, _artifacts, _store, reservations,
            session, run, laneId, turnId, newTurn: false, invocation: false, tool: toolCallId,
            maximumCost: null, maximumTokens: null);
    }

    private void TryWakeActiveMailboxWait(SessionId session, RunId run, ExecutionId execution,
        ExecutionMailboxMessageReceived message, DomainEvent messageEvent)
    {
        var registry = MailboxWaitRegistry.For(_store);
        if (!registry.TryGet(session, run, execution, out var waiter)) return;
        if (waiter.Signal.Task.IsCompleted) return;
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var requestEvent = own.SingleOrDefault(evt => evt.ToolCallId == waiter.ToolCall
            && evt.ExecutionId == execution && _codecs.Decode(evt) is ToolCallRequested request
            && request.ToolName == "core.agents.mailbox.receive");
        if (requestEvent is null || requestEvent.TaskId is not { } task || requestEvent.LaneId is not { } lane
            || requestEvent.TurnId is not { } turn) return;
        try
        {
            ValidateMailboxReceiveAuthority(session, run, waiter.Route, waiter.Billing, task, lane, turn,
                execution, waiter.ToolCall, own, own.Select(_codecs.Decode).ToArray());
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or KeyNotFoundException or ArgumentException)
        {
            return; // wake policy is fail-closed; the durable mailbox message remains pending.
        }
        var binding = own.Select(_codecs.Decode).OfType<SupervisionBindingCreated>()
            .Single(item => item.Binding.SubjectExecutionId == execution).Binding;
        var wake = CreateMailboxWake(session, run, execution, waiter.ToolCall, messageEvent, message,
            binding, new EventStream(_store, _codecs, session));
        try
        {
            var delivery = EncodeMailboxDelivery(message, wake, ReadMailboxText(message));
            if (!registry.Signal(session, run, execution, waiter.ToolCall, delivery))
                FailMailboxWake(session, run, execution, wake, "Mailbox receive ended before delivery.");
        }
        catch
        {
            FailMailboxWake(session, run, execution, wake, "Mailbox delivery preparation failed.");
            throw;
        }
    }

    private (DomainEvent MessageEvent, ExecutionMailboxMessageReceived Message, SupervisionBinding Binding)?
        FindUnacknowledgedMailboxMessage(SessionId session, RunId run, ExecutionId execution, IReadOnlyList<DomainEvent> journal)
    {
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var facts = own.Select(_codecs.Decode).ToArray();
        var binding = facts.OfType<SupervisionBindingCreated>().SingleOrDefault(item => item.Binding.SubjectExecutionId == execution)?.Binding;
        if (binding is null) return null;
        var records = PreM6RecordProjection.Replay(session, _codecs, journal);
        if (!records.Records.TryGetValue("binding:" + binding.BindingId, out var phase)
            || phase.Phase != PreM6RecordPhase.Accepted) return null;
        var mailboxes = facts.OfType<ExecutionMailboxCreated>().Where(item => item.ExecutionId == execution)
            .Select(item => item.Mailbox.MailboxId).ToHashSet();
        foreach (var evt in own.OrderBy(item => item.Sequence))
        {
            if (_codecs.Decode(evt) is not ExecutionMailboxMessageReceived received
                || received.ExecutionId != execution || !mailboxes.Contains(received.Message.MailboxId)) continue;
            if (records.Records["message:" + received.Message.MessageId].Facts
                .OfType<ExecutionMailboxMessageAcknowledged>().Any()) continue;
            if (records.Records.Values.Any(history => history.Facts.OfType<WakeRequestCreated>()
                    .Any(created => created.Request.MessageId == received.Message.MessageId)
                    && history.Phase is PreM6RecordPhase.Created or PreM6RecordPhase.Accepted)) return null;
            return (evt, received, binding);
        }
        return null;
    }

    private WakeRequest CreateMailboxWake(SessionId session, RunId run, ExecutionId execution, ToolCallId toolCall,
        DomainEvent messageEvent, ExecutionMailboxMessageReceived message, SupervisionBinding binding, EventStream stream)
    {
        var journal = _store.ReadFrom(session, 1);
        var requestStart = journal.SingleOrDefault(evt => evt.RunId == run && evt.ToolCallId == toolCall
            && evt.ExecutionId == execution && _codecs.Decode(evt) is ToolCallStarted)
            ?? throw new InvalidOperationException("Mailbox wake has no exact live ToolCall start.");
        var scope = new ExecutionScopeState(run, requestStart.TaskId, requestStart.LaneId,
            requestStart.TurnId, toolCall, execution);
        var source = message.Message.CorrelationEvent ?? new EvidenceEventRef(session, messageEvent.EventId);
        var request = new WakeRequest(WakeRequestId.New(), execution, source,
            "Mailbox message delivered to an active core.agents.mailbox.receive", binding.PolicyRevision,
            message.Message.MessageId);
        using var attributed = ExecutionScope.Begin(scope);
        stream.AppendBatch([new WakeRequestCreated(execution, request)], DurabilityClass.Barrier,
            [scope], [new EventCausation(requestStart.EventId)]);
        var createdEvent = _store.ReadFrom(session, 1).Single(evt => _codecs.Decode(evt) is WakeRequestCreated created
            && created.Request.WakeRequestId == request.WakeRequestId);
        stream.AppendBatch([new WakeRequestAccepted(execution, request.WakeRequestId)], DurabilityClass.Barrier,
            [scope], [new EventCausation(createdEvent.EventId)]);
        return request;
    }

    private void FailMailboxWake(SessionId session, RunId run, ExecutionId execution, WakeRequest request, string reason)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        var accepted = own.Single(evt => _codecs.Decode(evt) is WakeRequestAccepted item
            && item.ExecutionId == execution && item.WakeRequestId == request.WakeRequestId);
        var scope = AgentScope(run, own.Select(_codecs.Decode), execution);
        using var attributed = ExecutionScope.Begin(scope);
        new EventStream(_store, _codecs, session).AppendBatch([new WakeRequestFailed(execution,
            request.WakeRequestId, reason)], DurabilityClass.Barrier, [scope], [new EventCausation(accepted.EventId)]);
    }

    private string ReadMailboxText(ExecutionMailboxMessageReceived message)
    {
        var text = _artifacts?.GetText(message.Message.ContentRef.Hash)
            ?? throw new InvalidDataException("Mailbox content artifact is unavailable.");
        if (System.Text.Encoding.UTF8.GetByteCount(text) != message.Message.ContentRef.Size
            || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),
                message.Message.ContentRef.Hash.Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mailbox content artifact failed integrity verification.");
        return text;
    }

    private static string EncodeMailboxDelivery(ExecutionMailboxMessageReceived message, WakeRequest wake, string content)
        => "{" + JsonObj.Field("messageId", message.Message.MessageId.ToString()) + ","
            + JsonObj.Field("wakeRequestId", wake.WakeRequestId.ToString()) + ","
            + JsonObj.Field("content", content) + "}";

    private static bool TryParseMailboxSummary(string resultJson, out MailboxMessageId messageId,
        out WakeRequestId wakeId)
    {
        messageId = null!; wakeId = null!;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("summary", out var summary)) return false;
            var parts = summary.GetString()?.Split(':');
            if (parts is not ["mailbox", var message, "wake", var wake]
                || !Guid.TryParse(message, out var messageGuid) || !Guid.TryParse(wake, out var wakeGuid)) return false;
            messageId = new MailboxMessageId(messageGuid); wakeId = new WakeRequestId(wakeGuid);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void ReconcileDurableMailboxReceipts(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        foreach (var success in own.Where(evt => _codecs.Decode(evt) is ToolCallSucceeded).ToArray())
        {
            var payload = (ToolCallSucceeded)_codecs.Decode(success);
            if (!TryParseMailboxSummary(payload.ResultJson, out _, out _)) continue;
            var request = own.SingleOrDefault(evt => evt.ToolCallId == payload.ToolCallId
                && _codecs.Decode(evt) is ToolCallRequested call && call.ToolName == "core.agents.mailbox.receive");
            if (request is null || request.ExecutionId != success.ExecutionId || request.TaskId != success.TaskId
                || request.LaneId != success.LaneId || request.TurnId != success.TurnId) continue;
            using var scope = ExecutionScope.Begin(new(run, success.TaskId, success.LaneId, success.TurnId,
                payload.ToolCallId, success.ExecutionId));
            ResolveMailboxDelivery(payload.ToolCallId);
        }
        ReconcileFailedMailboxReceipts(session, run);
    }

    private void ReconcileFailedMailboxReceipts(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1);
        var own = journal.Where(evt => evt.RunId == run).ToArray();
        foreach (var wakeEvent in own.Where(evt => _codecs.Decode(evt) is WakeRequestCreated).ToArray())
        {
            var created = (WakeRequestCreated)_codecs.Decode(wakeEvent);
            if (created.Request.MessageId is not { } messageId) continue;
            var records = PreM6RecordProjection.Replay(session, _codecs, journal);
            if (!records.Records.TryGetValue("wake:" + created.Request.WakeRequestId, out var wakeHistory)
                || wakeHistory.Phase is not (PreM6RecordPhase.Created or PreM6RecordPhase.Accepted)) continue;
            var requestStart = own.SingleOrDefault(evt => evt.EventId == (wakeEvent.Causation as EventCausation)?.EventId
                && evt.ToolCallId is not null && _codecs.Decode(evt) is ToolCallStarted);
            if (requestStart?.ToolCallId is not { } callId || requestStart.TurnId is not { } turnId
                || requestStart.TaskId is not { } taskId || requestStart.LaneId is not { } laneId
                || requestStart.ExecutionId != created.ExecutionId) continue;
            var terminal = own.Where(evt => evt.ToolCallId == callId && evt.Sequence > requestStart.Sequence)
                .FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallFailed or ToolCallRejected or ToolCallEffectUnknown);
            terminal ??= own.Where(evt => evt.TurnId == turnId && evt.TaskId == taskId && evt.LaneId == laneId
                    && evt.ExecutionId == created.ExecutionId && evt.Sequence > requestStart.Sequence)
                .FirstOrDefault(evt => _codecs.Decode(evt) is TurnInterrupted or TurnAbandoned);
            if (terminal is null) continue; // ToolCallStarted without a terminal receipt is uncertain and remains pending.
            var cause = new EventCausation(terminal.EventId);
            var scope = new ExecutionScopeState(run, taskId, laneId, turnId, callId, created.ExecutionId);
            using var attributed = ExecutionScope.Begin(scope);
            new EventStream(_store, _codecs, session).AppendBatch([new WakeRequestFailed(created.ExecutionId,
                created.Request.WakeRequestId, "Mailbox receive ended without a successful ToolCall receipt.")],
                DurabilityClass.Barrier, [scope], [cause]);
        }
    }
}
