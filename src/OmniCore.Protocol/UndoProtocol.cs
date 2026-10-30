namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Explicit human compensation, never a model tool. Revisions bind the displayed review.</summary>
public static class UndoProtocol
{
    public const string Query = "workspace.undo";
    public const string Apply = "workspace.undo.apply";
    public static string QueryFor(string effect) => Query + ":" + Guid.Parse(effect).ToString("D");
    public static string Encode(UndoReview review) => JsonSerializer.Serialize(review, UndoJson.Default.UndoReview);
    public static UndoReview? Decode(string json) => JsonSerializer.Deserialize(json, UndoJson.Default.UndoReview);
}

public sealed record UndoReview(string? SessionId, string Revision, IReadOnlyList<UndoReviewFile> Files,
    string? Outcome = null, string? Error = null)
{
    public string? RunId { get; init; }
    public string? PendingEffectId { get; init; }
    public bool RecoveryRequired { get; init; }
}
public sealed record UndoReviewFile(string EffectId, string Path, string State, string Operation,
    long RestoreBytes, string? CurrentHash, string? RestoreHash)
{
    public FileDiffSnapshot? Diff { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UndoReview))]
internal partial class UndoJson : JsonSerializerContext;
