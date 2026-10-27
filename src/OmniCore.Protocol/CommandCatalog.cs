namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Wire-safe Host command catalog shared by CLI and external clients (ADR-0024 §3).</summary>
public sealed record CommandCatalogEntry(string Id, string Name, IReadOnlyList<string> Aliases, string Kind,
    string Description, IReadOnlyList<string> Arguments, string Source, bool Enabled, string? DisabledReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? UiDescriptions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    /// <summary>Availability of the qualified ID before short-name precedence is applied.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanonicalEnabled { get; init; }
}

public sealed record CommandCatalogSnapshot(IReadOnlyList<CommandCatalogEntry> Commands);

/// <summary>Bounded native command catalog. Revisions are observations, not authority.</summary>
public sealed record NativeCommandCatalogSnapshot(string? SessionId, string? RunId, long? ModeRevision,
    long? PolicyRevision, int SelectionRevision, long? RoutingPolicyRevision, string CatalogRevision,
    IReadOnlyList<NativeCommandCatalogEntry> Commands);

public sealed record NativeCommandCatalogEntry(string CanonicalCommandId, string Kind, bool Enabled,
    string? DisabledReason, string SourceFingerprint, string Description, IReadOnlyList<string> Arguments);

/// <summary>Renderer-safe status for a durable command admission. Sensitive inputs/results are omitted.</summary>
public sealed record NativeCommandRecoverySnapshot(IReadOnlyList<NativeCommandRecoveryEntry> Invocations);

public sealed record NativeCommandRecoveryEntry(string InvocationId, string? SessionId, string? RunId,
    string CanonicalCommandId, string Kind, string State, string? ReasonCode, long Sequence);

/// <summary>Read-only workflow continuation state derived from the durable invocation and stage journal.</summary>
public sealed record NativeWorkflowRecoverySnapshot(string? SessionId, string? RunId, long? ModeRevision,
    long? PolicyRevision, IReadOnlyList<NativeWorkflowRecoveryEntry> Invocations);

public sealed record NativeWorkflowRecoveryEntry(string InvocationId, string SessionId, string RunId,
    string CanonicalCommandId, string WorkflowId, string State, string? ReasonCode, string? Stage, long Sequence);

public sealed record WorkflowRequestDto(string WorkflowId, string Version, IReadOnlyList<string> Arguments);

public static class CommandCatalogJson
{
    public static string Encode(CommandCatalogSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, CatalogJsonContext.Default.CommandCatalogSnapshot);

    public static string Encode(WorkflowRequestDto request) =>
        JsonSerializer.Serialize(request, CatalogJsonContext.Default.WorkflowRequestDto);

    public static string Encode(NativeCommandCatalogSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, CatalogJsonContext.Default.NativeCommandCatalogSnapshot);

    public static string Encode(NativeCommandRecoverySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, CatalogJsonContext.Default.NativeCommandRecoverySnapshot);

    public static string Encode(NativeWorkflowRecoverySnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, CatalogJsonContext.Default.NativeWorkflowRecoverySnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandCatalogSnapshot))]
[JsonSerializable(typeof(CommandCatalogEntry))]
[JsonSerializable(typeof(WorkflowRequestDto))]
[JsonSerializable(typeof(NativeCommandCatalogSnapshot))]
[JsonSerializable(typeof(NativeCommandCatalogEntry))]
[JsonSerializable(typeof(NativeCommandRecoverySnapshot))]
[JsonSerializable(typeof(NativeCommandRecoveryEntry))]
[JsonSerializable(typeof(NativeWorkflowRecoverySnapshot))]
[JsonSerializable(typeof(NativeWorkflowRecoveryEntry))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
