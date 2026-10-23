namespace OmniCore.Host;

using OmniCore.Protocol;
using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Engine;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Task = System.Threading.Tasks.Task;

internal sealed record PersistedConversationCommand(string CommandId, string RequestHash, string SessionId,
    string RunId, long? FirstSequence, long? LastSequence, string? ContinuationInteractionId = null,
    string? ContinuationCommandId = null, string? ContinuationRequestHash = null,
    long? ContinuationFirstSequence = null, long? ContinuationLastSequence = null,
    string? ContinuationResponseType = null, string? ContinuationOptionId = null,
    string? ContinuationTurnId = null,
    bool NativeChoiceCapability = false, bool ExplicitChoiceRecovery = false,
    string? NativeInvocationId = null, string? NativeCommandKind = null,
    bool ExplicitNativeCommandRecovery = false, bool NativeWorkflowContinuation = false,
    string? ContinuationTaskId = null, string? ContinuationLaneId = null,
    string? ContinuationToolCallId = null, string? ContinuationInvocationId = null,
    bool NativeWeakSandboxCapability = false);

internal sealed record WeakSandboxWorkflowContinuation(string InvocationId, string InteractionId, string OptionId,
    string TaskId, string LaneId, string TurnId, string ToolCallId, string ResponseCommandId,
    long ResponseFirstSequence, long ResponseLastSequence);

/// <summary>Request/response stdio adapter over the same IOmniClient used by the in-process CLI.</summary>
public static class StdioProtocolHost
{
    public static int Run(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        CancellationToken cancellationToken = default, Func<string, string>? localizeDiagnostic = null)
        => RunCore(client, input, output, diagnostics, null, null, cancellationToken, localizeDiagnostic);

