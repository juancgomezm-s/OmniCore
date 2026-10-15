namespace OmniCore.Host;

using OmniCore.Protocol;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

/// <summary>Request/response stdio adapter over the same IOmniClient used by the in-process CLI.</summary>
public static class StdioProtocolHost
{
    public static int Run(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        CancellationToken cancellationToken = default, Func<string, string>? localizeDiagnostic = null)
        => RunCore(client, input, output, diagnostics, null, null, cancellationToken, localizeDiagnostic);

    internal static int RunWithTrustedActions(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        byte[] bootstrapKey, CancellationToken cancellationToken = default, Func<string, string>? localizeDiagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapKey);
        if (bootstrapKey.Length != 32) throw new ArgumentException("Trusted action bootstrap key must be 256 bits.", nameof(bootstrapKey));
        return RunCore(client, input, output, diagnostics, bootstrapKey,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), cancellationToken, localizeDiagnostic);
    }

    private static int RunCore(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        byte[]? bootstrapKey, string? actionNonce, CancellationToken cancellationToken,
        Func<string, string>? localizeDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var negotiated = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = WireFrame.Read(input);
                if (frame is null) return 0;
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
                    if (!TryNegotiate(client, request, bootstrapKey, actionNonce, out var response, out var accepted))
                    {
                        WireFrame.Write(output, response!.ToJson());
                        return 3;
                    }
                    negotiated = accepted;
                    WireFrame.Write(output, response!.ToJson());
                    continue;
                }

                if (request.Version != ProtocolVersions.V1 || request.MessageType != MessageTypes.Command)
                {
                    WireFrame.Write(output, Error(request.MessageId, "invalid_request",
                        "Expected a protocol 1.0 command request.").ToJson());
                    continue;
                }

                try
                {
                    var result = Dispatch(client, request, bootstrapKey, actionNonce);
                    WireFrame.Write(output, WireEnvelope.Response(request.MessageId, result).ToJson());
                }
                catch (ReplayFrameTooLargeException)
                {
                    WireFrame.Write(output, Error(request.MessageId, "frame_too_large",
                        "A single replay event exceeds the negotiated frame limit.").ToJson());
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException
                    or InvalidOperationException or NotSupportedException or FormatException
                    or KeyNotFoundException or OverflowException)
                {
                    WireFrame.Write(output, Error(request.MessageId, "unsupported_request", "Request is not supported.").ToJson());
                }
            }
            return 130;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException
            or System.Text.DecoderFallbackException or IOException)
        {
            diagnostics.WriteLine(localizeDiagnostic?.Invoke("stdio.host.invalid_frame")
                ?? "Stdio protocol connection ended with an invalid or incomplete frame.");
            return 2;
        }
    }

    private static string Dispatch(IOmniClient client, WireEnvelope request, byte[]? bootstrapKey, string? actionNonce)
    {
        using var document = JsonDocument.Parse(request.PayloadJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("operation", out var operationElement)
            || operationElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException();
        var operation = operationElement.GetString();
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
                var trustedOnly = commandName is "run.interrupt" or "run.cancel" or "run.mode.select";
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
                if (trustedScope is not null && commandName is not ("session.input" or "run.interrupt" or "run.cancel" or "run.mode.select"))
                    return EncodeAck(new CommandAck(command.MessageId, "error", "trusted_action_not_supported",
                        RuntimeCommandOutcome.Rejected()));
                var ack = DispatchCommand(client, command, trustedScope);
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
        if (commandName is not ("session.input" or "run.interrupt" or "run.cancel" or "run.mode.select")) return false;
        if ((commandName is "run.interrupt" or "run.cancel" or "run.mode.select") && expectedRun.Value is null) return false;
        var properties = payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        if (commandName == "session.input")
        {
            if (properties.Length != 4 || properties.Except(["cmd", "text", "mode", "expectedRevision"], StringComparer.Ordinal).Any()
                || !payload.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > 32_000
                || !payload.RootElement.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String
                || mode.GetString() != "plan"
                || !payload.RootElement.TryGetProperty("expectedRevision", out var inputRevision)
                || (inputRevision.ValueKind != JsonValueKind.Null &&
                    (inputRevision.ValueKind != JsonValueKind.Number || !inputRevision.TryGetInt64(out var parsedInputRevision)
                     || parsedInputRevision < 0))) return false;
            scope = new(expectedSession.Value, expectedRun.Value, workspaceHash,
                inputRevision.ValueKind == JsonValueKind.Null ? null : inputRevision.GetInt64(),
                CheckRevision: true, RequirePlanRun: true);
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
                CheckRevision: true, RequirePlanRun: false);
            return true;
        }
        scope = new(expectedSession.Value, expectedRun.Value, workspaceHash, null,
            CheckRevision: false, RequirePlanRun: false);
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

    private sealed record TrustedActionScope(string? ExpectedSessionId, string? ExpectedRunId, string WorkspaceHash,
        long? ExpectedRevision, bool CheckRevision, bool RequirePlanRun);

    private static CommandAck DispatchCommand(IOmniClient client, WireEnvelope command, TrustedActionScope? trustedScope = null)
    {
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

        var ack = trustedScope is null ? client.Send(command, CancellationToken.None)
            : ((ITrustedUserActionClient)client).SendUserAction(command, CancellationToken.None);
        store.CompleteCommandReceipt(command.MessageId, hash, ack.Status, ack.Outcome?.Kind.ToString(), ack.Outcome?.Reason,
            ack.FirstSeq, ack.LastSeq, scope, server.LastSessionId()?.ToString());
        return ack;
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
        string? actionNonce, out WireEnvelope? response, out bool accepted)
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
            supported = supported.Where(capability => capability is not ("command.result.lookup" or "events.replay.page"));
        var trustedAvailable = bootstrapKey is { Length: 32 } && actionNonce is not null
            && client is OmniServer { ReceiptStore: not null } && TryGetWorkspaceHash(out _);
        if (trustedAvailable) supported = supported.Concat(StdioProtocol.TrustedUserActionCapabilities);
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

    private sealed class ReplayFrameTooLargeException : Exception { }
}
