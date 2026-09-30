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

/// <summary>
/// Genera ids de comando/evento UUIDv7 (RFC 9562 §5.7) en texto canónico. Se construye a mano
/// porque esta librería compila también para net8, que no tiene <c>Guid.CreateVersion7</c>: 48 bits
/// de milisegundos Unix, versión 7, variante RFC y el resto aleatorio criptográfico.
/// </summary>
public sealed class Ids
{
    public static string NewV7()
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte) (millis >> 40);
        bytes[1] = (byte) (millis >> 32);
        bytes[2] = (byte) (millis >> 24);
        bytes[3] = (byte) (millis >> 16);
        bytes[4] = (byte) (millis >> 8);
        bytes[5] = (byte) millis;
        bytes[6] = (byte) ((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte) ((bytes[8] & 0x3F) | 0x80);

        var hex = new System.Text.StringBuilder(36);
        for (var i = 0; i < 16; i++)
        {
            if (i == 4 || i == 6 || i == 8 || i == 10)
            {
                hex.Append('-');
            }

            hex.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }
}

/// <summary>Appenda JSON manual determinista (solo objetos planos de strings/booleans).</summary>
public sealed class JsonObj
{
    public static string Empty() => "{}";

    public static string Field(string key, string value) => "\"" + key + "\":\"" + Escape(value) + "\"";

    public static string FieldBool(string key, bool value) => "\"" + key + "\":" + (value ? "true" : "false");

    public static string FieldRaw(string key, string rawValue) => "\"" + key + "\":" + rawValue;

    /// <summary>
    /// Parsea un objeto JSON plano a un mapa de strings: los valores string se devuelven
    /// decodificados; el resto (números, booleanos, objetos) como su texto JSON. Un JSON que no es un
    /// objeto devuelve un mapa vacío. Usa System.Text.Json: las comillas y escapes dentro de los
    /// valores se respetan.
    /// </summary>
    public static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>();
        if (json is null || json.Trim().Length == 0)
        {
            return result;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // JSON inválido: mapa vacío (el llamador trata la ausencia de campos).
        }

        return result;
    }

    /// <summary>Escapa un texto para meterlo entre comillas en JSON (RFC 8259).</summary>
    public static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int) c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Decodifica el contenido de un string JSON (sin las comillas externas).</summary>
    public static string Unescape(string value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse("\"" + value + "\"");
            return document.RootElement.GetString() ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            return value;
        }
    }
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