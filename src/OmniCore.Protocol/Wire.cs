namespace OmniCore.Protocol;

/// <summary>
/// Envelope wire de mensajes (ADR-0013 §1). Los DTOs de Protocol usan sus propios tipos de id
/// (string) y enums; nunca referencian Domain (ADR-0013 §3). Compila para net10 y net8
/// (lo consume OmniCoder): sin APIs exclusivas de net10 y con codificación JSON manual.
/// </summary>
public sealed class WireEnvelope
{
    public string Version { get; }

    public string MessageType { get; }

    public string MessageId { get; }

    public string PayloadJson { get; }

    public string? CausationId { get; }

    private WireEnvelope(string version, string messageType, string messageId, string payloadJson,
        string? causationId)
    {
        Version = version;
        MessageType = messageType;
        MessageId = messageId;
        PayloadJson = payloadJson;
        CausationId = causationId;
    }

    public static WireEnvelope Event(string eventId, string payloadJson) =>
        new(ProtocolVersions.V1, MessageTypes.Event, eventId, payloadJson, null);

    public static WireEnvelope Response(string messageId, string payloadJson) =>
        new(ProtocolVersions.V1, MessageTypes.Response, messageId, payloadJson, messageId);

    public static WireEnvelope Command(string commandId, string payloadJson) =>
        new(ProtocolVersions.V1, MessageTypes.Command, commandId, payloadJson, null);

    public static WireEnvelope Hello() =>
        new(ProtocolVersions.V1, MessageTypes.Hello, Ids.NewV7(), JsonObj.Empty(), null);

    public string ToJson() => EnvelopeJson.Encode(this);

    public static WireEnvelope FromJson(string json)
    {
        var map = JsonObj.Parse(json);
        var version = map.TryGetValue("version", out var v) ? v! : ProtocolVersions.V1;
        var messageType = map.TryGetValue("messageType", out var m) ? m! : MessageTypes.Event;
        var messageId = map.TryGetValue("messageId", out var mid) ? mid! : "";
        var payload = map.TryGetValue("payload", out var p) ? p! : "{}";
        var causation = map.TryGetValue("causationId", out var c) ? c : null;
        return new WireEnvelope(version, messageType, messageId, payload, causation);
    }

    public static string NewId() => Ids.NewV7();
}

/// <summary>Versiones del protocolo wire.</summary>
public sealed class ProtocolVersions
{
    public static readonly string V1 = "1.0";

    public static readonly string V1_0 = "1.0";
}

/// <summary>Nombres de tipos de mensaje (strings estables, ADR-0013).</summary>
public sealed class MessageTypes
{
    public static readonly string Event = "event";

    public static readonly string Command = "command";

    public static readonly string Response = "response";

    public static readonly string Error = "error";

    public static readonly string Hello = "hello";
}

/// <summary>Acuse de un command (ADR-0019 §1).</summary>
public sealed class CommandAck
{
    public string CommandId { get; }

    public string Status { get; }

    public string? Error { get; }

    public CommandAck(string commandId, string status, string? error)
    {
        CommandId = commandId;
        Status = status;
        Error = error;
    }

    public static CommandAck Ok(string commandId) => new(commandId, "ok", null);

    public static CommandAck Fail(string commandId, string error) => new(commandId, "error", error);

    public static CommandAck FailWithCause(string commandId, string error, string cause) =>
        new(commandId, "error", error + " :: " + cause);
}

/// <summary>Genera ids de comando/evento únicos y monótonos (portables net8/net10).</summary>
public sealed class Ids
{
    private static long _counter;

    private static readonly string _epoch = DateTimeOffset.Now.ToString();

    public static string NewV7()
    {
        _counter += 1;
        return "id-" + _epoch + "-" + _counter;
    }
}

/// <summary>Appenda JSON manual determinista (solo objetos planos de strings/booleans).</summary>
public sealed class JsonObj
{
    public static string Empty() => "{}";

    public static string Field(string key, string value) => "\"" + key + "\":\"" + Escape(value) + "\"";

    public static string FieldBool(string key, bool value) => "\"" + key + "\":" + (value ? "true" : "false");

    public static string FieldRaw(string key, string rawValue) => "\"" + key + "\":" + rawValue;

    /// <summary>Parsea un objeto plano a un mapa de strings (valores no-string se omiten).</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>();
        var trimmed = json.Trim();
        var s = trimmed.Length >= 2 && trimmed[0] == '{' ? trimmed.Substring(1, trimmed.Length - 2) : trimmed;
        var i = 0;
        while (i < s.Length)
        {
            var colon = s.IndexOf(':', i);
            if (colon < 0)
            {
                break;
            }

            var key = Unquote(s.Substring(i, colon - i).Trim());
            var valueStart = colon + 1;
            while (valueStart < s.Length && s[valueStart] == ' ')
            {
                valueStart += 1;
            }

            if (valueStart >= s.Length || s[valueStart] != '"')
            {
                var commaAfter = s.IndexOf(',', valueStart);
                i = commaAfter < 0 ? s.Length : commaAfter + 1;
                continue;
            }

            var valueEnd = s.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
            {
                break;
            }

            result[key] = Unescape(s.Substring(valueStart + 1, valueEnd - valueStart - 1));
            var next = s.IndexOf(',', valueEnd);
            i = next < 0 ? s.Length : next + 1;
        }

        return result;
    }

    private static string Unquote(string token) => token.Length >= 2 ? Unescape(token.Substring(1, token.Length - 2)) : token;

    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");
}

/// <summary>Codificador de wire envelopes a JSON.</summary>
public sealed class EnvelopeJson
{
    public static string Encode(WireEnvelope envelope)
    {
        var parts = new List<string>();
        parts.Add(JsonObj.Field("version", envelope.Version));
        parts.Add(JsonObj.Field("messageType", envelope.MessageType));
        parts.Add(JsonObj.Field("messageId", envelope.MessageId));
        parts.Add(JsonObj.FieldRaw("payload", envelope.PayloadJson));
        if (envelope.CausationId is not null)
        {
            parts.Add(JsonObj.Field("causationId", envelope.CausationId!));
        }

        return "{" + string.Join(",", parts.ToArray()) + "}";
    }
}

/// <summary>Fábrica de comandos tipados del protocolo.</summary>
public sealed class Commands
{
    public static WireEnvelope Sim(string scenarioYaml, bool json, bool resume) =>
        WireEnvelope.Command(Ids.NewV7(), ScenarioPayload.Encode(scenarioYaml, json, resume));

    public static WireEnvelope Query(string name) =>
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("query", name) + "}");
}

/// <summary>Payload del comando de simulación (ADR-0041).</summary>
public sealed class ScenarioPayload
{
    public static string Encode(string scenarioYaml, bool json, bool resume)
    {
        var parts = new string[] {
            JsonObj.Field("scenario", scenarioYaml),
            JsonObj.FieldBool("json", json),
            JsonObj.FieldBool("resume", resume),
        };
        return "{" + string.Join(",", parts) + "}";
    }
}

/// <summary>Resultado de una query del servidor.</summary>
public sealed class SessionQueryResult
{
    public string Name { get; }

    public string Json { get; }

    public SessionQueryResult(string name, string json)
    {
        Name = name;
        Json = json;
    }
}

/// <summary>Contrato mínimo del cliente (ADR-0019 §1). Solo usa tipos de Protocol.</summary>
public interface IOmniClient
{
    CommandAck Send(WireEnvelope command, CancellationToken cancellationToken);

    IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence);

    SessionQueryResult? Query(string name, CancellationToken cancellationToken);
}