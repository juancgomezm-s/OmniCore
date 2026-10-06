namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Models;

/// <summary>Fingerprints resolved runtime facts, not phase names or guessed configuration.</summary>
internal static class RuntimeFingerprintFactory
{
    internal static ExecutionFingerprint Create(ModelDefinition model, EffectiveModelProfile profile,
        HarnessPolicy harness, ModelSelection selection, string harnessHash, string contextPolicyHash,
        string modelPolicyHash, string tokenizerIdentity)
    {
        var components = new List<FingerprintComponent>
        {
            RuntimeBuildIdentity.ComponentFor(typeof(OmniCliRuntime).Assembly),
            Component("model.descriptor", writer =>
            {
                writer.WriteString("modelId", model.Id);
                writer.WriteString("providerId", model.ProviderId);
                writer.WriteNumber("contextWindow", model.ContextWindow);
                writer.WriteNumber("recommendedUsableContext", model.RecommendedUsableContext);
                writer.WriteNumber("maxOutputTokens", model.MaxOutputTokens);
                if (model.ParameterCountBillions is { } parameters) writer.WriteNumber("parameterCountBillions", parameters);
                else writer.WriteNull("parameterCountBillions");
                writer.WriteStartArray("aliases");
                foreach (var alias in model.Aliases) writer.WriteStringValue(alias);
                writer.WriteEndArray();
                writer.WriteString("selectedModel", selection.Model.ToString());
                writer.WriteString("routeId", selection.RouteId.Value);
                writer.WriteNumber("contextBudget", selection.ContextBudget);
                writer.WriteString("toolMode", selection.ToolMode.ToString());
                writer.WriteString("reasoningKind", selection.Reasoning?.Kind);
                if (selection.Reasoning?.BudgetTokens is { } tokens) writer.WriteNumber("reasoningBudgetTokens", tokens);
                else writer.WriteNull("reasoningBudgetTokens");
            }),
            Component("model.profile", writer =>
            {
                writer.WriteString("modelId", profile.ModelId);
                writer.WriteString("routeId", profile.RouteId.Value);
                writer.WriteNumber("contextWindow", profile.ContextWindow);
                writer.WriteNumber("recommendedUsableContext", profile.RecommendedUsableContext);
                writer.WriteNumber("maxOutputTokens", profile.MaxOutputTokens);
                writer.WriteStartArray("inputModalities");
                foreach (var modality in profile.InputModalities) writer.WriteStringValue(modality);
                writer.WriteEndArray();
                writer.WriteStartArray("toolCallFormats");
                foreach (var format in profile.ToolCallFormats) writer.WriteStringValue(format.ToString());
                writer.WriteEndArray();
                writer.WriteBoolean("supportsParallelTools", profile.SupportsParallelTools);
                writer.WriteStartObject("traits");
                foreach (var pair in profile.Traits.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    writer.WriteNumber(pair.Key, pair.Value);
                writer.WriteEndObject();
            }),
            Component("model.harness", writer =>
            {
                writer.WriteString("toolCallFormat", harness.ToolCallFormat.ToString());
                writer.WriteString("toolMode", harness.ToolMode.ToString());
                writer.WriteNumber("maxVisibleTools", harness.MaxVisibleTools);
                writer.WriteString("guidanceLevel", harness.GuidanceLevel.ToString());
                writer.WriteNumber("repairAttempts", harness.RepairAttempts);
                writer.WriteString("planControl", harness.PlanControl.ToString());
                writer.WriteNumber("stallThresholdTurns", harness.StallThresholdTurns);
                WriteContextPolicy(writer, harness.ContextManagement);
            }),
            Component("context.policy", writer =>
            {
                WriteContextPolicy(writer, harness.ContextManagement);
                writer.WriteNumber("contextBudget", selection.ContextBudget);
                writer.WriteString("tokenizerIdentity", tokenizerIdentity);
            })
        };
        // The endpoint may contain private configuration. Only its digest enters the journal.
        if (selection.Route is { } route)
            components.Add(HashComponent("provider.adapter", route.CanonicalJson()));
        return new ExecutionFingerprint(model.Id, harnessHash, "core-tools-1", contextPolicyHash,
            "none", RuntimeBuildIdentity.ForAssembly(typeof(OmniCliRuntime).Assembly), modelPolicyHash,
            tokenizerIdentity, components);
    }

    private static void WriteContextPolicy(Utf8JsonWriter writer, ContextManagementPolicy policy)
    {
        writer.WriteNumber("externalizeAboveCharacters", policy.ExternalizeAboveCharacters);
        writer.WriteNumber("compressBodyCharacters", policy.CompressBodyCharacters);
        writer.WriteNumber("recentTailItems", policy.RecentTailItems);
        writer.WriteNumber("compactAfterItems", policy.CompactAfterItems);
        writer.WriteNumber("maxCheckpointCharacters", policy.MaxCheckpointCharacters);
    }

    private static FingerprintComponent Component(string name, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return HashComponent(name, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static FingerprintComponent HashComponent(string name, string value) => new(name, "1",
        ContentHash.Sha256(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))));
}