    internal static int RunWithTrustedActions(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        byte[] bootstrapKey, CancellationToken cancellationToken = default, Func<string, string>? localizeDiagnostic = null,
        Func<PersistedConversationCommand, CancellationToken, Task<int>>? runPersistedConversation = null,
        Func<string, string?>? validateConversationAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapKey);
        if (bootstrapKey.Length != 32) throw new ArgumentException("Trusted action bootstrap key must be 256 bits.", nameof(bootstrapKey));
        return RunCore(client, input, output, diagnostics, bootstrapKey,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), cancellationToken, localizeDiagnostic,
            runPersistedConversation, validateConversationAttachments);
    }

    private static int RunCore(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        byte[]? bootstrapKey, string? actionNonce, CancellationToken cancellationToken,
        Func<string, string>? localizeDiagnostic,
        Func<PersistedConversationCommand, CancellationToken, Task<int>>? runPersistedConversation = null,
        Func<string, string?>? validateConversationAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var negotiated = false;
        var conversation = new ConversationExecution(runPersistedConversation, validateConversationAttachments);
        var eventStream = new EventStreamPump(output);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = WireFrame.Read(input);
                if (frame is null)
                {
                    eventStream.Dispose();
                    conversation.StopAndJoin();
                    return 0;
                }
                WireEnvelope request;
                try { request = ParseEnvelope(frame); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                {
                    diagnostics.WriteLine(localizeDiagnostic?.Invoke("stdio.host.invalid_frame")
                        ?? "Invalid stdio protocol frame; connection closed.");
                    return 2;
                }

                if (!negotiated)
                {
                    if (!TryNegotiate(client, request, bootstrapKey, actionNonce, runPersistedConversation is not null,
                        out var response, out var accepted, out var nativeChoiceCapability,
                        out var nativeWeakSandboxCapability))
                    {
                        WriteFrame(output, response!.ToJson());
                        return 3;
                    }
                    conversation.SetNativeChoiceCapability(nativeChoiceCapability);
                    conversation.SetNativeWeakSandboxCapability(nativeWeakSandboxCapability);
                    negotiated = accepted;
                    WriteFrame(output, response!.ToJson());
                    continue;
                }

                if (request.Version != ProtocolVersions.V1 || request.MessageType != MessageTypes.Command)
                {
                    WriteFrame(output, Error(request.MessageId, "invalid_request",
                        "Expected a protocol 1.0 command request.").ToJson());
                    continue;
                }

                try
                {
                    var result = Dispatch(client, request, bootstrapKey, actionNonce, conversation,
                        runPersistedConversation is not null, eventStream, out var acknowledgedReceiptCommandId);
                    var responseJson = WireEnvelope.Response(request.MessageId, result).ToJson();
                    var responseDelivered = Encoding.UTF8.GetByteCount(responseJson) <= StdioProtocol.MaximumFrameBytes;
                    if (!responseDelivered)
                        WriteFrame(output, Error(request.MessageId, "frame_too_large", "The query response exceeds the negotiated frame limit.").ToJson());
                    else WriteFrame(output, responseJson);
                    if (responseDelivered && acknowledgedReceiptCommandId is not null)
                        conversation.StartPendingAfterAck(acknowledgedReceiptCommandId);
                    eventStream.StartIfConfigured();
                }
                catch (ReplayFrameTooLargeException)
                {
                    WriteFrame(output, Error(request.MessageId, "frame_too_large",
                        "A single replay event exceeds the negotiated frame limit.").ToJson());
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException
                    or InvalidOperationException or NotSupportedException or FormatException
                    or KeyNotFoundException or OverflowException)
                {
                    WriteFrame(output, Error(request.MessageId, "unsupported_request", "Request is not supported.").ToJson());
                }
            }
            eventStream.Dispose();
            conversation.StopAndJoin();
            return 130;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException
            or System.Text.DecoderFallbackException or IOException)
        {
            diagnostics.WriteLine(localizeDiagnostic?.Invoke("stdio.host.invalid_frame")
                ?? "Stdio protocol connection ended with an invalid or incomplete frame.");
            eventStream.Dispose();
            conversation.StopAndJoin();
            return 2;
        }
        finally { eventStream.Dispose(); conversation.Dispose(); }
    }

    private static string Dispatch(IOmniClient client, WireEnvelope request, byte[]? bootstrapKey, string? actionNonce,
        ConversationExecution conversation, bool conversationExecutionAvailable, EventStreamPump eventStream,
        out string? acknowledgedReceiptCommandId)
    {
        acknowledgedReceiptCommandId = null;
        using var document = JsonDocument.Parse(request.PayloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("operation", out var operationElement)
            || operationElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException();
        var operation = operationElement.GetString();
        if (operation == StdioProtocol.EventStreamOperation)
        {
            if (!root.TryGetProperty("sessionId", out var streamSessionElement)
                || streamSessionElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("fromSequence", out var streamFromElement)
                || !streamFromElement.TryGetInt64(out var streamFrom) || streamFrom < 0
                || !root.TryGetProperty("window", out var windowElement)
                || !windowElement.TryGetInt32(out var window) || window is < 1 or > 32
                || client is not OmniServer streamServer || streamServer.ReceiptStore is null)
                throw new InvalidDataException();
            var sessionText = streamSessionElement.GetString()!;
            if (streamServer.LastSessionId()?.ToString() != sessionText) throw new InvalidDataException();
            var session = SessionId.Parse(sessionText);
            var cursor = Math.Max(1, streamFrom);
            var gap = streamServer.ReceiptStore.ReadPage(session, cursor, 1).HasGap;
            if (!eventStream.Configure(streamServer, session, cursor, window))
                throw new InvalidOperationException("event stream already active");
            return "{\"streamId\":" + Quote(eventStream.StreamId) + ",\"sessionId\":"
                + Quote(sessionText) + ",\"cursor\":" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"window\":" + window.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"hasGap\":" + (gap ? "true" : "false") + "}";
        }
        if (operation == StdioProtocol.EventAckOperation)
        {
            if (!root.TryGetProperty("streamId", out var ackId) || ackId.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("throughSequence", out var ackSequence) || !ackSequence.TryGetInt64(out var through))
                throw new InvalidDataException();
            return "{\"accepted\":" + (eventStream.Acknowledge(ackId.GetString()!, through) ? "true" : "false") + "}";
        }
        if (operation == StdioProtocol.EventReadyOperation)
        {
            if (!root.TryGetProperty("streamId", out var readyId) || readyId.ValueKind != JsonValueKind.String
                || !eventStream.MarkReady(readyId.GetString()!)) throw new InvalidDataException();
            return "{\"accepted\":true}";
        }
        if (operation == StdioProtocol.EventStopOperation)
        {
            if (!root.TryGetProperty("streamId", out var stopId) || stopId.ValueKind != JsonValueKind.String
                || !eventStream.StopSubscription(stopId.GetString()!)) throw new InvalidDataException();
            return "{\"accepted\":true}";
        }
        switch (operation)
        {
            case StdioProtocol.CommandOperation:
            {
                if (!root.TryGetProperty("command", out var commandElement)
                    || commandElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException();
                var commandJson = commandElement.GetRawText();
                var command = ParseEnvelope(commandJson);
                if (command.Version != ProtocolVersions.V1 || command.MessageType != MessageTypes.Command)
                    throw new InvalidDataException();
                using var payload = JsonDocument.Parse(command.PayloadJson);
                if (!payload.RootElement.TryGetProperty("cmd", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !StdioProtocol.IsSupportedCommand(nameElement.GetString()!))
                    throw new InvalidDataException();
                var commandName = nameElement.GetString()!;
                var trustedOnly = commandName is "conversation.start" or "conversation.new" or "conversation.resume"
                    or "conversation.rename" or "conversation.compact" or "conversation.alternative" or "session.security.set" or "session.security.default.set" or "run.interrupt" or "run.cancel" or "run.mode.select"
                    or "session.model.select" or "session.model.route.respond"
                    or "skills.activate" or "skills.release" or "memory.session.add" or "memory.session.propose"
                    or "memory.session.forget" or "memory.session.promote" or "memory.session.reject"
                    or "external.memory.consent" or "interaction.respond" or "interaction.continue"
                    or "command.invoke.user" or "command.continue" or "workflow.continue"
                    or "process.control"
                    or "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"
                    or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish" or "extensions.package";
                TrustedActionScope? trustedScope = null;
                if (root.TryGetProperty("actionProof", out var proofElement))
                {
                    if (bootstrapKey is null || actionNonce is null
                        || !TryVerifyTrustedActionProof(client, command, proofElement, bootstrapKey, actionNonce,
                            out trustedScope))
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_invalid",
                            RuntimeCommandOutcome.Rejected()));
                }
                if (trustedOnly && trustedScope is null)
                    return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                        RuntimeCommandOutcome.Rejected()));
                if (commandName is "command.invoke.user" or "command.continue" or "workflow.continue" or "process.control")
                {
                    var priorNativeCommand = FindReceiptResult(client, command);
                    if (priorNativeCommand is not null) return EncodeAck(priorNativeCommand);
                    if (commandName == "workflow.continue"
                        ? !conversation.CanAcceptWorkflowContinuation(client, command)
                        : commandName == "process.control"
                            ? !conversation.CanAcceptNativeProcessControl(client, command)
                            : !conversation.CanAcceptNativeCommand)
                        return EncodeAck(new CommandAck(command.MessageId, "busy", "conversation_turn_busy",
                            RuntimeCommandOutcome.Deferred("conversation_turn_busy")));
                }
                var responseType = payload.RootElement.TryGetProperty("responseType", out var responseTypeElement)
                    && responseTypeElement.ValueKind == JsonValueKind.String ? responseTypeElement.GetString() : null;
                if (commandName == "interaction.respond")
                {
                    // A receipt replay must return the durable original ACK before consulting
                    // the in-memory runner or attempting an explicit post-restart recovery.
                    var prior = FindReceiptResult(client, command);
                    if (prior is not null) return EncodeAck(prior);
                    if (trustedScope is null)
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                            RuntimeCommandOutcome.Rejected()));
                    var canResume = responseType switch
                    {
                        "questionnaire" => conversation.CanResumeQuestionnaire(client, trustedScope, command),
                        "choice" => conversation.CanResumeChoice(client, trustedScope, command),
                        "weakSandboxConsent" => conversation.CanResumeWeakSandboxConsent(client, trustedScope, command),
                        _ => false,
                    };
                    if (!canResume)
                    {
                        if (client is OmniServer responseServer && !responseServer.MatchesTrustedActionScope(
                                trustedScope.ExpectedSessionId, trustedScope.ExpectedRunId,
                                trustedScope.ExpectedRevision, trustedScope.CheckRevision, trustedScope.RequirePlanRun))
                            return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_scope_changed",
                                RuntimeCommandOutcome.Rejected()));
                        return EncodeAck(conversation.IsRunning
                            ? new CommandAck(command.MessageId, "busy", "conversation_turn_busy",
                                RuntimeCommandOutcome.Deferred("conversation_turn_busy"))
                            : new CommandAck(command.MessageId, "error", "conversation_questionnaire_resume_unavailable",
                                RuntimeCommandOutcome.Rejected()));
                    }
                }
                if (commandName == "interaction.continue")
                {
                    if (trustedScope is null)
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_user_action_required",
                            RuntimeCommandOutcome.Rejected()));
                    if (client is OmniServer recoveryServer && !recoveryServer.MatchesTrustedActionScope(
                            trustedScope.ExpectedSessionId, trustedScope.ExpectedRunId,
                            trustedScope.ExpectedRevision, trustedScope.CheckRevision, trustedScope.RequirePlanRun))
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_scope_changed",
                            RuntimeCommandOutcome.Rejected()));
                    if (!conversation.CanRecoverChoice(client, trustedScope, command))
                        return EncodeAck(new CommandAck(command.MessageId, "error", "interaction_recovery_unavailable",
                            RuntimeCommandOutcome.Rejected()));
                }
                if (trustedScope is not null && commandName is not ("session.input" or "conversation.start" or "conversation.new" or "conversation.resume"
                    or "conversation.rename" or "conversation.compact" or "conversation.alternative" or "session.security.set" or "session.security.default.set"
                    or "session.model.select" or "session.model.route.respond"
                    or "skills.activate" or "skills.release" or "memory.session.add" or "memory.session.propose"
                    or "memory.session.forget" or "memory.session.promote" or "memory.session.reject"
                    or "run.interrupt" or "run.cancel" or "run.mode.select" or "external.memory.consent" or "interaction.respond" or "interaction.continue"
                    or "command.invoke.user" or "command.continue" or "workflow.continue" or "attachment.upload.begin"
                    or "process.control"
                    or "attachment.upload.chunk" or "attachment.upload.finish" or "extensions.package"
                    or "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"))
                    return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_not_supported",
                        RuntimeCommandOutcome.Rejected()));
                if (commandName.StartsWith("attachment.upload.", StringComparison.Ordinal))
                {
                    if (trustedScope is null || client is not OmniServer uploadServer
                        || !uploadServer.MatchesTrustedActionScope(trustedScope.ExpectedSessionId,
                            trustedScope.ExpectedRunId, trustedScope.ExpectedRevision,
                            trustedScope.CheckRevision, trustedScope.RequirePlanRun))
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_scope_changed",
                            RuntimeCommandOutcome.Rejected()));
                    var uploaded = conversation.Upload(command, uploadServer, commandName);
                    return EncodeUploadAck(uploaded.Ack, uploaded.Reference);
                }
                if (commandName is "run.cancel" or "run.interrupt")
                {
                    if (trustedScope is null || client is not OmniServer cancelServer
                        || !cancelServer.MatchesTrustedActionScope(trustedScope.ExpectedSessionId,
                            trustedScope.ExpectedRunId, trustedScope.ExpectedRevision,
                            trustedScope.CheckRevision, trustedScope.RequirePlanRun))
                        return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_scope_changed",
                            RuntimeCommandOutcome.Rejected()));
                    if (!conversation.CancelAndJoin(TimeSpan.FromSeconds(15)))
                        return EncodeAck(new CommandAck(command.MessageId, "busy", "conversation_cancel_pending",
                            RuntimeCommandOutcome.Deferred("conversation_cancel_pending")));
                    // The interrupt event written by the runner advances the revision. The still
                    // pinned Session/Run and verified pre-cancel revision authorize this one write.
                    trustedScope = trustedScope with { CheckRevision = false };
                }
                else if (conversation.IsRunning && commandName is not ("process.control" or "session.security.set")
                    && commandName is not ("delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"))
                {
                    var duplicate = FindReceiptResult(client, command);
                    if (duplicate is not null) return EncodeAck(duplicate);
                    return EncodeAck(new CommandAck(command.MessageId, "busy", "conversation_turn_busy",
                        RuntimeCommandOutcome.Deferred("conversation_turn_busy")));
                }
                var newlyExecuted = false;
                var ack = commandName == "conversation.start"
                    ? conversation.Start(client, command, trustedScope)
                    : DispatchCommand(client, command, trustedScope, out newlyExecuted);
                if (commandName == "interaction.respond" && newlyExecuted
                    && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                {
                    if (responseType == "choice")
                    {
                        using var choice = JsonDocument.Parse(command.PayloadJson);
                        var option = choice.RootElement.GetProperty("optionId").GetString();
                        if (option is not ("approve_only" or "reject"))
                            ack = conversation.ResumeChoice(client, command, trustedScope!, ack);
                    }
                    else if (responseType == "weakSandboxConsent")
                        ack = conversation.ResumeWeakSandboxConsent(client, command, trustedScope!, ack);
                    else ack = conversation.ResumeQuestionnaire(client, command, trustedScope!, ack);
                }
                else if (commandName == "interaction.continue" && newlyExecuted
                    && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                    ack = conversation.ResumeRecoveredChoice(client, command, trustedScope!, ack);
                else if ((commandName is "command.invoke.user" or "command.continue" or "process.control") && newlyExecuted
                    && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                    conversation.ScheduleNativeCommand(client, command, ack);
                else if (commandName == "workflow.continue" && newlyExecuted
                    && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                    conversation.ScheduleNativeWorkflowContinuation(client, command, ack);
                if (newlyExecuted && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                    acknowledgedReceiptCommandId = conversation.PendingCommandForAcknowledgement(command.MessageId);
                var outcome = ack.Outcome is null ? "null" : "{\"kind\":"
                    + Quote(ack.Outcome.Kind.ToString()) + ",\"reason\":"
                    + (ack.Outcome.Reason is null ? "null" : Quote(ack.Outcome.Reason)) + "}";
                return "{\"ack\":{\"commandId\":" + Quote(ack.CommandId)
                    + ",\"status\":" + Quote(ack.Status) + ",\"error\":"
                    + (ack.Error is null ? "null" : Quote(ack.Error)) + ",\"outcome\":" + outcome
                    + ",\"firstSeq\":" + (ack.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                    + ",\"lastSeq\":" + (ack.LastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}}";
            }
            case StdioProtocol.CommandResultOperation:
            {
                if (client is not OmniServer server || server.ReceiptStore is not { } store
                    || !root.TryGetProperty("commandId", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("requestHash", out var hashElement) || hashElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException();
                var id = idElement.GetString()!;
                var hash = hashElement.GetString()!;
                var receipt = store.ReadCommandReceipt(id);
                if (receipt is null) return "{\"state\":\"not_found\"}";
                if (!string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal))
                    return "{\"state\":\"id_conflict\"}";
                if (receipt.State != OmniCore.Infrastructure.CommandReceiptState.Completed)
                    return "{\"state\":\"unknown\"}";
                var ack = AckFromReceipt(receipt);
                return "{\"state\":\"completed\",\"ack\":{\"commandId\":" + Quote(ack.CommandId)
                    + ",\"status\":" + Quote(ack.Status) + ",\"outcome\":" + Quote(ack.Outcome?.Kind.ToString() ?? "Deferred")
                    + ",\"reason\":" + (ack.Outcome?.Reason is null ? "null" : Quote(ack.Outcome.Reason))
                    + ",\"firstSeq\":" + (ack.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                    + ",\"lastSeq\":" + (ack.LastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                    + "},\"sessionId\":" + (receipt.SessionId is null ? "null" : Quote(receipt.SessionId)) + "}";
            }
            case StdioProtocol.ConversationResultOperation:
            {
                if (client is not OmniServer resultServer || resultServer.ReceiptStore is not { } resultStore
                    || !root.TryGetProperty("commandId", out var id) || id.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("requestHash", out var requestHash) || requestHash.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException();
                var commandId = id.GetString()!;
                var hash = requestHash.GetString()!;
                var receipt = resultStore.ReadCommandReceipt(commandId);
                if (receipt is null) return "{\"state\":\"not_found\"}";
                if (!string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal)) return "{\"state\":\"id_conflict\"}";
                if (receipt.State != OmniCore.Infrastructure.CommandReceiptState.Completed
                    || receipt.OutcomeKind != "Accepted") return "{\"state\":\"not_admitted\"}";
                if (receipt.ExecutionState is null && conversation.IsRunningFor(commandId)) return "{\"state\":\"running\"}";
                return "{\"state\":" + Quote(receipt.ExecutionState ?? "unknown") + ",\"code\":"
                    + (receipt.ExecutionCode is null ? "null" : Quote(receipt.ExecutionCode))
                    + ",\"sessionId\":" + (receipt.SessionId is null ? "null" : Quote(receipt.SessionId))
                    + ",\"runId\":" + (receipt.ExecutionRunId is null ? "null" : Quote(receipt.ExecutionRunId)) + "}";
            }
            case StdioProtocol.QueryOperation:
            {
                if (!root.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException();
                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || !StdioProtocol.IsSupportedQuery(name))
                    throw new InvalidDataException();
                if (StdioProtocol.TryGetQuery(name, out var descriptor)
                    && !NegotiatedCapabilities(client, bootstrapKey, actionNonce, conversationExecutionAvailable,
                        conversation.NativeChoiceCapability, conversation.NativeWeakSandboxCapability)
                        .Contains(descriptor.Capability, StringComparer.Ordinal))
                    throw new NotSupportedException();
                if (name == "humanActionScope")
                {
                    if (bootstrapKey is null || actionNonce is null || client is not OmniServer trustedServer
                        || trustedServer.ReceiptStore is null)
                        throw new NotSupportedException();
                    return "{\"name\":\"humanActionScope\",\"json\":"
                        + Quote(trustedServer.TrustedActionScopeJson()) + "}";
                }
                if (name == "interaction.recovery")
                {
                    if (!conversation.NativeChoiceCapability || bootstrapKey is null || actionNonce is null
                        || client is not OmniServer recoveryServer || recoveryServer.ReceiptStore is null)
                        throw new NotSupportedException();
                    return "{\"name\":\"interaction.recovery\",\"json\":"
                        + Quote(conversation.EncodeChoiceRecoveryStatus(recoveryServer)) + "}";
                }
                if (name == "native.catalog")
                {
                    var capabilities = NegotiatedCapabilities(client, bootstrapKey, actionNonce, conversationExecutionAvailable,
                        conversation.NativeChoiceCapability, conversation.NativeWeakSandboxCapability);
                    return "{\"name\":\"native.catalog\",\"json\":"
                        + Quote(StdioProtocol.EncodeNativeCatalog(capabilities)) + "}";
                }
                var dispatchName = StdioProtocol.TryGetQuery(name, out var queryDescriptor)
                    ? queryDescriptor.DispatchName(name) : name;
                var result = client.Query(dispatchName, CancellationToken.None);
                return "{\"name\":" + Quote(result?.Name ?? (StdioProtocol.TryGetQuery(name, out queryDescriptor)
                    ? queryDescriptor.ResultName : name)) + ",\"json\":"
                    + (result is null ? "null" : Quote(result.Json)) + "}";
            }
            case StdioProtocol.SubscribeOperation:
            {
                if (!root.TryGetProperty("fromSequence", out var fromElement)
                    || fromElement.ValueKind != JsonValueKind.Number
                    || !fromElement.TryGetInt64(out var from) || from < 0)
                    throw new InvalidDataException();
                var events = client.SubscribeSince(from);
                var replay = "{\"events\":[" + EncodeEvents(events) + "]}";
                if (Encoding.UTF8.GetByteCount(WireEnvelope.Response(request.MessageId, replay).ToJson())
                    > StdioProtocol.MaximumFrameBytes) throw new InvalidDataException();
                return replay;
            }
            case StdioProtocol.ReplayPageOperation:
            {
                if (client is not OmniServer server || server.ReceiptStore is not { } store)
                    throw new NotSupportedException();
                if (!root.TryGetProperty("fromSequence", out var fromElement)
                    || fromElement.ValueKind != JsonValueKind.Number
                    || !fromElement.TryGetInt64(out var from) || from < 0)
                    throw new InvalidDataException();
                var size = root.TryGetProperty("pageSize", out var sizeElement)
                    && sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt32(out var parsedSize)
                    ? parsedSize : 128;
                if (size is < 1 or > 256) throw new InvalidDataException();
                var hasSession = root.TryGetProperty("sessionId", out var sessionElement)
                    && sessionElement.ValueKind == JsonValueKind.String;
                var session = hasSession ? OmniCore.Domain.SessionId.Parse(sessionElement.GetString()!)
                    : server.LastSessionId();
                if (session is null)
                {
                    var next = Math.Max(1, from);
                    return "{\"sessionId\":null,\"requestedSequence\":"
                        + from.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"nextSequence\":" + next.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"highWater\":0,\"hasGap\":false,\"complete\":true,\"events\":[]}";
                }
                var highWater = root.TryGetProperty("highWater", out var highElement)
                    && highElement.ValueKind == JsonValueKind.Number && highElement.TryGetInt64(out var parsedHigh)
                    ? parsedHigh : (long?)null;
                var pageSize = size;
                var page = store.ReadPage(session, from, pageSize, highWater);
                while (true)
                {
                    var mapped = server.MapReplayPage(page.Events);
                    var result = "{\"sessionId\":" + Quote(session.ToString())
                        + ",\"requestedSequence\":" + page.RequestedSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"nextSequence\":" + page.NextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"highWater\":" + page.HighWater.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"hasGap\":" + (page.HasGap ? "true" : "false")
                        + ",\"complete\":" + (page.Complete ? "true" : "false")
                        + ",\"events\":[" + EncodeEvents(mapped) + "]}";
                    if (Encoding.UTF8.GetByteCount(WireEnvelope.Response(request.MessageId, result).ToJson())
                        <= StdioProtocol.MaximumFrameBytes) return result;
                    if (page.Events.Count <= 1) throw new ReplayFrameTooLargeException();
                    pageSize = Math.Max(1, Math.Min(page.Events.Count - 1, pageSize / 2));
                    page = store.ReadPage(session, from, pageSize, page.HighWater);
                }
            }
            case StdioProtocol.ArtifactReadOperation:
            {
                if (client is not OmniServer imageServer
                    || !root.TryGetProperty("sessionId", out var sessionElement) || sessionElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("hash", out var hashElement) || hashElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("offset", out var offsetElement) || !offsetElement.TryGetInt64(out var offset)
                    || !root.TryGetProperty("length", out var lengthElement) || !lengthElement.TryGetInt32(out var length))
                    throw new InvalidDataException();
                var chunk = imageServer.ReadAuthorizedBinaryArtifactChunk(sessionElement.GetString()!, hashElement.GetString()!, offset, length);
                if (chunk is null) throw new NotSupportedException();
                return "{\"offset\":" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"size\":" + chunk.Value.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"data\":" + Quote(Convert.ToBase64String(chunk.Value.Bytes)) + "}";
            }
            default: throw new InvalidDataException();
        }
    }


    private static string EncodeAck(CommandAck ack)
    {
        var outcome = ack.Outcome is null ? "null" : "{\"kind\":"
            + Quote(ack.Outcome.Kind.ToString()) + ",\"reason\":"
            + (ack.Outcome.Reason is null ? "null" : Quote(ack.Outcome.Reason)) + "}";
        return "{\"ack\":{\"commandId\":" + Quote(ack.CommandId)
            + ",\"status\":" + Quote(ack.Status) + ",\"error\":"
            + (ack.Error is null ? "null" : Quote(ack.Error)) + ",\"outcome\":" + outcome
            + ",\"firstSeq\":" + (ack.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
            + ",\"lastSeq\":" + (ack.LastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}}";
    }

    private static string EncodeUploadAck(CommandAck ack, ArtifactRef? reference)
    {
        var encoded = EncodeAck(ack);
        return reference is null ? encoded[..^1] + ",\"attachment\":null}"
            : encoded[..^1] + ",\"attachment\":"
                + JsonSerializer.Serialize(reference, AttachmentReferenceJsonContext.Default.ArtifactRef) + "}";
    }

    private static bool TryVerifyTrustedActionProof(IOmniClient client, WireEnvelope command, JsonElement proof,
        byte[] key, string nonce, out TrustedActionScope? scope)
    {
        scope = null;
        if (client is not OmniServer server || server.ReceiptStore is null || proof.ValueKind != JsonValueKind.Object
            || !proof.TryGetProperty("nonce", out var nonceElement) || nonceElement.ValueKind != JsonValueKind.String
            || nonceElement.GetString() != nonce
            || !proof.TryGetProperty("expectedSessionId", out var sessionElement)
            || !proof.TryGetProperty("expectedRunId", out var runElement)
            || !proof.TryGetProperty("signature", out var signatureElement) || signatureElement.ValueKind != JsonValueKind.String)
            return false;
        var expectedSession = OptionalCanonicalGuid(sessionElement);
        var expectedRun = OptionalCanonicalGuid(runElement);
        if (expectedSession.Invalid || expectedRun.Invalid) return false;
        if (!TryGetWorkspaceHash(out var workspaceHash)) return false;
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var signed = string.Join("\n", "omnicore-user-action-v1", nonce, command.MessageId, payloadHash,
            workspaceHash, expectedSession.Value ?? "-", expectedRun.Value ?? "-");
        byte[] supplied;
        try { supplied = Convert.FromHexString(signatureElement.GetString()!); }
        catch (FormatException) { return false; }
        var computed = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed));
        if (supplied.Length != computed.Length || !CryptographicOperations.FixedTimeEquals(supplied, computed)) return false;
        using var payload = JsonDocument.Parse(command.PayloadJson);
        if (!payload.RootElement.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String) return false;
        var commandName = cmd.GetString();
        if (commandName is not ("session.input" or "conversation.start" or "conversation.new" or "conversation.resume"
            or "conversation.rename" or "conversation.compact" or "conversation.alternative" or "session.security.set" or "session.security.default.set" or "run.interrupt" or "run.cancel" or "run.mode.select"
            or "session.model.select" or "session.model.route.respond"
            or "skills.activate" or "skills.release" or "memory.session.add" or "memory.session.propose"
            or "memory.session.forget" or "memory.session.promote" or "memory.session.reject"
            or "external.memory.consent" or "interaction.respond" or "interaction.continue"
            or "command.invoke.user" or "command.continue" or "workflow.continue"
            or "process.control"
            or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish" or "extensions.package"
            or "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send")) return false;
        if (commandName.StartsWith("attachment.upload.", StringComparison.Ordinal))
        {
            // Upload proofs pin the currently active Session/Run too: attachments after the
            // first turn are valid, but a grant from an earlier Run cannot be reused.
            if (!ValidUploadPayload(commandName, payload.RootElement)) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null, false, false, null);
            return true;
        }
        if ((commandName is "run.interrupt" or "run.cancel" or "run.mode.select") && expectedRun.Value is null) return false;
        var properties = payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        if (commandName == "extensions.package")
        {
            if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Except(["cmd", "action", "extensionId", "source", "trustProcess", "expectedManifestSha256", "expectedPackageSha256", "key", "value", "submission", "contributor", "previewId", "contentSha256"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String
                || action.GetString() is not ("install" or "enable" or "disable" or "uninstall" or "configure" or "update" or "rollback" or "submit-view" or "context-preview" or "context-approve" or "context-revoke")
                || !payload.RootElement.TryGetProperty(action.GetString() == "install" ? "source" : "extensionId", out var target)
                || target.ValueKind != JsonValueKind.String || target.GetString() is not { Length: > 0 and <= 1024 }) return false;
            if (action.GetString() == "update"
                && (!payload.RootElement.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
                    || source.GetString() is not { Length: > 0 and <= 1024 })) return false;
            if (action.GetString() == "submit-view")
            {
                if (!payload.RootElement.TryGetProperty("submission", out var submission) || submission.ValueKind != JsonValueKind.String
                    || submission.GetString() is not { } formJson || Encoding.UTF8.GetByteCount(formJson) > 65536) return false;
                var form = JsonSerializer.Deserialize(formJson, ExtensionJson.Default.ExtensionFormSubmission);
                if (form?.Scope is null || form.ExtensionId != target.GetString() || form.Scope.SessionId != expectedSession.Value) return false;
            }
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null, false, false, null);
            return true;
        }
        if (commandName is "command.invoke.user" or "command.continue" or "workflow.continue" or "process.control")
        {
            bool ExactCommandProperties(params string[] expected) => properties.Length == expected.Length
                && properties.Distinct(StringComparer.Ordinal).Count() == properties.Length
                && !properties.Except(expected, StringComparer.Ordinal).Any();
            bool RequiredString(string name, int maximum, out string value)
            {
                value = "";
                if (!payload.RootElement.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String
                    || node.GetString() is not { } text || text.Length == 0 || text.Length > maximum
                    || text.Any(char.IsControl)) return false;
                value = text;
                return true;
            }
            bool NullableRevision(string name) => payload.RootElement.TryGetProperty(name, out var node)
                && (node.ValueKind == JsonValueKind.Null
                    || node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value) && value > 0);

            var expectedCommandProperties = commandName is "command.invoke.user" or "process.control"
                ? new[] { "cmd", "canonicalCommandId", "arguments", "expectedSessionId", "expectedRunId",
                    "expectedModeRevision", "expectedPolicyRevision", "expectedSelectionRevision",
                    "expectedRoutingPolicyRevision", "catalogRevision", "sourceFingerprint" }
                : new[] { "cmd", "invocationId", "expectedSessionId", "expectedRunId", "expectedModeRevision",
                    "expectedPolicyRevision", "expectedSelectionRevision", "expectedRoutingPolicyRevision",
                    "catalogRevision", "sourceFingerprint" };
            var runlessExplain = commandName == "command.invoke.user"
                && RequiredString("canonicalCommandId", 256, out var runlessCommandId)
                && string.Equals(runlessCommandId, "core:explain", StringComparison.Ordinal);
            var runScopeMatches = payload.RootElement.TryGetProperty("expectedRunId", out var commandRunNode)
                && (expectedRun.Value is null
                    ? commandName == "command.invoke.user" && runlessExplain
                        && commandRunNode.ValueKind == JsonValueKind.Null
                    : commandRunNode.ValueKind == JsonValueKind.String
                        && string.Equals(commandRunNode.GetString(), expectedRun.Value, StringComparison.Ordinal));
            var modeScopeMatches = payload.RootElement.TryGetProperty("expectedModeRevision", out var commandModeNode)
                && (expectedRun.Value is not null || commandModeNode.ValueKind == JsonValueKind.Null);
            if (expectedSession.Value is null
                || !ExactCommandProperties(expectedCommandProperties)
                || !RequiredString("expectedSessionId", 36, out var commandSession)
                || commandSession != expectedSession.Value
                || !runScopeMatches
                || !modeScopeMatches
                || !NullableRevision("expectedModeRevision")
                || !NullableRevision("expectedRoutingPolicyRevision")
                || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var commandPolicyNode)
                || commandPolicyNode.ValueKind != JsonValueKind.Number
                || !commandPolicyNode.TryGetInt64(out var commandPolicyRevision) || commandPolicyRevision < 1
                || !payload.RootElement.TryGetProperty("expectedSelectionRevision", out var commandSelectionNode)
                || commandSelectionNode.ValueKind != JsonValueKind.Number
                || !commandSelectionNode.TryGetInt32(out var commandSelectionRevision) || commandSelectionRevision < 0
                || !RequiredString("catalogRevision", 64, out var commandCatalogRevision)
                || !IsSha256(commandCatalogRevision)
                || !RequiredString("sourceFingerprint", 64, out var commandSourceFingerprint)
                || !IsSha256(commandSourceFingerprint)) return false;
            if (commandName is "command.invoke.user" or "process.control")
            {
                if (!RequiredString("canonicalCommandId", 256, out var canonicalCommandId)
                    || !payload.RootElement.TryGetProperty("arguments", out var commandArguments)
                    || commandArguments.ValueKind != JsonValueKind.Array || commandArguments.GetArrayLength() > 32)
                    return false;
                var totalArgumentLength = 0;
                foreach (var argument in commandArguments.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { } text
                        || text.Length > 16_384) return false;
                    totalArgumentLength += text.Length;
                    if (totalArgumentLength > 65_536) return false;
                }
                if (commandName == "process.control"
                    && (canonicalCommandId != "core:process.control" || commandArguments.GetArrayLength() != 2
                        || commandArguments[0].ValueKind != JsonValueKind.String
                        || commandArguments[0].GetString() is not ("process.read" or "process.send_input" or "process.stop")
                        || commandArguments[1].ValueKind != JsonValueKind.String
                        || commandArguments[1].GetString() is not { Length: > 0 and <= 16_384 } processJson
                        || !IsJsonObject(processJson)
                        || !string.Equals(commandSourceFingerprint,
                            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
                            StringComparison.Ordinal))) return false;
            }
            else if (!RequiredString("invocationId", 36, out var invocationId)
                || !Guid.TryParseExact(invocationId, "D", out var invocationGuid)
                || invocationId != invocationGuid.ToString("D")) return false;

            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        if (commandName == "interaction.respond")
        {
            if (expectedSession.Value is null || expectedRun.Value is null
                || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !payload.RootElement.TryGetProperty("responseType", out var responseType)
                || responseType.ValueKind != JsonValueKind.String) return false;
            if (responseType.GetString() == "choice")
            {
                if (properties.Except(["cmd", "responseType", "interactionId", "optionId", "expectedRunId",
                    "expectedPolicyRevision"], StringComparer.Ordinal).Any() || properties.Length != 6
                    || !payload.RootElement.TryGetProperty("interactionId", out var choiceInteraction)
                    || choiceInteraction.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(choiceInteraction.GetString(), "D", out var choiceGuid)
                    || choiceInteraction.GetString() != choiceGuid.ToString("D")
                    || !payload.RootElement.TryGetProperty("optionId", out var option)
                    || option.ValueKind != JsonValueKind.String || option.GetString() is not { Length: > 0 and <= 80 } optionId
                    || optionId.Any(char.IsControl)
                    || !payload.RootElement.TryGetProperty("expectedRunId", out var choiceRun)
                    || choiceRun.ValueKind != JsonValueKind.String || choiceRun.GetString() != expectedRun.Value
                    || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var choicePolicyRevision)
                    || choicePolicyRevision.ValueKind != JsonValueKind.Number
                    || !choicePolicyRevision.TryGetInt64(out var choiceRevision) || choiceRevision < 1) return false;
                scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                    CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
                return true;
            }
            if (responseType.GetString() == "weakSandboxConsent")
            {
                static bool CanonicalChoiceId(JsonElement root, string name) =>
                    root.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParseExact(id.GetString(), "D", out var parsedId)
                    && id.GetString() == parsedId.ToString("D");
                if (properties.Length != 11
                    || properties.Except(["cmd", "responseType", "interactionId", "optionId", "expectedRunId",
                        "expectedPolicyRevision", "taskId", "laneId", "turnId", "toolCallId", "invocationId"],
                        StringComparer.Ordinal).Any()
                    || !CanonicalChoiceId(payload.RootElement, "interactionId")
                    || !CanonicalChoiceId(payload.RootElement, "taskId")
                    || !CanonicalChoiceId(payload.RootElement, "laneId")
                    || !CanonicalChoiceId(payload.RootElement, "turnId")
                    || !CanonicalChoiceId(payload.RootElement, "toolCallId")
                    || !CanonicalChoiceId(payload.RootElement, "invocationId")
                    || !payload.RootElement.TryGetProperty("optionId", out var weakOption)
                    || weakOption.ValueKind != JsonValueKind.String
                    || weakOption.GetString() is not ("consent_once" or "deny")
                    || !payload.RootElement.TryGetProperty("expectedRunId", out var weakRun)
                    || weakRun.ValueKind != JsonValueKind.String || weakRun.GetString() != expectedRun.Value
                    || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var weakPolicyRevision)
                    || weakPolicyRevision.ValueKind != JsonValueKind.Number
                    || !weakPolicyRevision.TryGetInt64(out var weakRevision) || weakRevision < 1)
                    return false;
                scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                    CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
                return true;
            }
            if (responseType.GetString() != "questionnaire"
                || properties.Except(["cmd", "responseType", "interactionId", "expectedRunId",
                    "expectedPolicyRevision", "answers", "cancelled"], StringComparer.Ordinal).Any()
                || properties.Length != 7
                || !payload.RootElement.TryGetProperty("interactionId", out var interactionId)
                || interactionId.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(interactionId.GetString(), "D", out var parsedInteractionId)
                || !string.Equals(interactionId.GetString(), parsedInteractionId.ToString("D"), StringComparison.Ordinal)
                || !payload.RootElement.TryGetProperty("expectedRunId", out var scopedRun)
                || scopedRun.ValueKind != JsonValueKind.String || scopedRun.GetString() != expectedRun.Value
                || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var policyRevision)
                || policyRevision.ValueKind != JsonValueKind.Number
                || !policyRevision.TryGetInt64(out var parsedPolicyRevision) || parsedPolicyRevision < 1
                || !payload.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() > 128
                || !payload.RootElement.TryGetProperty("cancelled", out var cancelled)
                || cancelled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        if (commandName == "interaction.continue")
        {
            if (expectedSession.Value is null || expectedRun.Value is null
                || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Except(["cmd", "interactionId", "expectedRunId", "expectedPolicyRevision"],
                    StringComparer.Ordinal).Any() || properties.Length != 4
                || !payload.RootElement.TryGetProperty("interactionId", out var recoveryInteraction)
                || recoveryInteraction.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(recoveryInteraction.GetString(), "D", out var recoveryGuid)
                || recoveryInteraction.GetString() != recoveryGuid.ToString("D")
                || !payload.RootElement.TryGetProperty("expectedRunId", out var recoveryRun)
                || recoveryRun.ValueKind != JsonValueKind.String || recoveryRun.GetString() != expectedRun.Value
                || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var recoveryRevision)
                || recoveryRevision.ValueKind != JsonValueKind.Number
                || !recoveryRevision.TryGetInt64(out var recoveryPolicyRevision) || recoveryPolicyRevision < 1)
                return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        if (commandName is "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send")
        {
            bool CanonicalGuid(string name) => payload.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(value.GetString(), "D", out var parsed)
                && value.GetString() == parsed.ToString("D");
            var expectedCount = commandName == "delegation.cancel" ? 5 : commandName == "delegation.disposition" ? 8 : 6;
            if (expectedSession.Value is null || expectedRun.Value is null
                || properties.Length != expectedCount || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Except(commandName == "delegation.cancel"
                    ? ["cmd", "expectedSessionId", "expectedRunId", "expectedPolicyRevision", "delegationId"]
                    : commandName == "delegation.disposition"
                        ? ["cmd", "expectedSessionId", "expectedRunId", "expectedPolicyRevision", "delegationId", "resultId", "outcome", "reason"]
                        : ["cmd", "expectedSessionId", "expectedRunId", "expectedPolicyRevision", "delegationId", "content"],
                    StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("expectedSessionId", out var sessionNode)
                || sessionNode.ValueKind != JsonValueKind.String || sessionNode.GetString() != expectedSession.Value
                || !payload.RootElement.TryGetProperty("expectedRunId", out var runNode)
                || runNode.ValueKind != JsonValueKind.String || runNode.GetString() != expectedRun.Value
                || !payload.RootElement.TryGetProperty("expectedPolicyRevision", out var policyNode)
                || policyNode.ValueKind != JsonValueKind.Number || !policyNode.TryGetInt64(out var policyRevision)
                || policyRevision < 1 || !CanonicalGuid("delegationId")) return false;
            if (commandName == "delegation.mailbox.send"
                && (!payload.RootElement.TryGetProperty("content", out var contentNode)
                    || contentNode.ValueKind != JsonValueKind.String
                    || contentNode.GetString() is not { Length: > 0 and <= 4096 } content
                    || content.Contains('\0'))) return false;
            if (commandName == "delegation.disposition"
                && (!CanonicalGuid("resultId")
                    || !payload.RootElement.TryGetProperty("outcome", out var outcomeNode)
                    || outcomeNode.ValueKind != JsonValueKind.String
                    || outcomeNode.GetString() is not ("Accepted" or "ReworkRequested")
                    || !payload.RootElement.TryGetProperty("reason", out var reasonNode)
                    || reasonNode.ValueKind != JsonValueKind.String
                    || reasonNode.GetString() is not { Length: > 0 and <= 4096 } reason
                    || string.IsNullOrWhiteSpace(reason))) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null,
                ExpectedPolicyRevision: policyRevision);
            return true;
        }
        if (commandName is "skills.activate" or "skills.release" or "memory.session.add" or "memory.session.propose"
            or "memory.session.forget" or "memory.session.promote" or "memory.session.reject")
        {
            if (expectedSession.Value is null
                || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !payload.RootElement.TryGetProperty("expectedSessionId", out var preferenceSession)
                || preferenceSession.ValueKind != JsonValueKind.String || preferenceSession.GetString() != expectedSession.Value
                || !payload.RootElement.TryGetProperty("expectedRevision", out var preferenceRevision)
                || preferenceRevision.ValueKind != JsonValueKind.String || !IsSha256(preferenceRevision.GetString()))
                return false;

            bool Exact(params string[] expected) => properties.Length == expected.Length
                && !properties.Except(expected, StringComparer.Ordinal).Any();
            bool Text(string name, int maximum, bool allowEmpty = false) =>
                payload.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && text.Length <= maximum && (allowEmpty || !string.IsNullOrWhiteSpace(text))
                && !text.Any(char.IsControl);
            bool Hash(string name) => payload.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && IsSha256(value.GetString());

            var valid = commandName switch
            {
                "skills.activate" => Exact("cmd", "skillId", "scope", "expectedSessionId", "expectedRevision",
                        "expectedManifestSha256", "expectedPackageContentSha256")
                    && Text("skillId", 128)
                    && System.Text.RegularExpressions.Regex.IsMatch(payload.RootElement.GetProperty("skillId").GetString()!,
                        "^[a-z0-9]+(?:[.-][a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    && ValidSkillScope(payload.RootElement.GetProperty("scope"))
                    && Hash("expectedManifestSha256") && Hash("expectedPackageContentSha256"),
                "skills.release" => Exact("cmd", "skillId", "scope", "expectedSessionId", "expectedRevision",
                        "expectedActiveManifestSha256", "expectedActivePackageContentSha256")
                    && Text("skillId", 128)
                    && System.Text.RegularExpressions.Regex.IsMatch(payload.RootElement.GetProperty("skillId").GetString()!,
                        "^[a-z0-9]+(?:[.-][a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    && ValidSkillScope(payload.RootElement.GetProperty("scope"))
                    && Hash("expectedActiveManifestSha256") && Hash("expectedActivePackageContentSha256"),
                "memory.session.add" or "memory.session.propose" => Exact("cmd", "expectedSessionId", "expectedRevision", "content")
                    && Text("content", 2048),
                "memory.session.forget" => Exact("cmd", "expectedSessionId", "expectedRevision", "memoryId")
                    && Text("memoryId", 128),
                "memory.session.promote" or "memory.session.reject" => Exact("cmd", "expectedSessionId", "expectedRevision", "candidateId")
                    && Text("candidateId", 128),
                _ => false,
            };
            if (!valid) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        if (commandName is "conversation.new" or "conversation.resume" or "conversation.compact" or "conversation.rename" or "conversation.alternative")
        {
            if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length) return false;
            var valid = commandName switch
            {
                "conversation.new" => properties.Length == 1 && properties[0] == "cmd",
                "conversation.resume" => properties.Length == 2
                    && !properties.Except(["cmd", "sessionId"], StringComparer.Ordinal).Any()
                    && CanonicalPayloadSession(payload.RootElement, "sessionId"),
                "conversation.compact" => properties.Length == 1 && properties[0] == "cmd"
                    && expectedSession.Value is not null,
                "conversation.alternative" => properties.Length == 3
                    && !properties.Except(["cmd", "checkpointId", "instruction"], StringComparer.Ordinal).Any()
                    && expectedSession.Value is not null
                    && payload.RootElement.TryGetProperty("checkpointId", out var checkpoint)
                    && checkpoint.ValueKind == JsonValueKind.String && checkpoint.GetString() is { Length: > 0 and <= 160 } checkpointId
                    && checkpointId.StartsWith(expectedSession.Value + "-", StringComparison.Ordinal)
                    && checkpointId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    && payload.RootElement.TryGetProperty("instruction", out var instruction)
                    && instruction.ValueKind == JsonValueKind.String && instruction.GetString()!.Length <= 16000,
                "conversation.rename" => properties.Length == 4
                    && !properties.Except(["cmd", "sessionId", "title", "expectedSequence"], StringComparer.Ordinal).Any()
                    && expectedSession.Value is not null
                    && payload.RootElement.TryGetProperty("sessionId", out var targetSession)
                    && targetSession.ValueKind == JsonValueKind.String && targetSession.GetString() == expectedSession.Value
                    && payload.RootElement.TryGetProperty("title", out var title)
                    && title.ValueKind == JsonValueKind.String && title.GetString() is { Length: > 0 and <= 120 } titleText
                    && !string.IsNullOrWhiteSpace(titleText) && !titleText.Any(char.IsControl)
                    && string.Equals(titleText, titleText.Trim(), StringComparison.Ordinal)
                    && payload.RootElement.TryGetProperty("expectedSequence", out var sequence)
                    && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt64(out var expectedSequence)
                    && expectedSequence > 0,
                _ => false,
            };
            if (!valid) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        if (commandName is "session.input" or "conversation.start")
        {
            var hasAttachments = commandName == "conversation.start" && HasAttachments(payload.RootElement);
            var expectedProperties = commandName == "conversation.start"
                ? hasAttachments
                    ? new[] { "cmd", "text", "mode", "expectedRevision", "expectedPolicyRevision", "expectedDefaultRevision", "attachments" }
                    : ["cmd", "text", "mode", "expectedRevision", "expectedPolicyRevision", "expectedDefaultRevision"]
                : hasAttachments
                    ? new[] { "cmd", "text", "mode", "expectedRevision", "attachments" }
                    : ["cmd", "text", "mode", "expectedRevision"];
            if (properties.Length != expectedProperties.Length || properties.Except(expectedProperties, StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || (string.IsNullOrWhiteSpace(text.GetString()) && !hasAttachments) || text.GetString()!.Length > 32_000
                || !payload.RootElement.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String
                || (mode.GetString() != "plan" && (commandName != "conversation.start" || mode.GetString() != "act"))
                || !payload.RootElement.TryGetProperty("expectedRevision", out var inputRevision)
                || (inputRevision.ValueKind != JsonValueKind.Null &&
                    (inputRevision.ValueKind != JsonValueKind.Number || !inputRevision.TryGetInt64(out var parsedInputRevision)
                     || parsedInputRevision < 0))) return false;
            if (commandName == "conversation.start")
            {
                if (!payload.RootElement.TryGetProperty("expectedPolicyRevision", out var policyRevision)
                    || !payload.RootElement.TryGetProperty("expectedDefaultRevision", out var defaultRevision)) return false;
                if (expectedSession.Value is null)
                {
                    if (policyRevision.ValueKind != JsonValueKind.Null || defaultRevision.ValueKind != JsonValueKind.String
                        || defaultRevision.GetString() is not { Length: 64 } defaultHash
                        || !defaultHash.All(Uri.IsHexDigit)) return false;
                }
                else if (policyRevision.ValueKind != JsonValueKind.Number
                    || !policyRevision.TryGetInt64(out var parsedPolicyRevision) || parsedPolicyRevision < 1
                    || defaultRevision.ValueKind != JsonValueKind.Null) return false;
            }
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash,
                inputRevision.ValueKind == JsonValueKind.Null ? null : inputRevision.GetInt64(),
                CheckRevision: true, RequirePlanRun: mode.GetString() == "plan", RequestedMode: mode.GetString(),
                RequireCurrentAct: mode.GetString() == "act");
            return true;
        }
        else if (commandName == "session.security.set")
        {
            if (expectedSession.Value is null || properties.Length != 4
                || properties.Except(["cmd", "sessionId", "category", "expectedRevision"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("sessionId", out var sessionId)
                || sessionId.ValueKind != JsonValueKind.String || sessionId.GetString() != expectedSession.Value
                || !payload.RootElement.TryGetProperty("category", out var category)
                || category.ValueKind != JsonValueKind.String
                || !Enum.TryParse<OmniCore.Domain.ModelPolicyCategory>(category.GetString(), false, out var policyCategory)
                || !Enum.IsDefined(policyCategory) || policyCategory == OmniCore.Domain.ModelPolicyCategory.Custom
                || !payload.RootElement.TryGetProperty("expectedRevision", out var policyRevision)
                || policyRevision.ValueKind != JsonValueKind.Number || !policyRevision.TryGetInt64(out var parsedPolicyRevision)
                || parsedPolicyRevision < 1) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        else if (commandName == "session.security.default.set")
        {
            if (properties.Length != 3
                || properties.Except(["cmd", "category", "expectedRevision"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("category", out var category)
                || category.ValueKind != JsonValueKind.String
                || !Enum.TryParse<OmniCore.Domain.ModelPolicyCategory>(category.GetString(), false, out var policyCategory)
                || !Enum.IsDefined(policyCategory) || policyCategory == OmniCore.Domain.ModelPolicyCategory.Custom
                || !payload.RootElement.TryGetProperty("expectedRevision", out var policyRevision)
                || policyRevision.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(policyRevision.GetString()))
                return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        else if (commandName is "session.model.select" or "session.model.route.respond")
        {
            if (expectedSession.Value is null
                || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
                return false;
            bool Revision(string propertyName, out long revision)
            {
                revision = 0;
                return payload.RootElement.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out revision) && revision >= 0;
            }
            if (!payload.RootElement.TryGetProperty("sessionId", out var selectedSession)
                || selectedSession.ValueKind != JsonValueKind.String || selectedSession.GetString() != expectedSession.Value
                || !Revision("expectedSelectionRevision", out _)
                || !Revision("expectedRoutingPolicyRevision", out var routingRevision)
                || (commandName == "session.model.route.respond" && routingRevision < 1)
                || !Revision("expectedSessionPolicyRevision", out var securityRevision) || securityRevision < 1
                || !payload.RootElement.TryGetProperty("catalogRevision", out var catalogRevision)
                || catalogRevision.ValueKind != JsonValueKind.String
                || catalogRevision.GetString() is not { Length: 64 } catalogHash || !catalogHash.All(Uri.IsHexDigit))
                return false;
            if (!payload.RootElement.TryGetProperty("expectedRunId", out var selectedRunScope)
                || selectedRunScope.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                || (selectedRunScope.ValueKind == JsonValueKind.String
                    && (!Guid.TryParseExact(selectedRunScope.GetString(), "D", out var scopeRun)
                        || selectedRunScope.GetString() != scopeRun.ToString("D")))
                || (selectedRunScope.ValueKind == JsonValueKind.Null ? null : selectedRunScope.GetString()) != expectedRun.Value)
                return false;
            if (commandName == "session.model.select")
            {
                if (properties.Except(["cmd", "sessionId", "modelId", "expectedSelectionRevision",
                    "expectedRoutingPolicyRevision", "expectedSessionPolicyRevision", "expectedRunId", "catalogRevision"], StringComparer.Ordinal).Any()
                    || properties.Length != 8
                    || !payload.RootElement.TryGetProperty("modelId", out var modelId)
                    || modelId.ValueKind != JsonValueKind.String || modelId.GetString() is not { Length: > 0 and <= 200 } modelText
                    || modelText.Any(char.IsControl)
                    ) return false;
            }
            else if (properties.Except(["cmd", "sessionId", "interactionId", "optionId", "expectedSelectionRevision",
                "expectedRoutingPolicyRevision", "expectedSessionPolicyRevision", "expectedRunId", "catalogRevision"], StringComparer.Ordinal).Any()
                || properties.Length != 9
                || !payload.RootElement.TryGetProperty("interactionId", out var interactionId)
                || interactionId.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(interactionId.GetString(), "D", out var parsedInteraction)
                || interactionId.GetString() != parsedInteraction.ToString("D")
                || !payload.RootElement.TryGetProperty("optionId", out var optionId)
                || optionId.ValueKind != JsonValueKind.String || optionId.GetString() is not ("allow_route" or "deny")) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        else if (commandName == "external.memory.consent")
        {
            if (properties.Length != 3 || properties.Except(["cmd", "decision", "scope"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String
                || decision.GetString() is not ("allow" or "revoke")
                || !payload.RootElement.TryGetProperty("scope", out var consentScope) || consentScope.ValueKind != JsonValueKind.String
                || consentScope.GetString() is not ("global" or "project")) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
                CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
            return true;
        }
        else if (commandName is "run.cancel" or "run.interrupt")
        {
            if (properties.Length != 2 || properties.Except(["cmd", "runId"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("runId", out var runId) || runId.ValueKind != JsonValueKind.String
                || runId.GetString() != expectedRun.Value) return false;
        }
        else if (commandName == "run.mode.select")
        {
            if (!payload.RootElement.TryGetProperty("runId", out var runId)
                || runId.ValueKind != JsonValueKind.String || runId.GetString() != expectedRun.Value
                || !payload.RootElement.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String
                || !payload.RootElement.TryGetProperty("expectedRevision", out var revision)
                || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var parsedRevision)
                || parsedRevision < 0) return false;

            var modeText = mode.GetString();
            var simpleModePayload = properties.Length == 4
                && !properties.Except(["cmd", "runId", "mode", "expectedRevision"], StringComparer.Ordinal).Any()
                && modeText is "plan" or "act";
            var adaptiveOrqPayload = false;
            if (modeText == "orq" && properties.Length == 13
                && !properties.Except(["cmd", "runId", "mode", "expectedRevision", "effort", "adaptive",
                    "allowedModes", "maxAgents", "maxDepth", "maxTurns", "maxToolCalls",
                    "maxElapsedSeconds", "maxSpendUsd"], StringComparer.Ordinal).Any()
                && payload.RootElement.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.String
                && effort.GetString() == "ultracode"
                && payload.RootElement.TryGetProperty("adaptive", out var adaptive) && adaptive.ValueKind == JsonValueKind.True
                && payload.RootElement.TryGetProperty("allowedModes", out var allowedModes)
                && allowedModes.ValueKind == JsonValueKind.String
                && allowedModes.GetString() is { Length: > 0 and <= 64 } allowedText
                && payload.RootElement.TryGetProperty("maxSpendUsd", out var maxSpend)
                && maxSpend.ValueKind == JsonValueKind.Number && maxSpend.TryGetDecimal(out var spend) && spend >= 0)
            {
                var allowedModeValues = allowedText.Split(',').Select(value => value.Trim()).ToArray();
                adaptiveOrqPayload = allowedModeValues.Length > 0
                    && allowedModeValues.All(value => value is "plan" or "act" or "orq")
                    && allowedModeValues.Distinct(StringComparer.Ordinal).Count() == allowedModeValues.Length
                    && allowedModeValues.Contains("orq", StringComparer.Ordinal)
                    && PositiveInt("maxAgents") && PositiveInt("maxDepth")
                    && PositiveInt("maxTurns") && PositiveInt("maxToolCalls")
                    && PositiveInt("maxElapsedSeconds");
            }
            bool PositiveInt(string name) => payload.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0;
            if (!simpleModePayload && !adaptiveOrqPayload) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, parsedRevision,
                CheckRevision: true, RequirePlanRun: false, RequestedMode: modeText);
            return true;
        }
        else
        {
            if (properties.Length != 4 || properties.Except(["cmd", "runId", "mode", "expectedRevision"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("runId", out var runId) || runId.ValueKind != JsonValueKind.String
                || runId.GetString() != expectedRun.Value
                || !payload.RootElement.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String
                || mode.GetString() is not ("plan" or "act")
                || !payload.RootElement.TryGetProperty("expectedRevision", out var revision)
                || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var parsedRevision) || parsedRevision < 0)
                return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, parsedRevision,
                CheckRevision: true, RequirePlanRun: false, RequestedMode: mode.GetString());
            return true;
        }
        scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
            CheckRevision: false, RequirePlanRun: false, RequestedMode: null);
        return true;
    }

    private static (string? Value, bool Invalid) OptionalCanonicalGuid(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return (null, false);
        if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out var value))
            return (null, true);
        var canonical = value.ToString("D");
        return (string.Equals(element.GetString(), canonical, StringComparison.Ordinal) ? canonical : null,
            !string.Equals(element.GetString(), canonical, StringComparison.Ordinal));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool ValidSkillScope(JsonElement element) => element.ValueKind == JsonValueKind.String
        && Enum.TryParse<OmniCore.Domain.ScopeLevel>(element.GetString(), ignoreCase: false, out var scope)
        && scope is OmniCore.Domain.ScopeLevel.BuiltIn or OmniCore.Domain.ScopeLevel.User
            or OmniCore.Domain.ScopeLevel.Project or OmniCore.Domain.ScopeLevel.Workspace or OmniCore.Domain.ScopeLevel.Session;

    private static bool CanonicalPayloadSession(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
        && Guid.TryParseExact(element.GetString(), "D", out var id)
        && string.Equals(element.GetString(), id.ToString("D"), StringComparison.Ordinal);

    private static bool HasAttachments(JsonElement root) => root.TryGetProperty("attachments", out var attachments)
        && attachments.ValueKind == JsonValueKind.Array && attachments.GetArrayLength() is > 0 and <= AttachmentReferences.MaximumCount;

    private static bool ValidUploadPayload(string commandName, JsonElement root)
    {
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (!root.TryGetProperty("uploadId", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(id.GetString(), "D", out _)) return false;
        if (commandName == "attachment.upload.begin")
            return names.Length == 5 && !names.Except(["cmd", "uploadId", "fileName", "size", "sha256"], StringComparer.Ordinal).Any()
                && root.TryGetProperty("fileName", out var fileName) && fileName.ValueKind == JsonValueKind.String
                && fileName.GetString()!.Length is > 0 and <= 180 && !fileName.GetString()!.Any(char.IsControl)
                && root.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)
                && bytes is > 0 and <= AttachmentContentMaterializer.MaximumBytes
                && root.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String
                && hash.GetString()!.Length == 64 && hash.GetString()!.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (commandName == "attachment.upload.chunk")
            return names.Length == 4 && !names.Except(["cmd", "uploadId", "index", "data"], StringComparer.Ordinal).Any()
                && root.TryGetProperty("index", out var index) && index.TryGetInt32(out var chunkIndex) && chunkIndex >= 0
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                && data.GetString()!.Length <= AttachmentUploadManager.MaximumChunkBytes * 4 / 3 + 8;
        return names.Length == 2 && !names.Except(["cmd", "uploadId"], StringComparer.Ordinal).Any();
    }

    private sealed record TrustedActionScope(string? ExpectedSessionId, string? ExpectedRunId, string WorkspaceHash,
        long? ExpectedRevision, bool CheckRevision, bool RequirePlanRun, string? RequestedMode,
        bool RequireCurrentAct = false, long? ExpectedPolicyRevision = null);

    private static CommandAck DispatchCommand(IOmniClient client, WireEnvelope command, TrustedActionScope? trustedScope,
        out bool executed, string? executionPayloadJson = null)
    {
        executed = false;
        if (client is not OmniServer server || server.ReceiptStore is not { } store)
            return trustedScope is null ? client.Send(command, CancellationToken.None)
                : new CommandAck(command.MessageId, "error", "trusted_action_unavailable", RuntimeCommandOutcome.Rejected());

        // Do not reserve a receipt while another live owner prevents execution: otherwise a
        // transient busy response would poison the command ID as an ambiguous result.
        if (!server.EnsureWriterOwnershipForCommand())
            return server.WriterOwnershipFailureAck(command.MessageId);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var scope = store.CommandReceiptScope;
        var expectedSession = server.LastSessionId()?.ToString();
        var reservation = store.ReserveCommandReceipt(command.MessageId, hash, scope, expectedSession);
        if (trustedScope is null && reservation.State != OmniCore.Infrastructure.CommandReceiptState.Reserved
            && !string.Equals(reservation.Receipt.SessionId, expectedSession, StringComparison.Ordinal))
            return new CommandAck(command.MessageId, "error", "command_session_conflict",
                RuntimeCommandOutcome.Rejected());
        if (reservation.State == OmniCore.Infrastructure.CommandReceiptState.IdConflict)
            return new CommandAck(command.MessageId, "error", "command_id_conflict",
                RuntimeCommandOutcome.Rejected());
        if (reservation.State == OmniCore.Infrastructure.CommandReceiptState.Completed)
            return AckFromReceipt(reservation.Receipt);
        if (reservation.State != OmniCore.Infrastructure.CommandReceiptState.Reserved)
            return new CommandAck(command.MessageId, "unknown", "command_result_unknown",
                RuntimeCommandOutcome.Deferred("command_result_unknown"));

        if (trustedScope is not null && !server.MatchesTrustedActionScope(
            trustedScope.ExpectedSessionId, trustedScope.ExpectedRunId, trustedScope.ExpectedRevision,
            trustedScope.CheckRevision, trustedScope.RequirePlanRun))
        {
            var rejected = new CommandAck(command.MessageId, "error", "trusted_action_scope_changed",
                RuntimeCommandOutcome.Rejected());
            store.CompleteCommandReceipt(command.MessageId, hash, rejected.Status, rejected.Outcome?.Kind.ToString(),
                rejected.Error, rejected.FirstSeq, rejected.LastSeq, scope, server.LastSessionId()?.ToString());
            return rejected;
        }
        var currentSession = server.LastSessionId();
        if (trustedScope?.ExpectedPolicyRevision is { } expectedPolicy
            && (currentSession is null || server.ReadSessionSecurityPolicy(currentSession)?.PolicyRevision != expectedPolicy))
        {
            var rejected = new CommandAck(command.MessageId, "error", "trusted_action_policy_changed",
                RuntimeCommandOutcome.Rejected());
            store.CompleteCommandReceipt(command.MessageId, hash, rejected.Status, rejected.Outcome?.Kind.ToString(),
                rejected.Error, rejected.FirstSeq, rejected.LastSeq, scope, server.LastSessionId()?.ToString());
            return rejected;
        }
        if (trustedScope is { RequireCurrentAct: true, ExpectedRunId: not null }
            && server.CurrentRunMode() != OmniCore.Domain.RunMode.Act)
        {
            var rejected = new CommandAck(command.MessageId, "error", "trusted_action_mode_changed",
                RuntimeCommandOutcome.Rejected());
            store.CompleteCommandReceipt(command.MessageId, hash, rejected.Status, rejected.Outcome?.Kind.ToString(),
                rejected.Error, rejected.FirstSeq, rejected.LastSeq, scope, server.LastSessionId()?.ToString());
            return rejected;
        }

        var executionCommand = executionPayloadJson is null ? command
            : WireEnvelope.Command(command.MessageId, executionPayloadJson);
        executed = true;
        var ack = trustedScope is null ? client.Send(executionCommand, CancellationToken.None)
            : ((ITrustedUserActionClient)client).SendUserAction(executionCommand, CancellationToken.None);
        store.CompleteCommandReceipt(command.MessageId, hash, ack.Status, ack.Outcome?.Kind.ToString(), ack.Outcome?.Reason,
            ack.FirstSeq, ack.LastSeq, scope, server.LastSessionId()?.ToString());
        return ack;
    }

    private static CommandAck? FindReceiptResult(IOmniClient client, WireEnvelope command)
    {
        if (client is not OmniServer { ReceiptStore: { } store }) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var receipt = store.ReadCommandReceipt(command.MessageId);
        if (receipt is null) return null;
        if (!string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal))
            return new CommandAck(command.MessageId, "error", "command_id_conflict", RuntimeCommandOutcome.Rejected());
        if (receipt.State != OmniCore.Infrastructure.CommandReceiptState.Completed)
            return new CommandAck(command.MessageId, "unknown", "command_result_unknown",
                RuntimeCommandOutcome.Deferred("command_result_unknown"));
        return AckFromReceipt(receipt);
    }

    private sealed class ConversationExecution(
        Func<PersistedConversationCommand, CancellationToken, Task<int>>? runner,
        Func<string, string?>? validateAttachments) : IDisposable
    {
        private sealed record ChoiceRecovery(PersistedConversationCommand Binding, string InteractionId,
            string RunId, long PolicyRevision, string Kind, string OptionId);

        private readonly object _gate = new();
        private CancellationTokenSource? _cancellation;
        private Task<int>? _task;
        private sealed record PendingNativeProcessControl(OmniServer Server, string CommandId,
            string RequestHash, string SessionId, string RunId, string ToolName, string ArgumentsJson,
            PersistedConversationCommand Binding, Task<int>? PriorTask);
        private PendingNativeProcessControl? _pendingNativeProcessControl;
        private Task<int>? _nativeProcessControlTask;
        private string? _nativeProcessControlCommandId;
        private PendingContinuation? _pendingContinuation;
        private string? _commandId;
        private PersistedConversationCommand? _deferredConversation;
        private bool _nativeChoiceCapability;
        private bool _nativeWeakSandboxCapability;
        private readonly AttachmentUploadManager _uploads = new();
        private sealed record PendingContinuation(OmniServer Server, PersistedConversationCommand Binding,
            string ReceiptCommandId, string ReceiptRequestHash);

        public void SetNativeChoiceCapability(bool supported)
        {
            lock (_gate) _nativeChoiceCapability = supported;
        }
        public bool NativeChoiceCapability { get { lock (_gate) return _nativeChoiceCapability; } }
        public void SetNativeWeakSandboxCapability(bool supported)
        {
            lock (_gate) _nativeWeakSandboxCapability = supported && _nativeChoiceCapability;
        }
        public bool NativeWeakSandboxCapability { get { lock (_gate) return _nativeWeakSandboxCapability; } }

        public bool CanAcceptNativeCommand
        {
            get { lock (_gate) return _pendingContinuation is null && _task is not { IsCompleted: false }
                && _deferredConversation is null && _pendingNativeProcessControl is null
                && _nativeProcessControlTask is not { IsCompleted: false }; }
        }

        public bool CanAcceptNativeProcessControl(IOmniClient client, WireEnvelope command)
        {
            if (client is not OmniServer server) return false;
            try
            {
                using var document = JsonDocument.Parse(command.PayloadJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("expectedSessionId", out var sessionNode) || sessionNode.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(sessionNode.GetString(), "D", out var sessionGuid)
                    || !root.TryGetProperty("expectedRunId", out var runNode) || runNode.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(runNode.GetString(), "D", out var runGuid)
                    || !root.TryGetProperty("arguments", out var actionArgs) || actionArgs.ValueKind != JsonValueKind.Array
                    || actionArgs.GetArrayLength() != 2 || actionArgs[0].ValueKind != JsonValueKind.String
                    || actionArgs[1].ValueKind != JsonValueKind.String) return false;
                lock (_gate)
                {
                    if (_pendingContinuation is not null || _deferredConversation is not null
                        || _pendingNativeProcessControl is not null
                        || _nativeProcessControlTask is { IsCompleted: false }) return false;
                }
                return server.CanExecuteNativeProcessControl(new SessionId(sessionGuid), new RunId(runGuid),
                    actionArgs[0].GetString()!, actionArgs[1].GetString()!);
            }
            catch (Exception failure) when (failure is JsonException or ArgumentException or FormatException)
            { return false; }
        }

        public bool CanAcceptWorkflowContinuation(IOmniClient client, WireEnvelope command)
        {
            if (client is not OmniServer server) return false;
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false }) return false;
            }
            if (!TryReadInvocationId(command.PayloadJson, out var invocationGuid)) return false;
            return server.NativeWorkflowContinuationIsAllowed(new CommandId(invocationGuid), out _, out _, out _);
        }

        public bool IsRunning { get { lock (_gate) return _pendingContinuation is not null || _task is { IsCompleted: false }
            || _pendingNativeProcessControl is not null || _nativeProcessControlTask is { IsCompleted: false }; } }
        public bool IsRunningFor(string commandId) { lock (_gate) return _commandId == commandId && _task is { IsCompleted: false }
            || _nativeProcessControlCommandId == commandId && _nativeProcessControlTask is { IsCompleted: false }; }
        public (CommandAck Ack, ArtifactRef? Reference) Upload(WireEnvelope command, OmniServer server, string commandName) =>
            _uploads.Dispatch(server, command, commandName);

        public string EncodeChoiceRecoveryStatus(OmniServer server)
        {
            var weakField = NativeWeakSandboxCapability
                ? ",\"weakSandbox\":" + EncodeWeakSandboxRecovery(server)
                : string.Empty;
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false })
                    return "{\"state\":\"none\",\"choices\":[]" + weakField + "}";
            }
            var recovery = ReadChoiceRecovery(server, null, null, null);
            if (recovery is null)
            {
                var blocked = BlockedChoiceRecoveryReason(server);
                return blocked is null ? "{\"state\":\"none\",\"choices\":[]" + weakField + "}"
                    : "{\"state\":\"blocked\",\"reason\":" + Quote(blocked)
                        + ",\"runId\":" + Quote(server.LastRunId()?.ToString() ?? "")
                        + ",\"policyRevision\":" + (server.LastSessionId() is { } session
                            ? server.ReadSessionSecurityPolicy(session)?.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"
                            : "null") + ",\"choices\":[]" + weakField + "}";
            }
            return "{\"state\":\"ready\",\"choices\":[{\"interactionId\":" + Quote(recovery.InteractionId)
                + ",\"runId\":" + Quote(recovery.RunId) + ",\"policyRevision\":"
                + recovery.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"kind\":" + Quote(recovery.Kind) + ",\"optionId\":" + Quote(recovery.OptionId) + "}]"
                + weakField + "}";
        }

        private static string EncodeWeakSandboxRecovery(OmniServer server)
        {
            var session = server.LastSessionId();
            var run = server.LastRunId();
            var revision = session is { } selectedSession ? server.ReadSessionSecurityPolicy(selectedSession)?.PolicyRevision : null;
            if (session is null || run is null || revision is null || revision < 1)
                return "{\"state\":\"none\"}";
            var codecs = server.AcquireCodecs();
            var events = server.AcquireStore().ReadFrom(session, 1);
            var weakRequests = events.Where(evt => evt.RunId == run && codecs.Decode(evt) is InteractionRequested request
                    && request.Kind == InteractionKind.WeakSandboxConsent)
                .Select(evt => (Event: evt, Request: (InteractionRequested)codecs.Decode(evt)))
                .Where(item => !events.Any(other => other.RunId == run
                    && codecs.Decode(other) is InteractionResolved resolved
                    && resolved.InteractionId == item.Request.InteractionId)
                    && !events.Any(other => other.RunId == run
                    && codecs.Decode(other) is InteractionExpired expired
                    && expired.InteractionId == item.Request.InteractionId))
                .ToArray();
            if (weakRequests.Length == 0)
                return EncodeAcceptedWeakSandboxRecovery(events, codecs, server, session, run);
            if (weakRequests.Length != 1 || weakRequests[0].Event.TaskId is not { } task
                || weakRequests[0].Event.LaneId is not { } lane || weakRequests[0].Event.TurnId is not { } turn
                || weakRequests[0].Event.ToolCallId is not { } toolCall)
                return "{\"state\":\"blocked\",\"reasonCode\":\"weak_consent_binding_unavailable\"}";

            var request = weakRequests[0].Request;
            if (!ProtocolMapper.OptionIds(request.OptionsJson).SequenceEqual(["consent_once", "deny"], StringComparer.Ordinal)
                || request.SubjectJson.Length > 8_192)
                return "{\"state\":\"blocked\",\"reasonCode\":\"weak_consent_request_invalid\"}";
            string? executable = null;
            string? workingDirectory = null;
            try
            {
                using var subject = JsonDocument.Parse(request.SubjectJson);
                var root = subject.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String
                    && operation.GetString() == "sandbox.weak_consent"
                    && root.TryGetProperty("risk", out var risk) && risk.ValueKind == JsonValueKind.String
                    && risk.GetString() == "high"
                    && root.TryGetProperty("executable", out var executableNode) && executableNode.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("workingDirectory", out var directoryNode) && directoryNode.ValueKind == JsonValueKind.String)
                {
                    executable = executableNode.GetString();
                    workingDirectory = directoryNode.GetString();
                }
            }
            catch (JsonException) { }
            if (string.IsNullOrWhiteSpace(executable) || executable.Length > 2_048
                || executable.Any(char.IsControl) || string.IsNullOrWhiteSpace(workingDirectory)
                || workingDirectory.Length > 2_048 || workingDirectory.Any(char.IsControl))
                return "{\"state\":\"blocked\",\"reasonCode\":\"weak_consent_subject_unavailable\"}";

            var invocationCandidates = events.Where(evt => evt.RunId == run
                    && codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
                    && admitted.SessionId == session && admitted.RunId == run && admitted.Kind == "compiledWorkflow")
                .Select(evt => ((NativeCommandInvocationAdmitted)codecs.Decode(evt)).InvocationId)
                .Distinct().Where(invocation => server.CanResolveNativeWeakSandboxConsent(session, run, task, lane, turn,
                    toolCall, request.InteractionId, invocation, "consent_once", revision.Value))
                .Take(2).ToArray();
            if (invocationCandidates.Length != 1)
                return "{\"state\":\"blocked\",\"reasonCode\":\"weak_consent_scope_unavailable\"}";

            return "{\"state\":\"pending\",\"sessionId\":" + Quote(session.ToString())
                + ",\"runId\":" + Quote(run.ToString()) + ",\"policyRevision\":"
                + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"invocationId\":" + Quote(invocationCandidates[0].ToString())
                + ",\"interactionId\":" + Quote(request.InteractionId.ToString())
                + ",\"taskId\":" + Quote(task.ToString()) + ",\"laneId\":" + Quote(lane.ToString())
                + ",\"turnId\":" + Quote(turn.ToString()) + ",\"toolCallId\":" + Quote(toolCall.ToString())
                + ",\"executable\":" + Quote(executable) + ",\"workingDirectory\":" + Quote(workingDirectory)
                + ",\"optionIds\":[\"consent_once\",\"deny\"]}";
        }

        private static string EncodeAcceptedWeakSandboxRecovery(IReadOnlyList<DomainEvent> events,
            IEventCodecRegistry codecs, OmniServer server, SessionId session, RunId run)
        {
            foreach (var requestEvent in events.Where(evt => evt.RunId == run
                         && codecs.Decode(evt) is InteractionRequested request
                         && request.Kind == InteractionKind.WeakSandboxConsent))
            {
                if (codecs.Decode(requestEvent) is not InteractionRequested request) continue;
                var resolvedEvent = events.SingleOrDefault(evt => evt.RunId == run
                    && codecs.Decode(evt) is InteractionResolved resolved
                    && resolved.InteractionId == request.InteractionId);
                if (resolvedEvent is null || codecs.Decode(resolvedEvent) is not InteractionResolved choice
                    || choice.Cause != InteractionCause.User || choice.IsQuestionnaire || choice.OptionId != "consent_once"
                    || resolvedEvent.Causation is not CommandCausation responseCause) continue;
                var receipt = server.ReceiptStore?.ReadCommandReceipt(responseCause.CommandId.ToString());
                if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                        OutcomeKind: "Accepted", ExecutionState: null or "cancelled",
                        FirstSequence: { } first, LastSequence: { } last }
                    || receipt.SessionId != session.ToString() || resolvedEvent.Sequence < first || resolvedEvent.Sequence > last
                    || !events.Any(evt => evt.RunId == run && codecs.Decode(evt) is RunInteractionResumed resumed
                        && resumed.InteractionId == request.InteractionId && resumed.CommandId == responseCause.CommandId.ToString()
                        && evt.Sequence >= first && evt.Sequence <= last)) continue;
                var toolEvents = events.Where(evt => evt.RunId == run && evt.TaskId == requestEvent.TaskId
                        && evt.LaneId == requestEvent.LaneId && evt.TurnId == requestEvent.TurnId
                        && evt.ToolCallId == requestEvent.ToolCallId && evt.Sequence > resolvedEvent.Sequence)
                    .Select(evt => codecs.Decode(evt)).ToArray();
                if (toolEvents.Any(item => item is ToolCallSucceeded or ToolCallFailed)) continue;
                var reason = toolEvents.Any(item => item is ToolCallStarted or ToolCallEffectUnknown
                    or ToolCallReconciled { Outcome: ReconciliationOutcome.Conflict or ReconciliationOutcome.Unresolvable })
                    ? "weak_consent_effect_outcome_unknown" : "weak_consent_response_not_resumed";
                return "{\"state\":\"blocked\",\"reasonCode\":" + Quote(reason) + "}";
            }
            return "{\"state\":\"none\"}";
        }

        private static string? BlockedChoiceRecoveryReason(OmniServer server)
        {
            var session = server.LastSessionId();
            var run = server.LastRunId();
            if (session is null || run is null) return null;
            var store = server.AcquireStore();
            var codecs = server.AcquireCodecs();
            var events = store.ReadFrom(session, 1);
            if (RunProjection.Replay(session, run, codecs, events).State != RunState.Running) return null;
            var requests = new Dictionary<InteractionId, DomainEvent>();
            foreach (var evt in events.Where(evt => evt.RunId == run))
            {
                if (codecs.Decode(evt) is InteractionRequested request
                    && request.Kind is InteractionKind.Permission or InteractionKind.PlanApproval)
                {
                    if (!requests.TryAdd(request.InteractionId, evt)) return "response_receipt_unknown";
                }
            }
            foreach (var evt in events.Where(item => item.RunId == run).OrderBy(item => item.Sequence))
            {
                if (codecs.Decode(evt) is not InteractionResolved resolved || !requests.TryGetValue(resolved.InteractionId, out var requestEvent)
                    || evt.Causation is not CommandCausation cause) continue;
                var request = (InteractionRequested)codecs.Decode(requestEvent);
                var responseInput = events.FirstOrDefault(item => item.RunId == run
                    && item.Causation is CommandCausation inputCause && inputCause.CommandId == cause.CommandId
                    && codecs.Decode(item) is UserInputReceived input
                    && RunControlService.IsNativeChoiceResponse(input, request.Kind));
                // Legacy interaction replies can resolve the same durable interaction without a native
                // choice command/receipt. They are history, not an interrupted native recovery attempt.
                if (responseInput is null) continue;
                var receipt = server.ReceiptStore?.ReadCommandReceipt(cause.CommandId.ToString());
                if (receipt is null || receipt.State != OmniCore.Infrastructure.CommandReceiptState.Completed
                    || receipt.OutcomeKind != "Accepted" || receipt.SessionId != session.ToString()
                    || receipt.FirstSequence is not { } firstSequence || receipt.LastSequence is not { } lastSequence
                    || evt.Sequence < firstSequence || evt.Sequence > lastSequence
                    || responseInput.Sequence < firstSequence || responseInput.Sequence > lastSequence)
                    return "response_receipt_unknown";
                if (request.Kind == InteractionKind.Permission)
                {
                    var permission = events.FirstOrDefault(item => item.RunId == run && item.TurnId == requestEvent.TurnId
                        && codecs.Decode(item) is PermissionRequested permissionRequest
                        && permissionRequest.InteractionId == resolved.InteractionId);
                    if (permission is not null && permission.ToolCallId is { } toolCall
                        && events.Any(item => item.RunId == run && item.ToolCallId == toolCall
                            && item.Sequence > permission.Sequence
                            && (codecs.Decode(item) is ToolCallEffectUnknown
                                || codecs.Decode(item) is ToolCallReconciled { Outcome: ReconciliationOutcome.Conflict or ReconciliationOutcome.Unresolvable })))
                        return "effect_outcome_unknown";
                }
                // A completed continuation, or one that reached a later durable input request, is
                // ordinary history. It must not be presented as a recovery action for this response.
                if (receipt.ExecutionState == "completed"
                    || receipt.ExecutionState == "deferred" && receipt.ExecutionCode == "input_required") continue;
                if (receipt.ExecutionState is not (null or "cancelled")) return "response_receipt_unknown";
                if (!HasStartedContinuation(events, codecs, run, requestEvent, request, responseInput.Sequence)) continue;
                return "continuation_already_started";
            }
            return null;
        }

        public bool CanRecoverChoice(IOmniClient client, TrustedActionScope scope, WireEnvelope command)
        {
            if (client is not OmniServer server || runner is null || !_nativeChoiceCapability
                || scope.ExpectedSessionId is not { } session || scope.ExpectedRunId is not { } run) return false;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            if (!CanonicalGuid(root, "interactionId", out var interaction)
                || !CanonicalGuid(root, "expectedRunId", out var expectedRun)
                || expectedRun != run
                || !root.TryGetProperty("expectedPolicyRevision", out var revisionElement)
                || !revisionElement.TryGetInt64(out var revision) || revision < 1) return false;
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false }) return false;
                return ReadChoiceRecovery(server, session, run, revision) is { } candidate
                    && candidate.InteractionId == interaction;
            }
        }

        private ChoiceRecovery? ReadChoiceRecovery(OmniServer server, string? expectedSession,
            string? expectedRun, long? expectedRevision)
        {
            var sessionId = server.LastSessionId();
            var runId = server.LastRunId();
            var policy = sessionId is { } session ? server.ReadSessionSecurityPolicy(session) : null;
            if (sessionId is null || runId is null || policy is null
                || expectedSession is not null && sessionId.ToString() != expectedSession
                || expectedRun is not null && runId.ToString() != expectedRun
                || expectedRevision is not null && policy.PolicyRevision != expectedRevision
                || new RunControlService(server.AcquireStore(), server.AcquireCodecs()).ActiveRun(sessionId) != runId)
                return null;
            var codecs = server.AcquireCodecs();
            var events = server.AcquireStore().ReadFrom(sessionId, 1);
            var run = runId;
            var projection = RunProjection.Replay(sessionId, run, codecs, events);
            var rootLane = projection.RootTask is { } rootTask
                ? LaneProjection.Replay(codecs, events).ForTask(rootTask).FirstOrDefault()?.Id : null;
            if (projection.State != RunState.Running || rootLane is null) return null;
            var pending = new Dictionary<InteractionId, DomainEvent>();
            var choices = new List<ChoiceRecovery>();
            foreach (var evt in events.Where(item => item.RunId == run).OrderBy(item => item.Sequence))
            {
                switch (codecs.Decode(evt))
                {
                    case InteractionRequested request when request.Kind is InteractionKind.Permission or InteractionKind.PlanApproval:
                        pending[request.InteractionId] = evt;
                        break;
                    case InteractionResolved resolved when pending.Remove(resolved.InteractionId, out var requestedEvent):
                    {
                        var request = (InteractionRequested)codecs.Decode(requestedEvent);
                        var targetLane = requestedEvent.LaneId;
                        var targetTask = requestedEvent.TaskId;
                        var targetToolCall = requestedEvent.ToolCallId;
                        var nativeLaneAllowed = targetLane == rootLane && targetTask == projection.RootTask
                            || request.Kind == InteractionKind.Permission && targetLane is { } childLane
                                && targetTask is { } childTask && server.AcceptedDelegationForChild(sessionId,
                                    run, childTask, childLane) is not null;
                        if (request.OptionsJson is null || !nativeLaneAllowed
                            || resolved.IsQuestionnaire || resolved.Cause != InteractionCause.User
                            || request.Kind == InteractionKind.Permission
                                && resolved.OptionId is not ("allow_once" or "allow_run" or "allow_workspace" or "deny")
                            || request.Kind == InteractionKind.PlanApproval
                                && resolved.OptionId is not ("approve_execute" or "continue_planning")) break;
                        var commandCause = evt.Causation as CommandCausation;
                        if (commandCause is null) break;
                        var responseReceipt = server.ReceiptStore?.ReadCommandReceipt(commandCause.CommandId.ToString());
                        if (responseReceipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                                OutcomeKind: "Accepted", FirstSequence: { } responseFirst,
                                LastSequence: { } responseLast }
                            || responseReceipt.ExecutionState is not (null or "cancelled")
                            || responseReceipt.SessionId != sessionId.ToString()
                            || evt.Sequence < responseFirst || evt.Sequence > responseLast) break;
                        var responseInput = events.SingleOrDefault(item => item.Sequence >= responseFirst
                            && item.Sequence <= responseLast && item.RunId == run && item.Causation is CommandCausation inputCause
                            && inputCause.CommandId == commandCause.CommandId
                            && codecs.Decode(item) is UserInputReceived input
                            && RunControlService.IsNativeChoiceResponse(input, request.Kind));
                        if (responseInput is null || HasStartedContinuation(events, codecs, run,
                            requestedEvent, request, responseInput.Sequence)) break;
                        var sourceTurnId = requestedEvent.TurnId;
                        if (sourceTurnId is null && request.Kind == InteractionKind.PlanApproval)
                            sourceTurnId = FindAcceptedPlanProposalTurn(events, codecs, run, requestedEvent, rootLane);
                        if (sourceTurnId is null) break;
                        var sourceTurnStart = events.FirstOrDefault(item => item.RunId == run
                            && item.TurnId == sourceTurnId && item.LaneId == targetLane
                            && codecs.Decode(item) is TurnStarted);
                        if (sourceTurnStart is null) break;
                        var previousTurnTerminal = events.Where(item => item.RunId == run
                                && item.Sequence < sourceTurnStart.Sequence && item.LaneId == targetLane)
                            .Where(item => codecs.Decode(item) is TurnCompleted or TurnAbandoned or TurnInterrupted)
                            .Select(item => item.Sequence).DefaultIfEmpty(0).Max();
                        var sourceInput = events.Where(item => item.RunId == run
                                && item.Sequence > previousTurnTerminal && item.Sequence < sourceTurnStart.Sequence
                                && codecs.Decode(item) is UserInputReceived)
                            .OrderBy(item => item.Sequence).LastOrDefault();
                        if (sourceInput?.Causation is not CommandCausation sourceCause) break;
                        var sourceReceipt = server.ReceiptStore?.ReadCommandReceipt(sourceCause.CommandId.ToString());
                        if (sourceReceipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                                OutcomeKind: "Accepted", FirstSequence: { } sourceFirst, LastSequence: { } sourceLast }
                            || sourceReceipt.SessionId != sessionId.ToString()
                            || sourceInput.Sequence < sourceFirst || sourceInput.Sequence > sourceLast
                            || sourceReceipt.ExecutionRunId is { } boundRun && boundRun != run.ToString()) break;
                        string? turnId = null;
                        if (request.Kind == InteractionKind.Permission)
                        {
                            if (requestedEvent.TurnId is not { } permissionTurn
                                || targetTask is null || targetLane is null || targetToolCall is null
                                || !IsOpenTurn(events, codecs, run, permissionTurn)) break;
                            turnId = permissionTurn.ToString();
                        }
                        var binding = new PersistedConversationCommand(sourceReceipt.CommandId,
                            sourceReceipt.RequestHash, sessionId.ToString(), run.ToString(), sourceFirst, sourceLast,
                            ContinuationInteractionId: request.InteractionId.ToString(),
                            ContinuationCommandId: commandCause.CommandId.ToString(),
                            ContinuationRequestHash: responseReceipt.RequestHash,
                            ContinuationFirstSequence: responseFirst, ContinuationLastSequence: responseLast,
                            ContinuationResponseType: "choice", ContinuationOptionId: resolved.OptionId,
                            ContinuationTurnId: turnId,
                            ContinuationTaskId: request.Kind == InteractionKind.Permission ? targetTask?.ToString() : null,
                            ContinuationLaneId: request.Kind == InteractionKind.Permission ? targetLane?.ToString() : null,
                            ContinuationToolCallId: request.Kind == InteractionKind.Permission ? targetToolCall?.ToString() : null,
                            NativeChoiceCapability: true, ExplicitChoiceRecovery: true);
                        var chosenOption = resolved.OptionId;
                        choices.Add(new ChoiceRecovery(binding, request.InteractionId.ToString(), run.ToString(),
                            policy.PolicyRevision, request.Kind.ToString(), chosenOption));
                        break;
                    }
                    case InteractionExpired expired:
                        pending.Remove(expired.InteractionId);
                        break;
                }
            }
            return choices.Count == 1 ? choices[0] : null;
        }

        private static TurnId? FindAcceptedPlanProposalTurn(IReadOnlyList<DomainEvent> events,
            IEventCodecRegistry codecs, RunId run, DomainEvent approvalEvent, LaneId rootLane)
        {
            foreach (var succeededEvent in events.Where(item => item.RunId == run && item.LaneId == rootLane
                         && item.TaskId == approvalEvent.TaskId && item.Sequence < approvalEvent.Sequence)
                         .OrderByDescending(item => item.Sequence))
            {
                if (codecs.Decode(succeededEvent) is not ToolCallSucceeded succeeded
                    || succeededEvent.TurnId is not { } proposalTurn) continue;
                var requested = events.Any(item => item.RunId == run && item.LaneId == rootLane
                    && item.TaskId == approvalEvent.TaskId && item.TurnId == proposalTurn
                    && item.Sequence < succeededEvent.Sequence
                    && codecs.Decode(item) is ToolCallRequested call
                    && call.ToolCallId == succeeded.ToolCallId && call.ToolName == "plan.propose");
                if (!requested) continue;
                var starts = events.Where(item => item.RunId == run && item.LaneId == rootLane
                    && item.TaskId == approvalEvent.TaskId && item.TurnId == proposalTurn
                    && item.Sequence < succeededEvent.Sequence && codecs.Decode(item) is TurnStarted).ToArray();
                return starts.Length == 1 ? proposalTurn : null;
            }
            return null;
        }

        private static bool HasStartedContinuation(IReadOnlyList<DomainEvent> events,
            IEventCodecRegistry codecs, RunId run, DomainEvent requestEvent, InteractionRequested request,
            long responseSequence)
        {
            if (request.Kind == InteractionKind.Permission)
            {
                var permission = events.SingleOrDefault(item => item.RunId == run && item.TurnId == requestEvent.TurnId
                    && codecs.Decode(item) is PermissionRequested permissionRequest
                    && permissionRequest.InteractionId == request.InteractionId);
                if (permission is null || codecs.Decode(permission) is not PermissionRequested permissionRequest)
                    return true;
                return events.Any(item => item.RunId == run && item.ToolCallId == permissionRequest.ToolCallId
                    && item.Sequence > permission.Sequence
                    && codecs.Decode(item) is PermissionGranted or ToolCallAuthorized or ToolCallStarted or ToolCallSucceeded or ToolCallFailed
                        or ToolCallEffectUnknown or ToolCallReconciled or PermissionDenied);
            }
            return events.Any(item => item.RunId == run && item.Sequence > responseSequence
                && codecs.Decode(item) is TurnStarted or ModelStepStarted or ToolCallStarted);
        }

        public CommandAck ResumeRecoveredChoice(IOmniClient client, WireEnvelope command,
            TrustedActionScope scope, CommandAck acknowledgement)
        {
            if (client is not OmniServer server || runner is null) return acknowledgement;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            if (!CanonicalGuid(root, "interactionId", out var interaction)
                || !CanonicalGuid(root, "expectedRunId", out var expectedRun)
                || !root.TryGetProperty("expectedPolicyRevision", out var revisionElement)
                || !revisionElement.TryGetInt64(out var revision)) return acknowledgement;
            lock (_gate)
            {
                if (_task is { IsCompleted: false }
                    || ReadChoiceRecovery(server, scope.ExpectedSessionId, expectedRun, revision) is not { } choice
                    || choice.InteractionId != interaction) return acknowledgement;
                var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
                _pendingContinuation = new PendingContinuation(server, choice.Binding,
                    command.MessageId, requestHash);
            }
            return acknowledgement;
        }

        public CommandAck Start(IOmniClient client, WireEnvelope command, TrustedActionScope? trustedScope)
        {
            if (trustedScope is null)
                return new CommandAck(command.MessageId, "error", "trusted_user_action_required", RuntimeCommandOutcome.Rejected());
            if (runner is null)
                return new CommandAck(command.MessageId, "error", "conversation_execution_unavailable", RuntimeCommandOutcome.Rejected());
            lock (_gate)
            {
                if (_task is { IsCompleted: false })
                {
                    var prior = FindReceiptResult(client, command);
                    return prior ?? new CommandAck(command.MessageId, "busy", "conversation_turn_busy",
                        RuntimeCommandOutcome.Deferred("conversation_turn_busy"));
                }
                using var payload = JsonDocument.Parse(command.PayloadJson);
                var root = payload.RootElement;
                var text = root.GetProperty("text").GetString()!;
                var attachments = HasAttachments(root) ? root.GetProperty("attachments").GetRawText() : null;
                var attachmentRefs = attachments is null ? Array.Empty<ArtifactRef>() : AttachmentReferences.Decode(attachments);
                if (validateAttachments is not null
                    && validateAttachments(attachments ?? "[]") is { } attachmentRejection)
                    return new CommandAck(command.MessageId, "error", attachmentRejection,
                        RuntimeCommandOutcome.Rejected());
                var mode = root.GetProperty("mode").GetString()!;
                var revision = root.GetProperty("expectedRevision");
                var revisionJson = revision.ValueKind == JsonValueKind.Null ? "null"
                    : revision.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var input = "{" + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", text)
                    + "," + JsonObj.Field("mode", mode) + ",\"expectedRevision\":" + revisionJson
                    + ",\"expectedPolicyRevision\":" + root.GetProperty("expectedPolicyRevision").GetRawText()
                    + ",\"expectedDefaultRevision\":" + root.GetProperty("expectedDefaultRevision").GetRawText()
                    + (attachments is null ? "" : "," + JsonObj.FieldRaw("attachments", attachments)) + "}";
                var ack = DispatchCommand(client, command, trustedScope, out var executed, input);
                if (executed && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted)
                {
                    var server = client as OmniServer;
                    if (server is null || server.LastSessionId() is not { } session || server.LastRunId() is not { } run)
                    {
                        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
                        if (server?.LastSessionId() is { } failedSession && server.LastRunId() is { } failedRun)
                            server.ReceiptStore?.CompleteCommandExecution(command.MessageId, hash, "failed",
                                "admitted_scope_unavailable", failedSession.ToString(), failedRun.ToString());
                        return new CommandAck(command.MessageId, "error", "admitted_scope_unavailable",
                            RuntimeCommandOutcome.Rejected());
                    }
                    var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
                    var binding = new PersistedConversationCommand(command.MessageId, requestHash,
                        session.ToString(), run.ToString(), ack.FirstSeq, ack.LastSeq,
                        NativeChoiceCapability: _nativeChoiceCapability);
                    _deferredConversation = null;
                    _cancellation?.Dispose();
                    _cancellation = new CancellationTokenSource();
                    var token = _cancellation.Token;
                    _commandId = command.MessageId;
                    _task = Task.Run(async () =>
                    {
                        var code = 1;
                        try { code = await runner(binding, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { code = 130; }
                        catch (Exception) { code = 1; }
                        if (code == 3)
                        {
                            lock (_gate) _deferredConversation = binding;
                        }
                        try
                        {
                            server.ReceiptStore?.CompleteCommandExecution(command.MessageId, requestHash,
                                code == 0 ? "completed" : code == 130 ? "cancelled" : code == 3 ? "deferred" : "failed",
                                code == 0 ? "turn_completed" : code == 130 ? "turn_cancelled" : code == 3 ? "input_required"
                                    : code == 4 ? "model_tools_unsupported" : "runtime_failed",
                                session.ToString(), run.ToString());
                        }
                        catch (Exception) { /* Admission stays Accepted; absent result is honestly Unknown. */ }
                        return code;
                    });
                }
                return ack;
            }
        }

        public bool CanResumeQuestionnaire(IOmniClient client, TrustedActionScope scope, WireEnvelope command)
        {
            if (client is not OmniServer server || runner is null || scope.ExpectedSessionId is not { } expectedSession
                || scope.ExpectedRunId is not { } expectedRun) return false;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            if (!payload.RootElement.TryGetProperty("interactionId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(idElement.GetString(), "D", out var interactionGuid)
                || !string.Equals(idElement.GetString(), interactionGuid.ToString("D"), StringComparison.Ordinal)) return false;
            var interactionId = new InteractionId(interactionGuid);
            var sessionId = new SessionId(Guid.Parse(expectedSession));
            var runId = new RunId(Guid.Parse(expectedRun));
            if (server.LastSessionId() != sessionId || server.LastRunId() != runId
                || new RunControlService(server.AcquireStore(), server.AcquireCodecs()).ActiveRun(sessionId) != runId)
                return false;

            var codecs = server.AcquireCodecs();
            var events = server.AcquireStore().ReadFrom(sessionId, 1);
            var requestEnvelope = events.FirstOrDefault(evt => evt.RunId == runId
                && codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interactionId && request.Kind == InteractionKind.Question
                && request.ToolCallJson is { Length: > 0 });
            if (requestEnvelope?.TurnId is not { } turnId
                || events.Any(evt => codecs.Decode(evt) is InteractionResolved resolved
                    && resolved.InteractionId == interactionId)
                || events.Any(evt => codecs.Decode(evt) is InteractionExpired expired
                    && expired.InteractionId == interactionId)
                || RunProjection.Replay(sessionId, runId, codecs, events).State != RunState.AwaitingInput)
                return false;

            var requested = (InteractionRequested)codecs.Decode(requestEnvelope);
            if (!TryGetQuestionnaireToolCallId(requested.ToolCallJson, out var requestedToolCallId)
                || requestEnvelope.ToolCallId is not { } requestToolCallId
                || requestToolCallId.ToString() != requestedToolCallId
                || requestEnvelope.TaskId is not { } requestTaskId
                || requestEnvelope.LaneId is not { } requestLaneId
                || !events.Any(evt => evt.RunId == runId && evt.TurnId == turnId
                    && evt.TaskId == requestTaskId && evt.LaneId == requestLaneId
                    && evt.ToolCallId == requestToolCallId
                    && codecs.Decode(evt) is ToolCallRequested toolCall && toolCall.ToolName == "user.ask"
                    && toolCall.ToolCallId == requestToolCallId)) return false;

            var runEvents = events.Where(evt => evt.RunId == runId).ToArray();
            var projection = RunProjection.Replay(sessionId, runId, codecs, runEvents);
            var rootLane = projection.RootTask is { } rootTask
                ? LaneProjection.Replay(codecs, runEvents).ForTask(rootTask).FirstOrDefault()?.Id : null;
            if (rootLane is null || requestLaneId != rootLane
                && server.AcceptedDelegationForChild(sessionId, runId, requestTaskId, requestLaneId) is null)
                return false;

            var turnStart = events.FirstOrDefault(evt => evt.RunId == runId && evt.TurnId == turnId
                && codecs.Decode(evt) is TurnStarted);
            if (turnStart is null || !IsOpenTurn(events, codecs, runId, turnId)) return false;
            var admitted = FindDeferredConversationBinding(server, events, codecs, sessionId, runId, turnStart.Sequence);
            if (admitted is null) return false;
            // Bind the answer to the exact child turn and user.ask call. The runner may resume
            // an accepted child delegation, but it must never infer that target from the UI.
            admitted = admitted with
            {
                ContinuationTurnId = turnId.ToString(),
                ContinuationTaskId = requestTaskId.ToString(),
                ContinuationLaneId = requestLaneId.ToString(),
                ContinuationToolCallId = requestToolCallId.ToString(),
            };
            lock (_gate)
            {
                if (_task is { IsCompleted: false }) return false;
                if (_deferredConversation is { } current
                    && (!string.Equals(current.CommandId, admitted.CommandId, StringComparison.Ordinal)
                        || !string.Equals(current.RequestHash, admitted.RequestHash, StringComparison.Ordinal))) return false;
                _deferredConversation = admitted;
                return true;
            }
        }

        public bool CanResumeChoice(IOmniClient client, TrustedActionScope scope, WireEnvelope command)
        {
            if (client is not OmniServer server || runner is null || scope.ExpectedSessionId is not { } expectedSession
                || scope.ExpectedRunId is not { } expectedRun || !_nativeChoiceCapability) return false;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            if (!CanonicalGuid(root, "interactionId", out var interactionText)
                || !root.TryGetProperty("optionId", out var optionElement)
                || optionElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("expectedRunId", out var runElement)
                || runElement.ValueKind != JsonValueKind.String || runElement.GetString() != expectedRun
                || !root.TryGetProperty("expectedPolicyRevision", out var revisionElement)
                || !revisionElement.TryGetInt64(out var expectedPolicyRevision)) return false;
            var session = new SessionId(Guid.Parse(expectedSession));
            var run = new RunId(Guid.Parse(expectedRun));
            var interaction = new InteractionId(Guid.Parse(interactionText));
            if (server.LastSessionId() != session || server.LastRunId() != run
                || new RunControlService(server.AcquireStore(), server.AcquireCodecs()).ActiveRun(session) != run
                || server.ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedPolicyRevision) return false;
            var codecs = server.AcquireCodecs();
            var events = server.AcquireStore().ReadFrom(session, 1);
            var requestEvent = events.SingleOrDefault(evt => evt.RunId == run
                && codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interaction
                    && request.Kind is InteractionKind.Permission or InteractionKind.PlanApproval or InteractionKind.MemoryPromotion);
            if (requestEvent is null || codecs.Decode(requestEvent) is not InteractionRequested requested
                || !Options(requested.OptionsJson).Contains(optionElement.GetString()!, StringComparer.Ordinal)
                || events.Any(evt => codecs.Decode(evt) is InteractionResolved resolved
                    && resolved.InteractionId == interaction)
                || events.Any(evt => codecs.Decode(evt) is InteractionExpired expired
                    && expired.InteractionId == interaction)
                || RunProjection.Replay(session, run, codecs, events).State != RunState.AwaitingInput) return false;
            var projection = RunProjection.Replay(session, run, codecs, events);
            var rootLane = projection.RootTask is { } rootTask
                ? LaneProjection.Replay(codecs, events).ForTask(rootTask).FirstOrDefault()?.Id : null;
            if (rootLane is null) return false;
            var requestedTask = requestEvent.TaskId;
            var requestedLane = requestEvent.LaneId;
            var requestedToolCall = requestEvent.ToolCallId;
            var targetIsRoot = requestedLane == rootLane && requestedTask == projection.RootTask;
            var targetIsAcceptedChild = requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion
                && requestedTask is { } childTask && requestedLane is { } childLane
                && server.AcceptedDelegationForChild(session, run, childTask, childLane) is not null;
            if (!targetIsRoot && !targetIsAcceptedChild) return false;
            if (requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion)
            {
                if (requestedTask is null || requestedLane is null || requestedToolCall is null) return false;
                if (requested.Kind == InteractionKind.Permission)
                {
                    var permission = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
                        .SingleOrDefault(item => item.Event.RunId == run && item.Event.TaskId == requestedTask
                            && item.Event.LaneId == requestedLane && item.Event.ToolCallId == requestedToolCall
                            && item.Event.TurnId == requestEvent.TurnId
                            && item.Payload is PermissionRequested requestedPermission
                            && requestedPermission.InteractionId == interaction);
                    if (permission.Event is null || permission.Event.TurnId != requestEvent.TurnId
                        || permission.Payload is not PermissionRequested permissionRequested
                        || events.Any(evt => evt.RunId == run && evt.ToolCallId == permissionRequested.ToolCallId
                            && codecs.Decode(evt) is PermissionGranted or PermissionDenied or ToolCallAuthorized
                                or ToolCallStarted or ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown
                                or ToolCallReconciled)) return false;
                }
                else if (!events.Any(evt => evt.RunId == run && evt.TaskId == requestedTask
                    && evt.LaneId == requestedLane && evt.TurnId == requestEvent.TurnId
                    && evt.ToolCallId == requestedToolCall && codecs.Decode(evt) is ToolCallRequested call
                    && call.ToolName == "memory.propose")) return false;
                var turn = requestEvent.TurnId;
                if (turn is null || !IsOpenTurn(events, codecs, run, turn)) return false;
            }
            var turnStart = requested.Kind == InteractionKind.Permission
                ? events.FirstOrDefault(evt => evt.RunId == run && evt.TurnId == requestEvent.TurnId
                    && codecs.Decode(evt) is TurnStarted)
                : events.LastOrDefault(evt => evt.RunId == run && evt.Sequence < requestEvent.Sequence
                    && codecs.Decode(evt) is TurnStarted);
            if (turnStart is null) return false;
            var optionId = optionElement.GetString();
            if (requested.Kind == InteractionKind.PlanApproval
                && optionId is ("approve_only" or "reject"))
            {
                // These choices are durable human decisions, but they do not dispatch another
                // model turn. Do not pin an in-memory conversation admission that could later
                // be mistaken for a resumable runner after this response is accepted.
                lock (_gate) return _task is not { IsCompleted: false };
            }
            var admitted = FindDeferredConversationBinding(server, events, codecs, session, run, turnStart.Sequence);
            if (admitted is null) return false;
            admitted = admitted with
            {
                NativeChoiceCapability = true,
                ContinuationTurnId = requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion
                    ? requestEvent.TurnId?.ToString() : null,
                ContinuationTaskId = requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion ? requestedTask?.ToString() : null,
                ContinuationLaneId = requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion ? requestedLane?.ToString() : null,
                ContinuationToolCallId = requested.Kind is InteractionKind.Permission or InteractionKind.MemoryPromotion ? requestedToolCall?.ToString() : null,
            };
            lock (_gate)
            {
                if (_task is { IsCompleted: false }) return false;
                if (_deferredConversation is { } current
                    && (!string.Equals(current.SessionId, expectedSession, StringComparison.Ordinal)
                        || !string.Equals(current.RunId, expectedRun, StringComparison.Ordinal))) return false;

                // A later choice can belong to a later Turn in the same Run. The journal-derived
                // admission above is scoped to that exact Turn's source input; retaining the first
                // in-memory binding would either lose a Permission TurnId or resume an older receipt.
                _deferredConversation = admitted;
                return string.Equals(_deferredConversation.SessionId, expectedSession, StringComparison.Ordinal)
                    && string.Equals(_deferredConversation.RunId, expectedRun, StringComparison.Ordinal)
                    && string.Equals(_deferredConversation.CommandId, admitted.CommandId, StringComparison.Ordinal)
                    && string.Equals(_deferredConversation.RequestHash, admitted.RequestHash, StringComparison.Ordinal);
            }
        }

        public bool CanResumeWeakSandboxConsent(IOmniClient client, TrustedActionScope scope, WireEnvelope command)
        {
            if (client is not OmniServer server || runner is null || !NativeWeakSandboxCapability
                || scope.ExpectedSessionId is not { } expectedSession || scope.ExpectedRunId is not { } expectedRun)
                return false;
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false }) return false;
                if (_deferredConversation is { } deferred
                    && (!string.Equals(deferred.SessionId, expectedSession, StringComparison.Ordinal)
                        || !string.Equals(deferred.RunId, expectedRun, StringComparison.Ordinal)
                        || deferred.NativeInvocationId is null)) return false;
            }
            try
            {
                using var payload = JsonDocument.Parse(command.PayloadJson);
                var root = payload.RootElement;
                static bool TryId(JsonElement node, string name, out Guid id)
                {
                    id = default;
                    return node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        && Guid.TryParseExact(value.GetString(), "D", out id)
                        && value.GetString() == id.ToString("D");
                }
                if (!TryId(root, "interactionId", out var interactionGuid)
                    || !TryId(root, "taskId", out var taskGuid) || !TryId(root, "laneId", out var laneGuid)
                    || !TryId(root, "turnId", out var turnGuid) || !TryId(root, "toolCallId", out var toolCallGuid)
                    || !TryId(root, "invocationId", out var invocationGuid)
                    || !root.TryGetProperty("optionId", out var option) || option.ValueKind != JsonValueKind.String
                    || option.GetString() is not ("consent_once" or "deny")
                    || !root.TryGetProperty("expectedPolicyRevision", out var revisionNode)
                    || !revisionNode.TryGetInt64(out var revision) || revision < 1
                    || server.LastSessionId()?.ToString() != expectedSession || server.LastRunId()?.ToString() != expectedRun)
                    return false;
                var session = new SessionId(Guid.Parse(expectedSession));
                var run = new RunId(Guid.Parse(expectedRun));
                var invocation = new CommandId(invocationGuid);
                var receipt = server.ReceiptStore?.ReadCommandReceipt(invocation.ToString());
                if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                        OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required",
                        FirstSequence: not null, LastSequence: not null }
                    || !string.Equals(receipt.SessionId, expectedSession, StringComparison.Ordinal)
                    || !string.Equals(receipt.ExecutionRunId, expectedRun, StringComparison.Ordinal)) return false;
                return server.CanResolveNativeWeakSandboxConsent(session, run, new TaskId(taskGuid),
                    new LaneId(laneGuid), new TurnId(turnGuid), new ToolCallId(toolCallGuid),
                    new InteractionId(interactionGuid), invocation, option.GetString()!, revision);
            }
            catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException)
            { return false; }
        }

        private static bool CanonicalGuid(JsonElement root, string property, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(element.GetString(), "D", out var parsed)
                || element.GetString() != parsed.ToString("D")) return false;
            value = parsed.ToString("D");
            return true;
        }

        private static string[] Options(string optionsJson)
        {
            try
            {
                using var document = JsonDocument.Parse(optionsJson);
                return document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetProperty("id").GetString()!).ToArray()
                    : [];
            }
            catch (JsonException) { return []; }
        }

        private static PersistedConversationCommand? FindDeferredConversationBinding(OmniServer server,
            IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs, SessionId sessionId, RunId runId,
            long turnStartSequence)
        {
            var store = server.ReceiptStore;
            if (store is null) return null;
            var startEvent = events.FirstOrDefault(evt => evt.RunId == runId
                && evt.Sequence == turnStartSequence && codecs.Decode(evt) is TurnStarted);
            if (startEvent is null || codecs.Decode(startEvent) is not TurnStarted currentTurn) return null;

            // Bind only admissions in the current Lane's interval. Older deferred receipts stay
            // durable after a questionnaire continuation; they must not make a later Turn in the
            // same Run ambiguous. Child-lane terminals do not advance this boundary.
            var laneByTurn = events.Where(evt => evt.RunId == runId && evt.Sequence < turnStartSequence)
                .Select(evt => (evt.Sequence, Payload: codecs.Decode(evt)))
                .Where(item => item.Payload is TurnStarted)
                .Select(item => (TurnStarted)item.Payload)
                .ToDictionary(item => item.TurnId, item => item.LaneId);
            var previousTerminalSequence = events.Where(evt => evt.RunId == runId && evt.Sequence < turnStartSequence)
                .Select(evt => (evt.Sequence, Payload: codecs.Decode(evt)))
                .Select(item => (item.Sequence, TurnId: item.Payload switch
                {
                    TurnCompleted completed => completed.TurnId,
                    TurnAbandoned abandoned => abandoned.TurnId,
                    TurnInterrupted interrupted => interrupted.TurnId,
                    _ => (TurnId?)null,
                }))
                .Where(item => item.TurnId is { } priorTurn
                    && laneByTurn.TryGetValue(priorTurn, out var lane) && lane == currentTurn.LaneId)
                .Select(item => item.Sequence)
                .DefaultIfEmpty(0)
                .Max();

            var candidates = events.Where(evt => evt.RunId == runId && evt.Sequence > previousTerminalSequence
                    && evt.Sequence < turnStartSequence
                    && codecs.Decode(evt) is UserInputReceived)
                .Select(evt => (Event: evt, Cause: evt.Causation as CommandCausation))
                .Where(item => item.Cause is not null)
                .Select(item => (item.Event, Receipt: store.ReadCommandReceipt(item.Cause!.CommandId.ToString())))
                .Where(item => item.Receipt is
                {
                    State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted",
                    ExecutionState: "deferred",
                    ExecutionCode: "input_required",
                    FirstSequence: not null,
                    LastSequence: not null,
                    ExecutionRunId: not null,
                })
                .Where(item => item.Event.Sequence >= item.Receipt!.FirstSequence
                    && item.Event.Sequence <= item.Receipt.LastSequence
                    && string.Equals(item.Receipt.SessionId, sessionId.ToString(), StringComparison.Ordinal)
                    && string.Equals(item.Receipt.ExecutionRunId, runId.ToString(), StringComparison.Ordinal))
                .Select(item => new PersistedConversationCommand(item.Receipt!.CommandId,
                    item.Receipt.RequestHash, sessionId.ToString(), runId.ToString(),
                    item.Receipt.FirstSequence, item.Receipt.LastSequence))
                .DistinctBy(item => item.CommandId, StringComparer.Ordinal)
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        private static bool IsOpenTurn(IReadOnlyList<DomainEvent> events, IEventCodecRegistry codecs,
            RunId runId, TurnId turnId)
        {
            var started = false;
            foreach (var evt in events.Where(evt => evt.RunId == runId && evt.TurnId == turnId)
                         .OrderBy(evt => evt.Sequence))
            {
                switch (codecs.Decode(evt))
                {
                    case TurnStarted:
                        started = true;
                        break;
                    case TurnCompleted completed when completed.TurnId == turnId:
                    case TurnAbandoned abandoned when abandoned.TurnId == turnId:
                    case TurnInterrupted interrupted when interrupted.TurnId == turnId:
                        return false;
                }
            }
            return started;
        }


        private static bool TryGetQuestionnaireToolCallId(string? toolCallJson, out string toolCallId)
        {
            toolCallId = string.Empty;
            if (string.IsNullOrWhiteSpace(toolCallJson)) return false;
            try
            {
                using var document = JsonDocument.Parse(toolCallJson);
                if (!document.RootElement.TryGetProperty("toolCallId", out var element)
                    || element.ValueKind != JsonValueKind.String) return false;
                toolCallId = element.GetString() ?? string.Empty;
                return Guid.TryParseExact(toolCallId, "D", out var parsed)
                    && string.Equals(toolCallId, parsed.ToString("D"), StringComparison.Ordinal);
            }
            catch (JsonException) { return false; }
        }

        public CommandAck ResumeQuestionnaire(IOmniClient client, WireEnvelope command,
            TrustedActionScope scope, CommandAck acknowledgement)
        {
            if (client is not OmniServer server || runner is null
                || acknowledgement.FirstSeq is null || acknowledgement.LastSeq is null)
                return new CommandAck(command.MessageId, "ok", "conversation_questionnaire_resume_recovery_required",
                    RuntimeCommandOutcome.Deferred("conversation_questionnaire_resume_recovery_required"),
                    acknowledgement.FirstSeq, acknowledgement.LastSeq);
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            var interactionId = root.GetProperty("interactionId").GetString()!;
            var sessionId = scope.ExpectedSessionId!;
            var runId = scope.ExpectedRunId!;
            var first = acknowledgement.FirstSeq.Value;
            var last = acknowledgement.LastSeq.Value;
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            lock (_gate)
            {
                if (_task is { IsCompleted: false } || _deferredConversation is not { } deferred
                    || !string.Equals(deferred.SessionId, sessionId, StringComparison.Ordinal)
                    || !string.Equals(deferred.RunId, runId, StringComparison.Ordinal)
                    || server.LastSessionId()?.ToString() != sessionId || server.LastRunId()?.ToString() != runId)
                    return new CommandAck(command.MessageId, "ok", "conversation_questionnaire_resume_recovery_required",
                        RuntimeCommandOutcome.Deferred("conversation_questionnaire_resume_recovery_required"), first, last);

                var continuation = deferred with
                {
                    ContinuationInteractionId = interactionId,
                    ContinuationCommandId = command.MessageId,
                    ContinuationRequestHash = requestHash,
                    ContinuationFirstSequence = first,
                    ContinuationLastSequence = last,
                    ContinuationTurnId = deferred.ContinuationTurnId,
                };
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;
                _commandId = command.MessageId;
                _task = Task.Run(async () =>
                {
                    var code = 1;
                    try { code = await runner(continuation, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { code = 130; }
                    catch (Exception) { code = 1; }
                    try
                    {
                        server.ReceiptStore?.CompleteCommandExecution(command.MessageId, requestHash,
                            code == 0 ? "completed" : code == 130 ? "cancelled" : code == 3 ? "deferred" : "failed",
                            code == 0 ? "turn_completed" : code == 130 ? "turn_cancelled" : code == 3 ? "input_required"
                                : code == 4 ? "model_tools_unsupported" : "runtime_failed",
                            sessionId, runId);
                    }
                    catch (Exception) { /* A durable response is never resent when completion status is unknown. */ }
                    finally
                    {
                        lock (_gate)
                        {
                            _deferredConversation = code == 3 ? continuation : null;
                        }
                    }
                    return code;
                });
            }
            return acknowledgement;
        }

        public CommandAck ResumeChoice(IOmniClient client, WireEnvelope command,
            TrustedActionScope scope, CommandAck acknowledgement)
        {
            if (client is not OmniServer server || runner is null
                || acknowledgement.FirstSeq is not { } first || acknowledgement.LastSeq is not { } last)
                return acknowledgement;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            var interactionId = root.GetProperty("interactionId").GetString()!;
            var optionId = root.GetProperty("optionId").GetString()!;
            var sessionId = scope.ExpectedSessionId!;
            var runId = scope.ExpectedRunId!;
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false } || _deferredConversation is not { } deferred
                    || !string.Equals(deferred.SessionId, sessionId, StringComparison.Ordinal)
                    || !string.Equals(deferred.RunId, runId, StringComparison.Ordinal)
                    || server.LastSessionId()?.ToString() != sessionId || server.LastRunId()?.ToString() != runId)
                    return acknowledgement;

                var continuation = deferred with
                {
                    ContinuationInteractionId = interactionId,
                    ContinuationCommandId = command.MessageId,
                    ContinuationRequestHash = requestHash,
                    ContinuationFirstSequence = first,
                    ContinuationLastSequence = last,
                    ContinuationResponseType = "choice",
                    ContinuationOptionId = optionId,
                    ContinuationTurnId = deferred.ContinuationTurnId,
                };
                _pendingContinuation = new PendingContinuation(server, continuation, command.MessageId, requestHash);
            }
            return acknowledgement;
        }

        public CommandAck ResumeWeakSandboxConsent(IOmniClient client, WireEnvelope command,
            TrustedActionScope scope, CommandAck acknowledgement)
        {
            if (client is not OmniServer server || runner is null
                || acknowledgement.FirstSeq is not { } first || acknowledgement.LastSeq is not { } last
                || scope.ExpectedSessionId is not { } sessionId || scope.ExpectedRunId is not { } runId)
                return acknowledgement;
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            var interactionId = root.GetProperty("interactionId").GetString()!;
            var optionId = root.GetProperty("optionId").GetString()!;
            var invocationId = root.GetProperty("invocationId").GetString()!;
            var taskId = root.GetProperty("taskId").GetString()!;
            var laneId = root.GetProperty("laneId").GetString()!;
            var turnId = root.GetProperty("turnId").GetString()!;
            var toolCallId = root.GetProperty("toolCallId").GetString()!;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var sourceReceipt = server.ReceiptStore?.ReadCommandReceipt(invocationId);
            if (sourceReceipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required",
                    FirstSequence: not null, LastSequence: not null }
                || !string.Equals(sourceReceipt.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(sourceReceipt.ExecutionRunId, runId, StringComparison.Ordinal)) return acknowledgement;
            var binding = new PersistedConversationCommand(invocationId, sourceReceipt.RequestHash!, sessionId,
                runId, sourceReceipt.FirstSequence, sourceReceipt.LastSequence,
                ContinuationInteractionId: interactionId, ContinuationCommandId: command.MessageId,
                ContinuationRequestHash: hash, ContinuationFirstSequence: first, ContinuationLastSequence: last,
                ContinuationResponseType: "weakSandboxConsent", ContinuationOptionId: optionId,
                ContinuationTurnId: turnId, NativeChoiceCapability: _nativeChoiceCapability,
                NativeInvocationId: invocationId, NativeCommandKind: "compiledWorkflow",
                ContinuationTaskId: taskId, ContinuationLaneId: laneId, ContinuationToolCallId: toolCallId,
                ContinuationInvocationId: invocationId, NativeWeakSandboxCapability: _nativeWeakSandboxCapability);
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false }
                    || _deferredConversation is { NativeInvocationId: { } current }
                        && !string.Equals(current, invocationId, StringComparison.Ordinal)) return acknowledgement;
                _pendingContinuation = new PendingContinuation(server, binding, command.MessageId, hash);
            }
            return acknowledgement;
        }

        public void StartPendingAfterAck(string acknowledgedReceiptCommandId)
        {
            PendingContinuation? pending;
            lock (_gate)
            {
                if (_pendingNativeProcessControl is { } processControl
                    && processControl.CommandId == acknowledgedReceiptCommandId)
                {
                    _pendingNativeProcessControl = null;
                    _nativeProcessControlCommandId = processControl.CommandId;
                    _nativeProcessControlTask = Task.Run(() => RunNativeProcessControl(processControl));
                    return;
                }
                pending = _pendingContinuation;
                if (pending is null || pending.ReceiptCommandId != acknowledgedReceiptCommandId
                    || runner is null || _task is { IsCompleted: false }) return;
                _pendingContinuation = null;
                _cancellation?.Dispose();
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;
                _commandId = pending.ReceiptCommandId;
                _task = Task.Run(async () =>
                {
                    var code = 1;
                    try { code = await runner(pending.Binding, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { code = 130; }
                    catch (Exception) { code = 1; }
                    try
                    {
                        pending.Server.ReceiptStore?.CompleteCommandExecution(pending.ReceiptCommandId,
                            pending.ReceiptRequestHash,
                            code == 0 ? "completed" : code == 130 ? "cancelled" : code == 3 ? "deferred" : "failed",
                            code == 0 ? "turn_completed" : code == 130 ? "turn_cancelled" : code == 3 ? "input_required"
                                : code == 4 ? "model_tools_unsupported" : "runtime_failed",
                            pending.Binding.SessionId, pending.Binding.RunId);
                    }
                    catch (Exception) { /* Accepted input is never resent when continuation status is unknown. */ }
                    finally
                    {
                        lock (_gate)
                        {
                            _deferredConversation = code == 3 ? pending.Binding : null;
                        }
                    }
                    return code;
                });
            }
        }

        private async Task<int> RunNativeProcessControl(PendingNativeProcessControl pending)
        {
            var code = 5;
            var state = "unknown";
            var detail = "effect_outcome_unknown";
            var usedPostTurnRunner = false;
            try
            {
                var session = new SessionId(Guid.Parse(pending.SessionId));
                var run = new RunId(Guid.Parse(pending.RunId));
                code = 6;
                if (pending.Server.CanExecuteNativeProcessControl(session, run,
                        pending.ToolName, pending.ArgumentsJson))
                    code = pending.Server.ExecuteNativeProcessControlDuringTurn(session, run,
                        pending.ToolName, pending.ArgumentsJson, pending.CommandId, CancellationToken.None);
                if (code == 6)
                {
                    if (pending.PriorTask is not null) await pending.PriorTask.ConfigureAwait(false);
                    usedPostTurnRunner = true;
                    code = runner is null ? 1 : await runner(pending.Binding, CancellationToken.None).ConfigureAwait(false);
                }
                state = code == 0 ? "completed" : code == 3 ? "deferred" : "failed";
                detail = code == 0 ? "native_tool_completed" : code == 3 ? "input_required" : "native_tool_failed";
            }
            catch (Exception)
            {
                // An exception after native admission cannot prove that the tool had no effect.
                code = 5;
            }
            try
            {
                pending.Server.ReceiptStore?.CompleteCommandExecution(pending.CommandId, pending.RequestHash,
                    state, detail, pending.SessionId, pending.RunId);
            }
            catch (Exception) { /* Keep the command admitted; callers reconcile its durable events. */ }
            if (usedPostTurnRunner && code == 0)
            {
                try
                {
                    pending.Server.ResumeDeferredProcessRunCompletion(
                        new SessionId(Guid.Parse(pending.SessionId)), new RunId(Guid.Parse(pending.RunId)));
                }
                catch (Exception) { /* The process receipt is terminal; run completion remains safely pending. */ }
            }
            return code;
        }

        public string? PendingCommandForAcknowledgement(string commandId)
        {
            lock (_gate)
                return _pendingContinuation?.ReceiptCommandId == commandId || _pendingNativeProcessControl?.CommandId == commandId
                    ? commandId : null;
        }

        public bool ScheduleNativeCommand(IOmniClient client, WireEnvelope command, CommandAck acknowledgement)
        {
            if (runner is null || client is not OmniServer server || server.ReceiptStore is not { } receipts
                || acknowledgement.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted
                || !Guid.TryParse(command.MessageId, out _)) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var receipt = receipts.ReadCommandReceipt(command.MessageId);
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: null }
                || !string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal)
                || receipt.SessionId is not { Length: > 0 } receiptSession
                || server.LastSessionId()?.ToString() != receiptSession
                || server.LastRunId() is not { } activeRun)
                return false;

            string invocationText;
            var commandName = ReadCommandName(command.PayloadJson);
            if (commandName is "command.invoke.user" or "process.control") invocationText = command.MessageId;
            else if (commandName != "command.continue") return false;
            else
            {
                try
                {
                    using var request = JsonDocument.Parse(command.PayloadJson);
                    if (!request.RootElement.TryGetProperty("invocationId", out var invocation)
                        || invocation.ValueKind != JsonValueKind.String) return false;
                    invocationText = invocation.GetString() ?? "";
                }
                catch (JsonException) { return false; }
            }
            if (!Guid.TryParseExact(invocationText, "D", out var invocationGuid)
                || invocationText != invocationGuid.ToString("D")
                || !server.TryReadNativeCommandPayload(new CommandId(invocationGuid), out var admission, out _)
                || admission.RunId?.ToString() != activeRun.ToString()
                || admission.SessionId.ToString() != receiptSession)
                return false;

            var binding = new PersistedConversationCommand(command.MessageId, hash, receiptSession,
                activeRun.ToString(), acknowledgement.FirstSeq, acknowledgement.LastSeq,
                NativeChoiceCapability: _nativeChoiceCapability,
                NativeInvocationId: invocationText, NativeCommandKind: admission.Kind,
                ExplicitNativeCommandRecovery: commandName == "command.continue",
                NativeWeakSandboxCapability: _nativeWeakSandboxCapability);
            lock (_gate)
            {
                if (commandName == "process.control")
                {
                    if (_pendingContinuation is not null || _deferredConversation is not null
                        || _pendingNativeProcessControl is not null
                        || _nativeProcessControlTask is { IsCompleted: false })
                        return false;
                    try
                    {
                        using var payload = JsonDocument.Parse(command.PayloadJson);
                        var args = payload.RootElement.GetProperty("arguments");
                        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != 2
                            || args[0].ValueKind != JsonValueKind.String || args[1].ValueKind != JsonValueKind.String
                            || !server.CanExecuteNativeProcessControl(new SessionId(Guid.Parse(receiptSession)), activeRun,
                                args[0].GetString()!, args[1].GetString()!)) return false;
                        _pendingNativeProcessControl = new PendingNativeProcessControl(server, command.MessageId, hash,
                            receiptSession, activeRun.ToString(), args[0].GetString()!, args[1].GetString()!, binding,
                            _task is { IsCompleted: false } ? _task : null);
                    }
                    catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException)
                    { return false; }
                    return true;
                }
                if (_pendingContinuation is not null || _task is { IsCompleted: false } || _deferredConversation is not null)
                    return false;
                _pendingContinuation = new PendingContinuation(server, binding, command.MessageId, hash);
            }
            return true;
        }

        public bool ScheduleNativeWorkflowContinuation(IOmniClient client, WireEnvelope command,
            CommandAck acknowledgement)
        {
            if (runner is null || client is not OmniServer server || server.ReceiptStore is not { } receipts
                || acknowledgement.Outcome?.Kind != RuntimeCommandOutcomeKind.Accepted
                || !Guid.TryParse(command.MessageId, out _)
                || !TryReadInvocationId(command.PayloadJson, out var invocationGuid)
                || !server.NativeWorkflowContinuationIsAllowed(new CommandId(invocationGuid), out _, out _, out _)) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var receipt = receipts.ReadCommandReceipt(command.MessageId);
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: null }
                || !string.Equals(receipt.RequestHash, hash, StringComparison.Ordinal)
                || receipt.SessionId is not { Length: > 0 } session
                || server.LastSessionId()?.ToString() != session
                || server.LastRunId() is not { } run) return false;
            var binding = new PersistedConversationCommand(command.MessageId, hash, session, run.ToString(),
                acknowledgement.FirstSeq, acknowledgement.LastSeq, NativeChoiceCapability: _nativeChoiceCapability,
                NativeInvocationId: invocationGuid.ToString("D"), NativeCommandKind: "compiledWorkflow",
                NativeWorkflowContinuation: true);
            lock (_gate)
            {
                if (_pendingContinuation is not null || _task is { IsCompleted: false }) return false;
                if (_deferredConversation is { NativeInvocationId: { } deferredInvocation }
                    && !string.Equals(deferredInvocation, invocationGuid.ToString("D"), StringComparison.Ordinal)) return false;
                _pendingContinuation = new PendingContinuation(server, binding, command.MessageId, hash);
            }
            return true;
        }

        private static bool TryReadInvocationId(string payloadJson, out Guid invocation)
        {
            invocation = default;
            try
            {
                using var request = JsonDocument.Parse(payloadJson);
                if (!request.RootElement.TryGetProperty("invocationId", out var node)
                    || node.ValueKind != JsonValueKind.String || node.GetString() is not { } text
                    || !Guid.TryParseExact(text, "D", out var parsed) || text != parsed.ToString("D")) return false;
                invocation = parsed;
                return true;
            }
            catch (JsonException) { return false; }
        }

        public bool CancelAndJoin(TimeSpan timeout)
        {
            Task<int>? task;
            lock (_gate)
            {
                task = _task;
                if (task is null || task.IsCompleted) return true;
                _cancellation?.Cancel();
            }
            return task.Wait(timeout);
        }

        public void StopAndJoin()
        {
            Task<int>? task;
            lock (_gate)
            {
                _cancellation?.Cancel();
                task = _task;
            }
            if (task is not null) _ = task.Wait(TimeSpan.FromSeconds(3));
        }

        public void Dispose()
        {
            StopAndJoin();
            lock (_gate)
            {
                _cancellation?.Dispose();
                _cancellation = null;
            }
            _uploads.Dispose();
        }
    }

    private static CommandAck AckFromReceipt(OmniCore.Infrastructure.CommandReceipt receipt)
    {
        var outcome = receipt.OutcomeKind switch
        {
            "Accepted" => RuntimeCommandOutcome.Accepted(),
            "Rejected" => RuntimeCommandOutcome.Rejected(),
            "NoOp" => RuntimeCommandOutcome.NoOp(),
            "Deferred" => RuntimeCommandOutcome.Deferred(receipt.OutcomeReason ?? "receipt_result_unknown"),
            _ => RuntimeCommandOutcome.Deferred("receipt_result_unknown"),
        };
        return new CommandAck(receipt.CommandId, receipt.AckStatus ?? "unknown", null, outcome,
            receipt.FirstSequence, receipt.LastSequence);
    }

    private static string EncodeEvents(IReadOnlyList<WireEnvelope> events)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var item in events)
        {
            var json = item.ToJson();
            if (builder.Length > 0) builder.Append(',');
            builder.Append(json);
        }
        return builder.ToString();
    }

    private static bool TryNegotiate(IOmniClient client, WireEnvelope request, byte[]? bootstrapKey,
        string? actionNonce, bool conversationExecutionAvailable, out WireEnvelope? response, out bool accepted,
        out bool nativeChoiceCapability, out bool nativeWeakSandboxCapability)
    {
        accepted = false;
        nativeChoiceCapability = false;
        nativeWeakSandboxCapability = false;
        if (request.Version != ProtocolVersions.V1 || request.MessageType != MessageTypes.Hello)
        {
            response = Error(request.MessageId, "incompatible_protocol", "No common protocol version.");
            return false;
        }
        using var document = JsonDocument.Parse(request.PayloadJson);
        if (!document.RootElement.TryGetProperty("supportedVersions", out var versions)
            || versions.ValueKind != JsonValueKind.Array
            || !versions.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && value.GetString() == ProtocolVersions.V1))
        {
            response = Error(request.MessageId, "incompatible_protocol", "No common protocol version.");
            return false;
        }
        var baseCapabilities = NegotiatedCapabilities(client, bootstrapKey, actionNonce, conversationExecutionAvailable);
        var trustedAvailable = baseCapabilities.Contains("query.humanActionScope", StringComparer.Ordinal);
        nativeChoiceCapability = trustedAvailable
            && document.RootElement.TryGetProperty("capabilities", out var requestedCapabilities)
            && requestedCapabilities.ValueKind == JsonValueKind.Array
            && requestedCapabilities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && value.GetString() == "command.conversation.interaction.choice.respond.user");
        nativeWeakSandboxCapability = nativeChoiceCapability
            && document.RootElement.TryGetProperty("capabilities", out var weakCapabilities)
            && weakCapabilities.ValueKind == JsonValueKind.Array
            && weakCapabilities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && value.GetString() == "command.conversation.weak-sandbox.respond.user");
        var supported = nativeChoiceCapability
            ? NegotiatedCapabilities(client, bootstrapKey, actionNonce, conversationExecutionAvailable,
                nativeChoiceCapability, nativeWeakSandboxCapability)
            : baseCapabilities;
        var capabilities = "[" + string.Join(",", supported.Select(Quote)) + "]";
        response = WireEnvelope.Response(request.MessageId,
            "{\"protocolVersion\":\"1.0\",\"capabilities\":" + capabilities
            + (trustedAvailable ? ",\"trustedActionNonce\":" + Quote(actionNonce!) : "") + "}");
        accepted = true;
        return true;
    }

    private static string[] NegotiatedCapabilities(IOmniClient client, byte[]? bootstrapKey,
        string? actionNonce, bool conversationExecutionAvailable, bool nativeChoiceCapability = false,
        bool nativeWeakSandboxCapability = false)
    {
        IEnumerable<string> supported = StdioProtocol.SupportedCapabilities;
        if (client is not OmniServer)
        {
            var hostOnlyQueries = new HashSet<string>(["query.toolCatalog", "query.extensionCatalog", "query.mcpConfiguration",
                "query.hookCatalog", "query.skillCatalog", "query.sessionMemory", "query.modePreferences",
                "query.reasoningPreferences", "query.workspaceStatus", "query.turnContext", "query.openRouterStatus", "query.uiPreferences",
                "query.sessionSecurity", "query.sessionSecurityDefault", "query.conversations",
                "query.commandOutcome", "query.commandCatalog", "query.commandRecovery", "query.workflowRecovery",
                "query.processSessions"],
                StringComparer.Ordinal);
            supported = supported.Where(capability => !hostOnlyQueries.Contains(capability));
        }
        if (client is not OmniServer { ReceiptStore: not null })
            supported = supported.Where(capability => capability is not ("command.result.lookup" or "events.replay.page"
                or "events.stream.v1" or "conversation.result.lookup"));
        var trustedAvailable = bootstrapKey is { Length: 32 } && actionNonce is not null
            && client is OmniServer { ReceiptStore: not null } && TryGetWorkspaceHash(out _);
        if (trustedAvailable)
            supported = supported.Concat(StdioProtocol.TrustedUserActionCapabilities.Where(capability =>
                capability is not ("command.conversation.start.user"
                    or "command.conversation.questionnaire.respond.user"
                    or "command.conversation.interaction.choice.respond.user"
                    or "command.conversation.weak-sandbox.respond.user"
                    or "command.conversation.interaction.continue.user"
                    or "command.invoke.user" or "command.invoke.continue.user") || conversationExecutionAvailable)
                .Where(capability => capability switch
                {
                    "command.conversation.interaction.choice.respond.user"
                        or "command.conversation.interaction.continue.user" => nativeChoiceCapability,
                    "command.conversation.weak-sandbox.respond.user" => nativeChoiceCapability && nativeWeakSandboxCapability,
                    _ => true,
                }));
        if (nativeChoiceCapability) supported = supported.Append("query.interactionRecovery");
        return supported.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static WireEnvelope ParseEnvelope(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("messageType", out var type)
            || type.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("messageId", out var id)
            || id.ValueKind != JsonValueKind.String
            || !document.RootElement.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Malformed wire envelope.");
        return WireEnvelope.FromJson(json);
    }

    private static WireEnvelope Error(string messageId, string code, string message) => WireEnvelope.FromJson(
        "{\"version\":\"1.0\",\"messageType\":\"error\",\"messageId\":" + Quote(messageId)
        + ",\"payload\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + "}}");

    private static bool TryGetWorkspaceHash(out string workspaceHash)
    {
        try
        {
            var workspace = ProjectIdentity.CanonicalWorkspacePath(
                ProjectIdentity.ResolvePhysicalWorkspaceRoot(Environment.CurrentDirectory));
            workspaceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)));
            return true;
        }
        catch (Exception)
        {
            workspaceHash = string.Empty;
            return false;
        }
    }

    private static string ReadCommandName(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("cmd", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private static bool IsJsonObject(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private static string Quote(string value) => "\"" + JsonObj.Escape(value) + "\"";

    private static void WriteFrame(Stream output, string json)
    {
        lock (output) WireFrame.Write(output, json);
    }

    /// <summary>Bounded journal tailer. It reads only when the consumer has credit; the journal remains authoritative.</summary>
    private sealed class EventStreamPump(Stream output) : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource _stop = new();
        private readonly Queue<long> _outstanding = new();
        private Task? _worker;
        private OmniServer? _server;
        private SessionId? _session;
        private long _cursor;
        private long _lastAcknowledged;
        private int _window;
        private bool _configured;
        private bool _started;
        private bool _ready;
        private bool _disposed;
        private bool _gap;
        private string _streamId = Guid.NewGuid().ToString("N");
        public string StreamId { get { lock (_gate) return _streamId; } }

        public bool Configure(OmniServer server, SessionId session, long cursor, int window)
        {
            lock (_gate)
            {
                if (_disposed || _configured || _worker is { IsCompleted: false }) return false;
                _stop.Dispose();
                _stop = new CancellationTokenSource();
                _streamId = Guid.NewGuid().ToString("N");
                _outstanding.Clear();
                _gap = false;
                _ready = false;
                _started = false;
                _server = server;
                _session = session;
                _cursor = cursor;
                _lastAcknowledged = cursor - 1;
                _window = window;
                _configured = true;
                return true;
            }
        }

        public void StartIfConfigured()
        {
            lock (_gate)
            {
                if (!_configured || !_ready || _started) return;
                _started = true;
                var token = _stop.Token;
                _worker = Task.Run(() => PumpAsync(token));
            }
        }

        public bool MarkReady(string streamId)
        {
            lock (_gate)
            {
                if (!_configured || streamId != StreamId) return false;
                _ready = true;
                return true;
            }
        }

        public bool StopSubscription(string streamId)
        {
            Task? worker;
            lock (_gate)
            {
                if (!_configured || streamId != _streamId) return false;
                _configured = false;
                _ready = false;
                _server = null;
                _session = null;
                _stop.Cancel();
                worker = _worker;
            }
            try { if (worker is not null && !worker.Wait(TimeSpan.FromSeconds(2))) return false; }
            catch (AggregateException) { }
            lock (_gate)
            {
                if (_streamId != streamId) return false;
                _started = false;
                _worker = null;
                _outstanding.Clear();
                return true;
            }
        }

        public bool Acknowledge(string streamId, long throughSequence)
        {
            lock (_gate)
            {
                if (!_configured || streamId != StreamId || throughSequence < _lastAcknowledged) return false;
                if (throughSequence == _lastAcknowledged) return true;
                if (!_outstanding.Contains(throughSequence)) return false;
                while (_outstanding.Count > 0 && _outstanding.Peek() <= throughSequence) _outstanding.Dequeue();
                _lastAcknowledged = throughSequence;
                return true;
            }
        }

        private async Task PumpAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                OmniServer server;
                SessionId session;
                long cursor;
                int capacity;
                string streamId;
                lock (_gate)
                {
                    if (_gap || _server is null || _session is null) return;
                    server = _server;
                    session = _session!;
                    cursor = _cursor;
                    capacity = _window - _outstanding.Count;
                    streamId = _streamId;
                }
                if (capacity <= 0)
                {
                    await Task.Delay(40, token).ConfigureAwait(false);
                    continue;
                }
                // A short bounded journal read is the wake mechanism available in stdio 1.0.
                // No events are buffered while credit is exhausted; writers and commands share no stdout wait.
                var page = server.ReadStreamPage(session, cursor, Math.Min(capacity, 32));
                if (page.HasGap)
                {
                    lock (_gate)
                    {
                        if (_streamId != streamId || !_configured) return;
                        _gap = true;
                    }
                    var payload = "{\"streamId\":" + Quote(streamId) + ",\"kind\":\"gap\",\"requestedSequence\":"
                        + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"nextSequence\":"
                        + page.NextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"highWater\":"
                        + page.HighWater.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                    WriteFrame(output, WireEnvelope.Event(WireEnvelope.NewId(), payload).ToJson());
                    return;
                }
                if (page.Events.Count == 0)
                {
                    lock (_gate)
                    {
                        if (_streamId != streamId || !_configured) return;
                        _cursor = Math.Max(_cursor, page.NextSequence);
                    }
                    await Task.Delay(40, token).ConfigureAwait(false);
                    continue;
                }
                foreach (var item in page.Events)
                {
                    if (token.IsCancellationRequested) return;
                    using var document = JsonDocument.Parse(item.PayloadJson);
                    if (!document.RootElement.TryGetProperty("seq", out var sequenceElement)
                        || sequenceElement.ValueKind != JsonValueKind.String
                        || !long.TryParse(sequenceElement.GetString(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var sequence)) continue;
                    var envelopeJson = item.ToJson();
                    var payload = "{\"streamId\":" + Quote(streamId) + ",\"kind\":\"event\",\"sequence\":"
                        + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"event\":" + envelopeJson + "}";
                    var frame = WireEnvelope.Event(WireEnvelope.NewId(), payload).ToJson();
                    if (Encoding.UTF8.GetByteCount(frame) > StdioProtocol.MaximumFrameBytes)
                    {
                        WriteFrame(output, WireEnvelope.Event(WireEnvelope.NewId(), "{\"streamId\":" + Quote(streamId)
                            + ",\"kind\":\"frame_too_large\",\"sequence\":"
                            + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}").ToJson());
                        lock (_gate) { if (_streamId == streamId) _gap = true; }
                        return;
                    }
                    lock (_gate)
                    {
                        if (_streamId != streamId || !_configured || token.IsCancellationRequested) return;
                        if (_outstanding.Count >= _window) break;
                        _outstanding.Enqueue(sequence);
                        _cursor = sequence + 1;
                    }
                    lock (_gate)
                    {
                        if (_streamId != streamId || !_configured || token.IsCancellationRequested) return;
                    }
                    WriteFrame(output, frame);
                }
                lock (_gate)
                {
                    if (_streamId != streamId || !_configured) return;
                    _cursor = Math.Max(_cursor, page.NextSequence);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; }
            _stop.Cancel();
            try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }

    private sealed class ReplayFrameTooLargeException : Exception { }
}
