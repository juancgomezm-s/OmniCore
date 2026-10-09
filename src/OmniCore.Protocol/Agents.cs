using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

/// <summary>Canonical Lane/Task/executor facts, never inferred worker liveness.</summary>
public sealed record AgentLaneSnapshot(string LaneId, string TaskId, string? ParentTaskId, string Objective,
    string LaneState, string TaskState, string ProfileId, long? ProfileRevision,
    string? ExecutionId, string? ParentExecutionId, string? ExecutionState,
    string? DelegationId, string? DelegationState, string? Model,
    string? LastContextEventId, bool ExecutionAmbiguous, IReadOnlyList<string>? SelectableContextItemIds = null,
    string? ResultId = null, string? ResultDisposition = null, string? ResultSummary = null,
    IReadOnlyList<string>? PendingJoinIds = null, bool CancellationRequested = false,
    AgentBudgetSnapshot? Budget = null, string? SupervisionState = null,
    int PendingMailboxMessages = 0, int PendingWakeRequests = 0, int? ResultIssueCount = null,
    bool IntegrationVerified = false, bool IntegrationValidationPassed = false,
    int ToolBackedEvidenceCount = 0);
public sealed record AgentBudgetSnapshot(decimal? MaxCostUsd, decimal? CostUsedUsd, long? MaxTokens,
    long? TokensUsed, int? MaxTurns, int TurnsUsed, int? MaxToolCalls, int ToolCallsUsed);
public sealed record AgentCapacitySnapshot(int Active, int Waiting, int Maximum, bool WriterActive,
    IReadOnlyList<string> WaitingDelegationIds);
public sealed record AgentsSnapshot(string SessionId, string? RunId, long BasedOnJournalSequence,
    bool ProjectionUnavailable, IReadOnlyList<AgentLaneSnapshot> Lanes, AgentCapacitySnapshot? Capacity = null,
    IReadOnlyList<FanOutGroupSnapshot>? FanOutGroups = null);
public sealed record FanOutGroupSnapshot(string GroupId, string OwnerExecutionId, string Policy,
    IReadOnlyList<string> DelegationIds, string State, IReadOnlyList<string> MemberResultIds, string? AggregateId);

/// <summary>Explicit user request. Empty selection means no inherited parent content.
/// Budgets bound queued work; they do not grant tools, routing or worker capacity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DelegationCreateRequest(string ProfileId, string SourceEventId, string Objective,
    IReadOnlyList<string> SelectedItemIds, int MaximumPacketBytes, int MaxTurns, int MaxToolCalls,
    long MaxTokens, decimal MaxCostUsd, int Priority = 0);

public static class AgentsJson
{
    public static string Encode(AgentsSnapshot snapshot) => JsonSerializer.Serialize(snapshot, AgentsJsonContext.Default.AgentsSnapshot);
    public static AgentsSnapshot? Decode(string json) => JsonSerializer.Deserialize(json, AgentsJsonContext.Default.AgentsSnapshot);
    public static string EncodeRequest(DelegationCreateRequest request) => JsonSerializer.Serialize(request, AgentsJsonContext.Default.DelegationCreateRequest);
    public static DelegationCreateRequest? DecodeRequest(string json) => JsonSerializer.Deserialize(json, AgentsJsonContext.Default.DelegationCreateRequest);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentsSnapshot))]
[JsonSerializable(typeof(DelegationCreateRequest))]
[JsonSerializable(typeof(AgentCapacitySnapshot))]
[JsonSerializable(typeof(AgentBudgetSnapshot))]
[JsonSerializable(typeof(FanOutGroupSnapshot))]
internal partial class AgentsJsonContext : JsonSerializerContext;
