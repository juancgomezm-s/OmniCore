namespace OmniCore.Host;

using OmniCore.Protocol;
using OmniCore.Domain;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Task = System.Threading.Tasks.Task;

internal sealed record PersistedConversationCommand(string CommandId, string RequestHash, string SessionId,
    string RunId, long? FirstSequence, long? LastSequence);

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
                        out var response, out var accepted))
                    {
                        WriteFrame(output, response!.ToJson());
                        return 3;
                    }
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
                    var result = Dispatch(client, request, bootstrapKey, actionNonce, conversation, eventStream);
                    WriteFrame(output, WireEnvelope.Response(request.MessageId, result).ToJson());
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
        ConversationExecution conversation, EventStreamPump eventStream)
    {
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
                var trustedOnly = commandName is "conversation.start" or "run.interrupt" or "run.cancel" or "run.mode.select"
                    or "external.memory.consent"
                    or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish";
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
                if (trustedScope is not null && commandName is not ("session.input" or "conversation.start"
                    or "run.interrupt" or "run.cancel" or "run.mode.select" or "external.memory.consent" or "attachment.upload.begin"
                    or "attachment.upload.chunk" or "attachment.upload.finish"))
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
                else if (conversation.IsRunning)
                {
                    var duplicate = FindReceiptResult(client, command);
                    if (duplicate is not null) return EncodeAck(duplicate);
                    return EncodeAck(new CommandAck(command.MessageId, "busy", "conversation_turn_busy",
                        RuntimeCommandOutcome.Deferred("conversation_turn_busy")));
                }
                var ack = commandName == "conversation.start"
                    ? conversation.Start(client, command, trustedScope)
                    : DispatchCommand(client, command, trustedScope, out _);
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
                if (name == "humanActionScope")
                {
                    if (bootstrapKey is null || actionNonce is null || client is not OmniServer trustedServer
                        || trustedServer.ReceiptStore is null)
                        throw new NotSupportedException();
                    return "{\"name\":\"humanActionScope\",\"json\":"
                        + Quote(trustedServer.TrustedActionScopeJson()) + "}";
                }
                var result = client.Query(name, CancellationToken.None);
                return "{\"name\":" + Quote(result?.Name ?? name) + ",\"json\":"
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
        if (commandName is not ("session.input" or "conversation.start" or "run.interrupt" or "run.cancel" or "run.mode.select"
            or "external.memory.consent"
            or "attachment.upload.begin" or "attachment.upload.chunk" or "attachment.upload.finish")) return false;
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
        if (commandName is "session.input" or "conversation.start")
        {
            var hasAttachments = commandName == "conversation.start" && HasAttachments(payload.RootElement);
            var expectedProperties = hasAttachments
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
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash,
                inputRevision.ValueKind == JsonValueKind.Null ? null : inputRevision.GetInt64(),
                CheckRevision: true, RequirePlanRun: mode.GetString() == "plan", RequestedMode: mode.GetString(),
                RequireCurrentAct: mode.GetString() == "act");
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
        bool RequireCurrentAct = false);

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
            return new CommandAck(command.MessageId, "busy", "session_writer_owned",
                RuntimeCommandOutcome.Deferred("session_writer_owned"));

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
        private readonly object _gate = new();
        private CancellationTokenSource? _cancellation;
        private Task<int>? _task;
        private string? _commandId;
        private readonly AttachmentUploadManager _uploads = new();

        public bool IsRunning { get { lock (_gate) return _task is { IsCompleted: false }; } }
        public bool IsRunningFor(string commandId) { lock (_gate) return _commandId == commandId && _task is { IsCompleted: false }; }
        public (CommandAck Ack, ArtifactRef? Reference) Upload(WireEnvelope command, OmniServer server, string commandName) =>
            _uploads.Dispatch(server, command, commandName);

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
                if (attachmentRefs.Length > 0 && validateAttachments is not null
                    && validateAttachments(attachments!) is { } attachmentRejection)
                    return new CommandAck(command.MessageId, "error", attachmentRejection,
                        RuntimeCommandOutcome.Rejected());
                var mode = root.GetProperty("mode").GetString()!;
                var revision = root.GetProperty("expectedRevision");
                var revisionJson = revision.ValueKind == JsonValueKind.Null ? "null"
                    : revision.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var input = "{" + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", text)
                    + "," + JsonObj.Field("mode", mode) + ",\"expectedRevision\":" + revisionJson
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
                        session.ToString(), run.ToString(), ack.FirstSeq, ack.LastSeq);
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
        string? actionNonce, bool conversationExecutionAvailable, out WireEnvelope? response, out bool accepted)
    {
        accepted = false;
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
        var supported = StdioProtocol.SupportedCapabilities.AsEnumerable();
        if (client is not OmniServer { ReceiptStore: not null })
            supported = supported.Where(capability => capability is not ("command.result.lookup" or "events.replay.page" or "events.stream.v1"
                or "conversation.result.lookup"));
        var trustedAvailable = bootstrapKey is { Length: 32 } && actionNonce is not null
            && client is OmniServer { ReceiptStore: not null } && TryGetWorkspaceHash(out _);
        if (trustedAvailable)
            supported = supported.Concat(StdioProtocol.TrustedUserActionCapabilities.Where(capability =>
                capability != "command.conversation.start.user" || conversationExecutionAvailable));
        var capabilities = "[" + string.Join(",", supported.Select(Quote)) + "]";
        response = WireEnvelope.Response(request.MessageId,
            "{\"protocolVersion\":\"1.0\",\"capabilities\":" + capabilities
            + (trustedAvailable ? ",\"trustedActionNonce\":" + Quote(actionNonce!) : "") + "}");
        accepted = true;
        return true;
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
