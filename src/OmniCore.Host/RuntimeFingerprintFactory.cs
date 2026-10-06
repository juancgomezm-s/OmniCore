namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Tools;

/// <summary>Fingerprints resolved runtime facts, not phase names or guessed configuration.</summary>
internal static class RuntimeFingerprintFactory
{
    internal static ExecutionFingerprint Create(ModelDefinition model, EffectiveModelProfile profile,
        HarnessPolicy harness, ModelSelection selection, string harnessHash, string contextPolicyHash,
        string modelPolicyHash, string tokenizerIdentity, IModelProvider? provider = null)
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
                if (selection.MaxOutputTokens is { } outputLimit) writer.WriteNumber("requestedMaxOutputTokens", outputLimit);
                else writer.WriteNull("requestedMaxOutputTokens");
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
            components.Add(ProviderAdapterComponent(route, provider));
        return new ExecutionFingerprint(model.Id, harnessHash, "core-tools-1", contextPolicyHash,
            "none", RuntimeBuildIdentity.ForAssembly(typeof(OmniCliRuntime).Assembly), modelPolicyHash,
            tokenizerIdentity, components);
    }

    internal static ExecutionFingerprint WithTurnConfiguration(ExecutionFingerprint baseline,
        FakeCatalog catalog, IReadOnlyList<ToolDefinition> visibleTools, string systemPrompt, Plan? plan)
    {
        var tools = Component("tools.plan", writer =>
        {
            writer.WriteStartArray("tools");
            foreach (var visible in visibleTools)
            {
                var descriptor = catalog.Find(new ToolId(visible.Name))?.Descriptor
                    ?? throw new InvalidOperationException("Visible tool has no canonical descriptor.");
                writer.WriteStartObject();
                writer.WriteString("visibleName", visible.Name);
                writer.WriteString("toolId", descriptor.Id.ToString());
                writer.WriteString("description", visible.Description);
                writer.WriteString("schemaHash", Digest(visible.InputSchemaJson));
                writer.WriteString("sourceKind", descriptor.Source.Kind.ToString());
                writer.WriteString("sourceScope", descriptor.Source.Scope.ToString());
                writer.WriteString("sourceTrust", descriptor.Source.Trust.ToString());
                writer.WriteString("sourceOwner", descriptor.Source.Owner);
                writer.WriteString("sourceVersion", descriptor.Source.Version);
                writer.WriteBoolean("readOnly", descriptor.ReadOnly);
                writer.WriteBoolean("destructive", descriptor.Destructive);
                writer.WriteString("risk", descriptor.Risk.ToString());
                writer.WriteString("protection", descriptor.Protection.ToString());
                writer.WriteString("effectClass", descriptor.EffectClass.ToString());
                writer.WriteStartArray("tags");
                foreach (var tag in descriptor.Tags) writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        var prompt = Component("prompt.template", writer =>
        {
            writer.WriteString("templateId", "ExplorerTurn.SystemPrompt");
            writer.WriteString("renderedHash", Digest(systemPrompt));
        });
        var revision = Component("plan.revision", writer =>
        {
            writer.WriteString("planId", plan?.Id.ToString());
            if (plan is null) writer.WriteNull("revision");
            else writer.WriteNumber("revision", plan.Revision);
        });
        var names = new HashSet<string>(new[] { tools.Name, prompt.Name, revision.Name }, StringComparer.Ordinal);
        var components = baseline.Components.Where(component => !names.Contains(component.Name))
            .Concat(new[] { tools, prompt, revision }).ToArray();
        return new ExecutionFingerprint(baseline.ModelKey, baseline.HarnessPolicyHash, tools.Hash.Value,
            baseline.ContextPolicyHash, baseline.OverridesHash, baseline.Build, baseline.ModelPolicyHash,
            baseline.TokenizerHash, components);
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

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FingerprintComponent ProviderAdapterComponent(ModelRoute route, IModelProvider? provider)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("route");
            using (var routeDocument = JsonDocument.Parse(route.CanonicalJson()))
                routeDocument.RootElement.WriteTo(writer);
            if (provider is null)
            {
                writer.WriteNull("providerType");
                writer.WriteNull("providerBuild");
            }
            else
            {
                writer.WriteString("providerType", provider.GetType().FullName);
                writer.WriteString("providerBuild", RuntimeBuildIdentity.ForAssembly(provider.GetType().Assembly));
            }
            writer.WriteEndObject();
        }

        return HashComponent("provider.adapter", "2", Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static FingerprintComponent HashComponent(string name, string value) => HashComponent(name, "1", value);

    private static FingerprintComponent HashComponent(string name, string version, string value) => new(name, version,
        ContentHash.Sha256(Digest(value)));
}
