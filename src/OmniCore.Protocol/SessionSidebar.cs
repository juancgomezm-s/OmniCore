using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

/// <summary>Read-only, replayable presentation of the active Run's logical plan. No TaskGraph or telemetry.</summary>
public sealed record SidebarPlanItem(string Id, string? ParentId, string Description, string State, int Order);
public sealed record SidebarPlan(string Id, string RunId, int Revision, IReadOnlyList<SidebarPlanItem> Items);
public sealed record SessionSidebarSnapshot(string SessionId, string? RunId, long BasedOnJournalSequence,
    string Title, string? Objective, string? Mode, string? RunState, SidebarPlan? Plan,
    bool RecoveryBlocked, bool ProjectionUnavailable);

public static class SidebarJson
{
    public static string Encode(SessionSidebarSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, SidebarJsonContext.Default.SessionSidebarSnapshot);
    public static SessionSidebarSnapshot? Decode(string json) =>
        JsonSerializer.Deserialize(json, SidebarJsonContext.Default.SessionSidebarSnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionSidebarSnapshot))]
internal partial class SidebarJsonContext : JsonSerializerContext;
