using System.Text.Json.Serialization;

namespace OmniCore.Host;

/// <summary>core.explorer.v1 read-only result schema. Claims are not validation evidence.
/// No plan mutations, writes or arbitrary artifact capabilities are imported from model text.</summary>
internal sealed record DelegationResultDocument(int Version, string Outcome, string Summary,
    IReadOnlyList<string> Findings, IReadOnlyList<string> RemainingIssues);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DelegationResultDocument))]
internal partial class DelegationResultJson : JsonSerializerContext;
