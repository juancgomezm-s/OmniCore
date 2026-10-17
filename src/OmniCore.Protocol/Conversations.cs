using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

public sealed record ConversationRow(string Id, string Title, DateTimeOffset UpdatedAt, bool Current,
    long JournalSequence = 0);
public sealed record ConversationList(IReadOnlyList<ConversationRow> Conversations);

public static class ConversationJson
{
    public static string Encode(ConversationList value) => JsonSerializer.Serialize(value, ConversationJsonContext.Default.ConversationList);
    public static ConversationList? Decode(string json) => JsonSerializer.Deserialize(json, ConversationJsonContext.Default.ConversationList);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConversationList))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;
