using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

// Portable UI contracts. No renderer, window handle, tool call or shell command crosses this boundary.
public sealed record CompanionDecision(string Id, string Title, string Kind, string[] OptionIds, string[] Labels);
public sealed record CompanionInstance(string Id, string Workspace, string Session, string? Run, string State,
    DateTimeOffset Seen, CompanionDecision[] Decisions);
public sealed record CompanionNotice(string Id, string Instance, string Session, string Title, DateTimeOffset At, bool Read = false, bool Resolved = false);
public sealed record CompanionDelivery(string Id, string Target, string Session, string? Run, string Kind,
    string Text, string? Interaction = null, string? Option = null, string Status = "queued", string? Detail = null);
public sealed record CompanionMemory(string Path, string Text, string Version);
public sealed record CompanionChatMessage(string Role, string Text);
public sealed record CompanionHubSnapshot(CompanionInstance[] Instances, CompanionNotice[] Notices,
    CompanionDelivery[] Deliveries, CompanionMemory Memory);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionInstance))]
[JsonSerializable(typeof(CompanionNotice))]
[JsonSerializable(typeof(CompanionDelivery))]
[JsonSerializable(typeof(CompanionMemory))]
[JsonSerializable(typeof(CompanionHubSnapshot))]
[JsonSerializable(typeof(CompanionChatMessage[]))]
[JsonSerializable(typeof(CompanionChatMessage))]
public partial class CompanionCoordinatorJson : JsonSerializerContext;
