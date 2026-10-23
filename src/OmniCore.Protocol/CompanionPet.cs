using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

/// <summary>Portable companion contract. Appearance/animation IDs are renderer independent.</summary>
public sealed record CompanionPetSnapshot(bool Discovered, long Revision, string? Name, string? Species,
    string AppearanceId, string Variant, int Xp, int Level, int Mood, string Stage,
    int Interactions, int HatchAfter, string RulesVersion, CompanionClientPreferences Preferences,
    CompanionReaction? Reaction = null, string? GuestId = null);

public sealed record CompanionClientPreferences(bool Visible = true, bool ReducedMotion = false, string RenderMode = "auto");
public sealed record CompanionReaction(string AnimationId, DateTimeOffset Until, int Priority);
public sealed record CompanionPetAction(string Action, string? Name = null, string ClientId = "terminal",
    bool? ReducedMotion = null, string? RenderMode = null);
public sealed record CompanionPetChanged(long Revision, CompanionPetSnapshot State);

public static class CompanionPetProtocol
{
    public const string Query = "companion.pet";
    public const string Command = "companion.pet";
    public const string Changed = "companion.pet.changed";
    public static readonly string[] Species = ["ox", "fox", "owl", "beaver", "cat", "capybara"];
    public static readonly string[] Animations = ["idle", "working", "waiting", "success", "failure", "pet", "sleep"];
    public static string Encode(CompanionPetSnapshot state) => JsonSerializer.Serialize(state, CompanionPetJson.Default.CompanionPetSnapshot);
    public static CompanionPetSnapshot? Decode(string json) => JsonSerializer.Deserialize(json, CompanionPetJson.Default.CompanionPetSnapshot);
    public static string EncodeAction(CompanionPetAction action) => JsonSerializer.Serialize(action, CompanionPetJson.Default.CompanionPetAction);
    public static CompanionPetAction? DecodeAction(string json) => JsonSerializer.Deserialize(json, CompanionPetJson.Default.CompanionPetAction);
    public static string EncodeChange(CompanionPetChanged change) => JsonSerializer.Serialize(change, CompanionPetJson.Default.CompanionPetChanged);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionPetSnapshot))]
[JsonSerializable(typeof(CompanionPetAction))]
[JsonSerializable(typeof(CompanionPetChanged))]
internal partial class CompanionPetJson : JsonSerializerContext;
