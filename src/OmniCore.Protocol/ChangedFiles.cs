using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

/// <summary>One journal-attributed filesystem effect, not an inventory of the user's Git changes.</summary>
public sealed record ChangedFile(string EffectId, string Path, string? RunId, string? LaneId,
    string Status, int? Added, int? Deleted, bool DiffAvailable, string? UnavailableReason);
public sealed record ChangedFilesSnapshot(string SessionId, long BasedOnJournalSequence,
    IReadOnlyList<ChangedFile> Files, bool Unavailable = false);
public sealed record FileDiffSnapshot(string SessionId, string EffectId, string Path,
    bool Available, string? Reason, string? Before, string? After);

public static class FilesJson
{
    public static string Encode(ChangedFilesSnapshot value) => JsonSerializer.Serialize(value, FilesJsonContext.Default.ChangedFilesSnapshot);
    public static string Encode(FileDiffSnapshot value) => JsonSerializer.Serialize(value, FilesJsonContext.Default.FileDiffSnapshot);
    public static ChangedFilesSnapshot? Decode(string json) => JsonSerializer.Deserialize(json, FilesJsonContext.Default.ChangedFilesSnapshot);
    public static FileDiffSnapshot? DecodeDiff(string json) => JsonSerializer.Deserialize(json, FilesJsonContext.Default.FileDiffSnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChangedFilesSnapshot))]
[JsonSerializable(typeof(FileDiffSnapshot))]
internal partial class FilesJsonContext : JsonSerializerContext;
