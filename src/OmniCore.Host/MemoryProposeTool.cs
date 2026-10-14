namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Model proposal entrypoint; all content goes through the Session memory policy.</summary>
public sealed class MemoryProposeTool(SessionMemoryPolicy policy) : ITool
{
    private const string Schema = "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"+
        "\"content\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2048},"+
        "\"kind\":{\"type\":\"string\",\"enum\":[\"Fact\",\"Preference\",\"Decision\",\"Convention\",\"Procedure\",\"Pitfall\",\"Architecture\",\"Environment\",\"Summary\"]}},"+
        "\"required\":[\"content\",\"kind\"]}";
    private readonly ToolDescriptor _descriptor = new(new ToolId("memory.propose"),
        "Propose a concise fact, preference, decision, convention, procedure, pitfall, architecture, environment, or summary for Session memory. Proposals are screened and may be rejected or require user confirmation; never include secrets or third-party personal data.",
        new InputSchema(Schema), ["memory", "session"], true, false, ToolRisk.Low,
        ComponentSource.Core(), ToolProtection.None, EffectClass.None);
    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var json = JsonDocument.Parse(call.NormalizedArgumentsJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<MemoryKind>(kind.GetString(), false, out _))
                return new PreparationRejected("memory.propose: invalid candidate arguments", null, ToolErrorCode.InvalidArguments);
            return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));
        }
        catch (JsonException) { return new PreparationRejected("memory.propose: invalid JSON", null, ToolErrorCode.InvalidArguments); }
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.EmitEvent is null) return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.ToolFailure,
            "memory.propose: durable event sink unavailable"));
        try
        {
            using var json = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
            var root = json.RootElement;
            var content = root.GetProperty("content").GetString()!;
            var kind = Enum.Parse<MemoryKind>(root.GetProperty("kind").GetString()!, false);
            var candidate = policy.Propose(content, kind, MemoryCandidateOrigin.Model);
            return System.Threading.Tasks.Task.FromResult(ToolResult.Ok("memory candidate " + candidate.Decision,
                "{\"candidateId\":\"" + candidate.Id + "\",\"decision\":\"" + candidate.Decision + "\"}",
                candidate.Id.Length, false, EffectOutcome.None));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments, "memory.propose: candidate rejected by policy"));
        }
    }
}
