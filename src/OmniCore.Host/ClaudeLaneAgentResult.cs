namespace OmniCore.Host;

using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Domain;
using OmniCore.Tools;

internal sealed record ClaudeLaneAgentResultDocument(string Outcome, string Summary,
    string[] Findings, string[] FilesChanged, string[] RemainingIssues,
    string Confidence, JsonElement[] ProposedPlanMutations);

internal sealed record ClaudeLaneValidatedAgentResult(AgentResult Result, string SchemaId);

internal static partial class ClaudeLaneAgentResultValidator
{
    internal const string SchemaId = "core.claude-agent-result.v1";
    internal const string JsonSchema = """
        {"type":"object","additionalProperties":false,"required":["outcome","summary","findings","filesChanged","remainingIssues","confidence","proposedPlanMutations"],"properties":{"outcome":{"type":"string","enum":["Succeeded","Failed","Blocked","Cancelled"]},"summary":{"type":"string","maxLength":32768},"findings":{"type":"array","maxItems":256,"items":{"type":"string","maxLength":8192}},"filesChanged":{"type":"array","maxItems":256,"items":{"type":"string","maxLength":1024}},"remainingIssues":{"type":"array","maxItems":256,"items":{"type":"string","maxLength":8192}},"confidence":{"type":"string","enum":["Low","Medium","High"]},"proposedPlanMutations":{"type":"array","maxItems":0}}}
        """;

    internal static ClaudeLaneValidatedAgentResult? Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_048_576) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            EnsureUniqueMembers(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var value = JsonSerializer.Deserialize(json, ClaudeLaneAgentResultJsonContext.Default.ClaudeLaneAgentResultDocument);
            if (value is null || value.Summary is null || value.Summary.Length > 32_768
                || value.Findings is null || value.Findings.Length > 256
                || value.FilesChanged is null || value.FilesChanged.Length > 256
                || value.RemainingIssues is null || value.RemainingIssues.Length > 256
                || value.ProposedPlanMutations is null || value.ProposedPlanMutations.Length != 0
                || !Enum.TryParse<AgentOutcome>(value.Outcome, ignoreCase: false, out var outcome)
                || !Enum.IsDefined(outcome)
                || !Enum.TryParse<ConfidenceLevel>(value.Confidence, ignoreCase: false, out var confidence)
                || !Enum.IsDefined(confidence)
                || value.Findings.Any(text => !Bounded(text, 8_192))
                || value.RemainingIssues.Any(text => !Bounded(text, 8_192))
                || value.FilesChanged.Any(path => !FilesystemReconciliationMetadata.IsSaneRelativePath(path)
                    || new RedactionPolicy().IsSecretPath(path))) return null;
            return new(new AgentResult(outcome, value.Summary, value.Findings, [], value.FilesChanged,
                value.RemainingIssues, confidence, []), SchemaId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    internal static string SerializeRedacted(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var redactor = new PiiRedactor();
        var wire = new ClaudeLaneAgentResultDocument(result.Outcome.ToString(),
            redactor.Redact(result.Summary ?? string.Empty),
            result.Findings.Select(redactor.Redact).ToArray(), result.FilesChanged.ToArray(),
            result.RemainingIssues.Select(redactor.Redact).ToArray(), result.Confidence.ToString(), []);
        return JsonSerializer.Serialize(wire, ClaudeLaneAgentResultJsonContext.Default.ClaudeLaneAgentResultDocument);
    }

    private static bool Bounded(string? value, int limit) => value is not null && value.Length <= limit;

    private static void EnsureUniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidOperationException("duplicate");
                EnsureUniqueMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) EnsureUniqueMembers(item);
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(ClaudeLaneAgentResultDocument))]
    private sealed partial class ClaudeLaneAgentResultJsonContext : JsonSerializerContext;
}
