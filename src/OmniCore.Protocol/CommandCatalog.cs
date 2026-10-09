namespace OmniCore.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Wire-safe Host command catalog shared by CLI and external clients (ADR-0024 §3).</summary>
public sealed record CommandCatalogEntry(string Id, string Name, IReadOnlyList<string> Aliases, string Kind,
    string Description, IReadOnlyList<string> Arguments, string Source, bool Enabled, string? DisabledReason);

public sealed record CommandCatalogSnapshot(IReadOnlyList<CommandCatalogEntry> Commands);

public sealed record WorkflowRequestDto(string WorkflowId, string Version, IReadOnlyList<string> Arguments);

public static class CommandCatalogJson
{
    public static string Encode(CommandCatalogSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, CatalogJsonContext.Default.CommandCatalogSnapshot);

    public static string Encode(WorkflowRequestDto request) =>
        JsonSerializer.Serialize(request, CatalogJsonContext.Default.WorkflowRequestDto);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandCatalogSnapshot))]
[JsonSerializable(typeof(CommandCatalogEntry))]
[JsonSerializable(typeof(WorkflowRequestDto))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}
