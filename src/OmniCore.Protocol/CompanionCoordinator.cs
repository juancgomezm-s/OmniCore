using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

// Portable UI contracts. No renderer, window handle, tool call or shell command crosses this boundary.
public sealed record CompanionDecision(string Id, string Title, string Kind, string[] OptionIds, string[] Labels);
public sealed record CompanionInstance(string Id, string Workspace, string Session, string? Run, string State,
    DateTimeOffset Seen, CompanionDecision[] Decisions, bool AcceptsInstructions = true, bool SupportsLateral = false);
public sealed record CompanionNotice(string Id, string Instance, string Session, string Title, DateTimeOffset At, bool Read = false, bool Resolved = false);
public sealed record CompanionDelivery(string Id, string Target, string Session, string? Run, string Kind,
    string Text, string? Interaction = null, string? Option = null, string Status = "queued", string? Detail = null,
    string? SenderInstance = null, string? SenderSession = null, long Sequence = 0)
{
    public string Origin => Kind == "message" ? "agent" : "user";
    public string Relation => Kind == "message" ? "lateral" : "human";
}
public sealed record CompanionCreatedSession(string SessionId, string Title, string Workspace, bool Running = false);
public sealed record CompanionMailboxPage(CompanionDelivery[] Messages, long Next);
public sealed record CompanionMemory(string Path, string Text, string Version);
public sealed record CompanionChatMessage(string Role, string Text);
public sealed record CompanionChatSettings(string? ModelId = null, bool LocalOnly = false);
public sealed record CompanionModelOption(string Id, string Provider, bool Local);
public sealed record CompanionChatUsage(string Model, string Provider, long Requests = 0, long Responses = 0,
    long Failed = 0, long Input = 0, long Output = 0, long CacheRead = 0, long CacheWrite = 0, long Reasoning = 0,
    long MeasuredTokenRequests = 0, decimal EstimatedUsd = 0, long PricedResponses = 0,
    decimal ReportedUsd = 0, long ReportedCostResponses = 0);
public sealed record CompanionUsageSample(string Id, string Model, string Provider, long? Input = null, long? Output = null,
    long? CacheRead = null, long? CacheWrite = null, long? Reasoning = null, decimal? EstimatedUsd = null, decimal? ReportedUsd = null);
public sealed record CompanionHubSnapshot(CompanionInstance[] Instances, CompanionNotice[] Notices,
    CompanionDelivery[] Deliveries, CompanionMemory Memory, CompanionReaction? Reaction = null);
public sealed record CompanionMemoryEntry(string Id, string Text);
public sealed record CompanionSessionTarget(string InstanceId, string SessionId, string Project, string State);
public sealed record CompanionToolResult(bool Ok, string? Error = null, string? Text = null,
    CompanionMemoryEntry[]? Memories = null, CompanionSessionTarget[]? Sessions = null,
    CompanionNotice[]? Notices = null, CompanionDelivery? Delivery = null, CompanionReaction? Reaction = null, int? Total = null,
    CompanionCreatedSession? CreatedSession = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionInstance))]
[JsonSerializable(typeof(CompanionNotice))]
[JsonSerializable(typeof(CompanionDelivery))]
[JsonSerializable(typeof(CompanionMemory))]
[JsonSerializable(typeof(CompanionHubSnapshot))]
[JsonSerializable(typeof(CompanionChatMessage[]))]
[JsonSerializable(typeof(CompanionChatMessage))]
[JsonSerializable(typeof(CompanionChatSettings))]
[JsonSerializable(typeof(CompanionModelOption[]))]
[JsonSerializable(typeof(CompanionChatUsage))]
[JsonSerializable(typeof(CompanionChatUsage[]))]
[JsonSerializable(typeof(CompanionToolResult))]
[JsonSerializable(typeof(CompanionReaction))]
[JsonSerializable(typeof(CompanionMailboxPage))]
[JsonSerializable(typeof(CompanionCreatedSession))]
public partial class CompanionCoordinatorJson : JsonSerializerContext;
