namespace OmniCore.Host;

using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record LifecycleEvidenceJson(long Sequence, string Type, string? Task,
    string? Lane, string? Turn, IReadOnlyList<string> Artifacts, string? Provenance = null,
    string? Tool = null, string? Outcome = null, string? Effect = null, string? Summary = null);
internal sealed record LifecycleValidationContextJson(string SessionId, string RunId, string TurnId,
    string Objective, IReadOnlyList<LifecycleEvidenceJson> Evidence, bool Truncated = false);
internal sealed record LifecycleValidationOutcomeJson(string Key, bool Passed);
internal sealed record LifecycleAfterValidationJson(string Context,
    IReadOnlyList<LifecycleValidationOutcomeJson> Results);
internal sealed record LifecycleCompactionJson(string RunId, string TurnId, int ItemCount,
    int InputCharacters, string Policy, int? OutputCharacters = null, string? Summary = null);
internal sealed record ExtensionCompletionRequestJson(string SessionId, string RunId,
    IReadOnlyList<string>? Validators, IReadOnlyList<string>? Gates, JsonElement? RunContext);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LifecycleValidationContextJson))]
[JsonSerializable(typeof(LifecycleEvidenceJson))]
[JsonSerializable(typeof(LifecycleAfterValidationJson))]
[JsonSerializable(typeof(LifecycleValidationOutcomeJson))]
[JsonSerializable(typeof(LifecycleCompactionJson))]
[JsonSerializable(typeof(ExtensionCompletionRequestJson))]
internal sealed partial class LifecycleJsonContext : JsonSerializerContext { }
