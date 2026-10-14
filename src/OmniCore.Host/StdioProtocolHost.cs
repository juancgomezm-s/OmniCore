namespace OmniCore.Host;

using OmniCore.Protocol;
using System.Text.Json;

/// <summary>Request/response stdio adapter over the same IOmniClient used by the in-process CLI.</summary>
public static class StdioProtocolHost
{
    public static int Run(IOmniClient client, Stream input, Stream output, TextWriter diagnostics,
        CancellationToken cancellationToken = default, Func<string, string>? localizeDiagnostic = null)
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
                    if (!TryNegotiate(request, out var response, out var accepted))
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
                    var result = Dispatch(client, request);
                    WireFrame.Write(output, WireEnvelope.Response(request.MessageId, result).ToJson());
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException
                    or InvalidOperationException or NotSupportedException)
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

    private static string Dispatch(IOmniClient client, WireEnvelope request)
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
                var commandJson = root.GetProperty("command").GetRawText();
                var command = ParseEnvelope(commandJson);
                if (command.Version != ProtocolVersions.V1 || command.MessageType != MessageTypes.Command)
                    throw new InvalidDataException();
                using var payload = JsonDocument.Parse(command.PayloadJson);
                if (!payload.RootElement.TryGetProperty("cmd", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !StdioProtocol.IsSupportedCommand(nameElement.GetString()!))
                    throw new InvalidDataException();
                var ack = client.Send(command, CancellationToken.None);
                var outcome = ack.Outcome is null ? "null" : "{\"kind\":"
                    + Quote(ack.Outcome.Kind.ToString()) + ",\"reason\":"
                    + (ack.Outcome.Reason is null ? "null" : Quote(ack.Outcome.Reason)) + "}";
                return "{\"ack\":{\"commandId\":" + Quote(ack.CommandId)
                    + ",\"status\":" + Quote(ack.Status) + ",\"error\":"
                    + (ack.Error is null ? "null" : Quote(ack.Error)) + ",\"outcome\":" + outcome
                    + ",\"firstSeq\":" + (ack.FirstSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")
                    + ",\"lastSeq\":" + (ack.LastSeq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}}";
            }
            case StdioProtocol.QueryOperation:
            {
                var name = root.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name) || !StdioProtocol.IsSupportedQuery(name))
                    throw new InvalidDataException();
                var result = client.Query(name, CancellationToken.None);
                return "{\"name\":" + Quote(result?.Name ?? name) + ",\"json\":"
                    + (result is null ? "null" : Quote(result.Json)) + "}";
            }
            case StdioProtocol.SubscribeOperation:
            {
                var from = root.GetProperty("fromSequence").GetInt64();
                if (from < 0) throw new InvalidDataException();
                var events = client.SubscribeSince(from);
                var encodedEvents = EncodeEvents(events);
                return "{\"events\":[" + encodedEvents + "]}";
            }
            default: throw new InvalidDataException();
        }
    }

    private static string EncodeEvents(IReadOnlyList<WireEnvelope> events)
    {
        var builder = new System.Text.StringBuilder();
        var byteCount = 0;
        foreach (var item in events)
        {
            var json = item.ToJson();
            var count = System.Text.Encoding.UTF8.GetByteCount(json) + (builder.Length == 0 ? 0 : 1);
            if (byteCount + count > StdioProtocol.MaximumFrameBytes - 512)
                throw new InvalidDataException("Event replay exceeds the maximum stdio response frame.");
            if (builder.Length > 0) builder.Append(',');
            builder.Append(json);
            byteCount += count;
        }
        return builder.ToString();
    }

    private static bool TryNegotiate(WireEnvelope request, out WireEnvelope? response, out bool accepted)
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
        var capabilities = "[" + string.Join(",", StdioProtocol.SupportedCapabilities.Select(Quote)) + "]";
        response = WireEnvelope.Response(request.MessageId,
            "{\"protocolVersion\":\"1.0\",\"capabilities\":" + capabilities + "}");
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

    private static string Quote(string value) => "\"" + JsonObj.Escape(value) + "\"";
}
