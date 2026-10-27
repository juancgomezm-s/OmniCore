using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmniCore.Domain;

/// <summary>One passive, untrusted text message in the canonical external transcript format.</summary>
public sealed record ConversationTranscriptMessage(string Role, string Text);

/// <summary>
/// Core-authored receipt event for a neutral external transcript. The source is deliberately
/// limited to user/assistant text; it can never carry policy, instructions, tools, or grants.
/// </summary>
public sealed record ConversationTranscriptImported(string FormatId, int SchemaVersionValue,
    string SourceSha256, string CommandId, string RequestSha256, string? Title,
    IReadOnlyList<ConversationTranscriptMessage> Messages) : IValidatedDomainEventPayload
{
    public EventType Type() => EventType.Of("conversation.transcript_imported");
    public int SchemaVersion() => 1;

    public void Validate()
    {
        if (FormatId != ConversationTranscriptImport.FormatId || SchemaVersionValue != 1
            || !ConversationTranscriptImport.IsSha256(SourceSha256)
            || !Guid.TryParseExact(CommandId, "D", out var commandGuid)
            || !string.Equals(CommandId, commandGuid.ToString("D"), StringComparison.Ordinal)
            || !ConversationTranscriptImport.IsSha256(RequestSha256)
            || Title is { Length: > 120 } || Title?.Any(char.IsControl) == true
            || Messages is null || Messages.Count is < 1 or > ConversationTranscriptImport.MaximumMessages)
            throw new InvalidDataException("Invalid imported transcript provenance.");
        var total = 0;
        foreach (var message in Messages)
        {
            if (message.Role is not ("user" or "assistant") || message.Text is null)
                throw new InvalidDataException("Invalid imported transcript message.");
            int length;
            try { length = ConversationTranscriptImport.StrictUtf8.GetByteCount(message.Text); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Imported transcript is not valid Unicode.", error); }
            if (length > ConversationTranscriptImport.MaximumMessageUtf8Bytes)
                throw new InvalidDataException("Imported transcript message exceeds its byte limit.");
            total = checked(total + length);
        }
        if (total > ConversationTranscriptImport.MaximumTotalTextUtf8Bytes)
            throw new InvalidDataException("Imported transcript exceeds its text byte limit.");
        ConversationTranscriptImport.ValidateProjectionBounds(FormatId, SourceSha256, CommandId,
            RequestSha256, Title, Messages);
    }
}

/// <summary>Strict parser for the Core-owned neutral transcript interchange format.</summary>
public static class ConversationTranscriptImport
{
    public const string FormatId = "omnicore-conversation-import.v1";
    public const int MaximumSourceBytes = 384 * 1024;
    public const int MaximumMessages = 10_000;
    public const int MaximumMessageUtf8Bytes = 64 * 1024;
    public const int MaximumTotalTextUtf8Bytes = 350 * 1024;
    public const int MaximumCommandPayloadBytes = 1024 * 1024 - 4096;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record Document(string Sha256, string? Title,
        IReadOnlyList<ConversationTranscriptMessage> Messages);

    public static Document Parse(ReadOnlySpan<byte> source)
    {
        try { return ParseCore(source); }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or OverflowException or EncoderFallbackException)
        { throw new InvalidDataException("Transcript JSON or field types are invalid.", error); }
    }

    private static Document ParseCore(ReadOnlySpan<byte> source)
    {
        if (source.Length is 0 or > MaximumSourceBytes)
            throw new InvalidDataException("Transcript file size is outside the allowed limit.");
        string json;
        try { json = StrictUtf8.GetString(source); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Transcript must be strict UTF-8.", error); }
        if (json.Length > 0 && json[0] == '\uFEFF') throw new InvalidDataException("Transcript must not contain a UTF-8 BOM.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        ExactProperties(root, ["formatId", "schemaVersion", "title", "messages"], ["formatId", "schemaVersion", "messages"]);
        var formatId = root.GetProperty("formatId");
        if (formatId.ValueKind != JsonValueKind.String || formatId.GetString() != FormatId
            || root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Unsupported transcript format or schema version.");
        string? title = null;
        if (root.TryGetProperty("title", out var titleNode))
        {
            if (titleNode.ValueKind != JsonValueKind.String || titleNode.GetString() is not { } value
                || value.Length > 120 || value.Any(char.IsControl))
                throw new InvalidDataException("Transcript title is invalid.");
            title = value;
        }
        var array = root.GetProperty("messages");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > MaximumMessages)
            throw new InvalidDataException("Transcript message count is outside the allowed limit.");
        var messages = new List<ConversationTranscriptMessage>(array.GetArrayLength());
        var total = 0;
        foreach (var item in array.EnumerateArray())
        {
            ExactProperties(item, ["role", "text"], ["role", "text"]);
            var roleNode = item.GetProperty("role");
            var textNode = item.GetProperty("text");
            var role = roleNode.ValueKind == JsonValueKind.String ? roleNode.GetString() : null;
            if (role is not ("user" or "assistant") || textNode.ValueKind != JsonValueKind.String
                || textNode.GetString() is not { } text)
                throw new InvalidDataException("Transcript messages must contain only user/assistant text.");
            int size;
            try { size = StrictUtf8.GetByteCount(text); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Transcript text is not valid Unicode.", error); }
            if (size > MaximumMessageUtf8Bytes) throw new InvalidDataException("Transcript message exceeds its byte limit.");
            total = checked(total + size);
            if (total > MaximumTotalTextUtf8Bytes) throw new InvalidDataException("Transcript text exceeds its byte limit.");
            messages.Add(new(role, text));
        }
        var sourceSha = Convert.ToHexString(SHA256.HashData(source));
        ValidateProjectionBounds(FormatId, sourceSha, "00000000-0000-0000-0000-000000000000",
            new string('0', 64), title, messages);
        return new(sourceSha, title, messages);
    }

    public static byte[] DecodeBase64Document(string base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > ((MaximumSourceBytes + 2) / 3) * 4)
            throw new InvalidDataException("Encoded transcript is outside the allowed limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException error) { throw new InvalidDataException("Transcript payload is not base64.", error); }
        if (bytes.Length > MaximumSourceBytes || !string.Equals(Convert.ToBase64String(bytes), base64, StringComparison.Ordinal))
            throw new InvalidDataException("Encoded transcript is outside the allowed limit.");
        return bytes;
    }

    public static void ValidateCommandPayloadSize(string commandPayload)
    {
        if (StrictUtf8.GetByteCount(commandPayload) > MaximumCommandPayloadBytes)
            throw new InvalidDataException("Signed transcript command exceeds the protocol frame budget.");
    }

    internal static bool IsSha256(string value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static void ValidateProjectionBounds(string formatId, string sourceSha, string commandId,
        string requestSha, string? title, IReadOnlyList<ConversationTranscriptMessage> messages)
    {
        static byte[] Write(Action<Utf8JsonWriter> build)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) build(writer);
            return stream.ToArray();
        }
        var messageBytes = Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("Role", message.Role);
                writer.WriteString("Text", message.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        if (messageBytes.Length > MaximumCanonicalEventBytes)
            throw new InvalidDataException("Transcript canonical message payload exceeds its storage limit.");
        var canonicalEvent = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("FormatId", formatId);
            writer.WriteNumber("SchemaVersionValue", 1);
            writer.WriteString("SourceSha256", sourceSha);
            writer.WriteString("CommandId", commandId);
            writer.WriteString("RequestSha256", requestSha);
            if (title is null) writer.WriteNull("Title"); else writer.WriteString("Title", title);
            writer.WritePropertyName("Messages");
            writer.WriteRawValue(messageBytes, skipInputValidation: false);
            writer.WriteEndObject();
        });
        if (canonicalEvent.Length > MaximumCanonicalEventBytes)
            throw new InvalidDataException("Transcript canonical event exceeds its storage limit.");

        var payload = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "conversation.transcript_imported");
            writer.WriteString("seq", "999999999999999999");
            writer.WriteString("sessionId", "00000000-0000-0000-0000-000000000000");
            writer.WriteString("origin", "Imported");
            writer.WriteString("reviewRequired", "true");
            writer.WriteString("formatId", formatId);
            writer.WriteString("policy", "ObserveOnly");
            writer.WriteString("sourceSha256", sourceSha);
            writer.WriteString("title", title ?? "");
            writer.WriteString("messageCount", messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("messages", Encoding.UTF8.GetString(messageBytes));
            writer.WriteEndObject();
        });
        var replayEnvelope = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", "1.0");
            writer.WriteString("messageType", "event");
            writer.WriteString("messageId", "00000000-0000-0000-0000-000000000000");
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payload, skipInputValidation: false);
            writer.WriteEndObject();
        });
        if (replayEnvelope.Length > MaximumReplayFrameBytes)
            throw new InvalidDataException("Transcript read model exceeds the protocol frame budget.");
    }

    public const int MaximumCanonicalEventBytes = 512 * 1024;
    public const int MaximumReplayFrameBytes = 1024 * 1024 - 4096;

    private static void ExactProperties(JsonElement element, string[] allowed, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Transcript object is invalid.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!found.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Transcript contains duplicate or unknown properties.");
        if (required.Any(name => !found.Contains(name))) throw new InvalidDataException("Transcript is missing a required property.");
    }
}
