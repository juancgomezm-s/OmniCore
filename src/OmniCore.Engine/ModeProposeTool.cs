namespace OmniCore.Engine;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Declares an advisory recommendation. Only the runtime writes the canonical record.</summary>
public sealed class ModeProposeTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(new ToolId("mode.propose"),
        "Recommends a different Run mode with a reason. Does not change mode, permissions, routes or budgets; requires separate authorization.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"plan\",\"act\",\"orq\"]},\"reason\":{\"type\":\"string\",\"maxLength\":512}},\"required\":[\"mode\",\"reason\"],\"additionalProperties\":false}"),
        new[] { "core" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
        new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return System.Threading.Tasks.Task.FromResult(TryParse(intent.Intent.NormalizedArgumentsJson, out _, out _)
            ? new ToolResult("mode.propose: recommendation recorded; mode remains unchanged", "{}", null,
                2, false, EffectOutcome.None)
            : ToolResult.Error("mode.propose: mode must be plan|act|orq and reason must contain 1–512 characters; no authority fields are accepted"));
    }

    public static bool TryParse(string json, out RunMode mode, out string reason)
    {
        mode = default;
        reason = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Count(p => p.Name == "mode") != 1
                || properties.Count(p => p.Name == "reason") != 1
                || root.GetProperty("mode").ValueKind != JsonValueKind.String
                || root.GetProperty("reason").ValueKind != JsonValueKind.String) return false;
            var selected = root.GetProperty("mode").GetString();
            if (selected is not ("plan" or "act" or "orq")) return false;
            mode = selected switch { "plan" => RunMode.Plan, "act" => RunMode.Act, _ => RunMode.Orchestrate };
            reason = root.GetProperty("reason").GetString()!;
            return !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512;
        }
        catch (JsonException) { return false; }
    }
}
