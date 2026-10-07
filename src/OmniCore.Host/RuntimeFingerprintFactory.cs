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
    internal sealed record PreparedRuntimeFingerprint(ExecutionFingerprint Fingerprint,
        IReadOnlyList<IPreparedArtifact> Artifacts);

    internal static PreparedRuntimeFingerprint Prepare(ModelDefinition model, EffectiveModelProfile profile,
        HarnessPolicy harness, ModelSelection selection, string harnessHash, string contextPolicyHash,
        string modelPolicyHash, string tokenizerIdentity, IModelProvider? provider = null,
        ModelQualificationSnapshot? qualification = null, IArtifactStore? artifacts = null)
    {
        var pending = new List<IPreparedArtifact>();
        var fingerprint = Create(model, profile, harness, selection, harnessHash, contextPolicyHash,
            modelPolicyHash, tokenizerIdentity, provider, qualification, artifacts, pending);
        return new PreparedRuntimeFingerprint(fingerprint, pending.AsReadOnly());
    }

    internal static PreparedRuntimeFingerprint PrepareTurnConfiguration(ExecutionFingerprint baseline,
        FakeCatalog catalog, IReadOnlyList<ToolDefinition> visibleTools, string systemPrompt, Plan? plan,
        IArtifactStore? artifacts = null, ProfileId? agentProfile = null,
        IReadOnlyList<ActiveSkillFingerprint>? activeSkills = null, AgentProfile? resolvedAgentProfile = null,
        RunModeAuthority? modeAuthority = null, TurnInstructionSnapshot? instructionSnapshot = null,
        ReasoningResolution? reasoningResolution = null)
    {
        var pending = new List<IPreparedArtifact>();
        var fingerprint = WithTurnConfiguration(baseline, catalog, visibleTools, systemPrompt, plan,
            artifacts, agentProfile, activeSkills, pending, resolvedAgentProfile, modeAuthority, instructionSnapshot,
            reasoningResolution);
        return new PreparedRuntimeFingerprint(fingerprint, pending.AsReadOnly());
    }

    internal static ExecutionFingerprint Create(ModelDefinition model, EffectiveModelProfile profile,
        HarnessPolicy harness, ModelSelection selection, string harnessHash, string contextPolicyHash,
        string modelPolicyHash, string tokenizerIdentity, IModelProvider? provider = null,
        ModelQualificationSnapshot? qualification = null, IArtifactStore? artifacts = null,
        ICollection<IPreparedArtifact>? pending = null)
    {
        FingerprintComponent ResolvedComponent(string name, Action<Utf8JsonWriter> write, string version = "1") =>
            Component(name, write, version, artifacts, pending);
        var components = new List<FingerprintComponent>
        {
            HashComponent(RuntimeBuildIdentity.FingerprintComponentName, RuntimeBuildIdentity.FingerprintComponentVersion,
                RuntimeBuildIdentity.CanonicalJsonFor(typeof(OmniCliRuntime).Assembly), artifacts, pending),
            ResolvedComponent("model.descriptor", writer =>
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
            ResolvedComponent("model.profile", writer =>
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
                if (qualification is null)
                {
                    writer.WriteNull("qualificationKeyHash");
                    writer.WriteNull("qualificationRevision");
                    writer.WriteNull("qualificationState");
                }
                else
                {
                    writer.WriteString("qualificationKeyHash", qualification.KeyHash);
                    writer.WriteNumber("qualificationRevision", qualification.ProfileRevision);
                    writer.WriteString("qualificationState", qualification.State.ToString());
                }
            }, "2"),
            ResolvedComponent("model.harness", writer =>
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
            ResolvedComponent("context.policy", writer =>
            {
                WriteContextPolicy(writer, harness.ContextManagement);
                writer.WriteNumber("contextBudget", selection.ContextBudget);
                writer.WriteString("tokenizerIdentity", tokenizerIdentity);
            })
        };
        var reasoning = profile.ReasoningCapability;
        if (reasoning.Supported is not null || reasoning.EffortLevels is not null || reasoning.ReplayPolicy is not null)
            components.Add(ResolvedComponent("model.reasoning.declared", writer =>
            {
                writer.WriteString("routeId", profile.RouteId.Value);
                if (reasoning.Supported is { } supported) writer.WriteBoolean("supported", supported);
                else writer.WriteNull("supported");
                if (reasoning.EffortLevels is null) writer.WriteNull("effortLevels");
                else
                {
                    writer.WriteStartArray("effortLevels");
                    foreach (var level in reasoning.EffortLevels) writer.WriteStringValue(level);
                    writer.WriteEndArray();
                }
                writer.WriteString("replayPolicy", reasoning.ReplayPolicy?.ToString());
                if (reasoning.UltraCodeBudgetTokens is not null || reasoning.UltraCodeOutputReserveTokens is not null)
                {
                    if (reasoning.UltraCodeBudgetTokens is { } budget) writer.WriteNumber("ultraCodeBudgetTokens", budget);
                    else writer.WriteNull("ultraCodeBudgetTokens");
                    if (reasoning.UltraCodeOutputReserveTokens is { } reserve) writer.WriteNumber("ultraCodeOutputReserveTokens", reserve);
                    else writer.WriteNull("ultraCodeOutputReserveTokens");
                }
            }, reasoning.UltraCodeBudgetTokens is not null || reasoning.UltraCodeOutputReserveTokens is not null ? "2" : "1"));
        // Endpoint configuration follows the same sensitive/redacted content path as other
        // components. A changed representation never claims to explain the original digest.
        if (selection.Route is { } route)
            components.Add(ProviderAdapterComponent(route, provider, artifacts, pending));
        if (selection.ReasoningResolution is { } resolution)
        {
            resolution.Validate();
            components.Add(ResolvedComponent("model.reasoning.resolved", writer =>
            {
                writer.WritePropertyName("resolution");
                WriteReasoningResolution(writer, resolution);
            }));
        }
        return new ExecutionFingerprint(model.Id, harnessHash, "core-tools-1", contextPolicyHash,
            "none", RuntimeBuildIdentity.ForAssembly(typeof(OmniCliRuntime).Assembly), modelPolicyHash,
            tokenizerIdentity, components);
    }

    internal static ExecutionFingerprint WithTurnConfiguration(ExecutionFingerprint baseline,
        FakeCatalog catalog, IReadOnlyList<ToolDefinition> visibleTools, string systemPrompt, Plan? plan,
        IArtifactStore? artifacts = null, ProfileId? agentProfile = null,
        IReadOnlyList<ActiveSkillFingerprint>? activeSkills = null,
        ICollection<IPreparedArtifact>? pending = null, AgentProfile? resolvedAgentProfile = null,
        RunModeAuthority? modeAuthority = null, TurnInstructionSnapshot? instructionSnapshot = null,
        ReasoningResolution? reasoningResolution = null)
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
                writer.WriteString("inputSchemaJson", visible.InputSchemaJson);
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
        }, "2", artifacts, pending);
        var prompt = Component("prompt.template", writer =>
        {
            writer.WriteString("templateId", "ExplorerTurn.SystemPrompt");
            writer.WriteString("renderedHash", Digest(systemPrompt));
            writer.WriteString("renderedText", systemPrompt);
        }, "2", artifacts, pending);
        var revision = Component("plan.revision", writer =>
        {
            writer.WriteString("planId", plan?.Id.ToString());
            if (plan is null) writer.WriteNull("revision");
            else writer.WriteNumber("revision", plan.Revision);
        }, artifacts: artifacts, pending: pending);
        var profile = Component("agent.profile", writer =>
        {
            if (resolvedAgentProfile is not null)
            {
                if (agentProfile is not null && agentProfile != resolvedAgentProfile.Id)
                    throw new ArgumentException("Resolved agent profile does not match the Lane profile identity.", nameof(resolvedAgentProfile));
                AgentProfileFingerprint.Write(writer, resolvedAgentProfile);
                return;
            }
            writer.WriteString("profileId", agentProfile?.ToString());
            writer.WriteString("source", agentProfile is null ? "unavailable" : "lane.created");
        }, version: resolvedAgentProfile is null ? "1" : "2", artifacts: artifacts, pending: pending);
        var skills = Component("skills.active", writer =>
        {
            writer.WriteString("source", activeSkills is null ? "unavailable" : "provided");
            if (activeSkills is null) writer.WriteNull("skills");
            else
            {
                if (activeSkills.Any(skill => skill is null || string.IsNullOrWhiteSpace(skill.Id)
                    || string.IsNullOrWhiteSpace(skill.Version) || skill.ContentHash is null)
                    || activeSkills.Select(skill => skill.Id).Distinct(StringComparer.Ordinal).Count() != activeSkills.Count)
                    throw new ArgumentException("Active skill identities must be complete and unique.", nameof(activeSkills));
                writer.WriteStartArray("skills");
                foreach (var skill in activeSkills.OrderBy(skill => skill.Id, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", skill.Id);
                    writer.WriteString("version", skill.Version);
                    writer.WriteString("contentHash", skill.ContentHash.ToString());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
        }, artifacts: artifacts, pending: pending);
        var resolved = new List<FingerprintComponent> { tools, prompt, revision, profile, skills };
        if (reasoningResolution is not null)
        {
            reasoningResolution.Validate();
            resolved.Add(Component("model.reasoning.resolved", writer =>
            {
                writer.WritePropertyName("resolution");
                WriteReasoningResolution(writer, reasoningResolution);
            }, artifacts: artifacts, pending: pending));
        }
        if (instructionSnapshot is not null)
        {
            instructionSnapshot.Validate();
            resolved.Add(Component("turn.instruction", writer =>
            {
                writer.WriteBoolean("conversationOnly", instructionSnapshot.ConversationOnly);
                writer.WriteString("resolvedInstruction", instructionSnapshot.ResolvedInstruction);
            }, artifacts: artifacts, pending: pending));
        }
        if (modeAuthority is not null)
        {
            modeAuthority.Validate();
            resolved.Add(Component("run.mode_authority", writer =>
            {
                writer.WritePropertyName("authority");
                ModeAuthorityFingerprint.Write(writer, modeAuthority);
            }, artifacts: artifacts, pending: pending));
        }
        // Absent authority must not inherit a previous Turn's authorization from its baseline.
        // The optional component leaves pre-ADR0047 journals' hashes unchanged.
        var names = new HashSet<string>(resolved.Select(component => component.Name), StringComparer.Ordinal)
            { "run.mode_authority", "turn.instruction", "model.reasoning.resolved" };
        var components = baseline.Components.Where(component => !names.Contains(component.Name))
            .Concat(resolved).ToArray();
        return new ExecutionFingerprint(baseline.ModelKey, baseline.HarnessPolicyHash, tools.Hash.Value,
            baseline.ContextPolicyHash, baseline.OverridesHash, baseline.Build, baseline.ModelPolicyHash,
            baseline.TokenizerHash, components);
    }

    private static void WriteReasoningResolution(Utf8JsonWriter writer, ReasoningResolution resolution)
    {
        writer.WriteStartObject();
        WriteRequest("requestedRequest", resolution.RequestedRequest);
        WriteRequest("appliedRequest", resolution.AppliedRequest);
        writer.WriteString("source", resolution.Source.ToString());
        WriteRevision("userPreferenceRevision", resolution.UserPreferenceRevision);
        WriteRevision("runPreferenceRevision", resolution.RunPreferenceRevision);
        WriteRevision("modeAuthorityRevision", resolution.ModeAuthorityRevision);
        if (resolution.TurnBoostId is { } boost) writer.WriteString("turnBoostId", boost);
        else writer.WriteNull("turnBoostId");
        if (resolution.OutputReserveTokens is { } reserve) writer.WriteNumber("outputReserveTokens", reserve);
        else writer.WriteNull("outputReserveTokens");
        writer.WriteStartArray("reductions");
        foreach (var reduction in resolution.Reductions) writer.WriteStringValue(reduction.ToString());
        writer.WriteEndArray();
        writer.WriteEndObject();

        void WriteRevision(string name, long? revision)
        {
            if (revision is { } value) writer.WriteNumber(name, value);
            else writer.WriteNull(name);
        }
        void WriteRequest(string name, ReasoningRequest? request)
        {
            if (request is null) { writer.WriteNull(name); return; }
            writer.WriteStartObject(name);
            writer.WriteString("kind", request.Kind);
            if (request.BudgetTokens is { } budget) writer.WriteNumber("budgetTokens", budget);
            else writer.WriteNull("budgetTokens");
            writer.WriteEndObject();
        }
    }

    private static void WriteContextPolicy(Utf8JsonWriter writer, ContextManagementPolicy policy)
    {
        writer.WriteNumber("externalizeAboveCharacters", policy.ExternalizeAboveCharacters);
        writer.WriteNumber("compressBodyCharacters", policy.CompressBodyCharacters);
        writer.WriteNumber("recentTailItems", policy.RecentTailItems);
        writer.WriteNumber("compactAfterItems", policy.CompactAfterItems);
        writer.WriteNumber("maxCheckpointCharacters", policy.MaxCheckpointCharacters);
    }

    private static FingerprintComponent Component(string name, Action<Utf8JsonWriter> write, string version = "1",
        IArtifactStore? artifacts = null, ICollection<IPreparedArtifact>? pending = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return HashComponent(name, version, Encoding.UTF8.GetString(stream.ToArray()), artifacts, pending);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FingerprintComponent ProviderAdapterComponent(ModelRoute route, IModelProvider? provider,
        IArtifactStore? artifacts, ICollection<IPreparedArtifact>? pending)
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

        return HashComponent("provider.adapter", "2", Encoding.UTF8.GetString(stream.ToArray()), artifacts, pending);
    }

    private static FingerprintComponent HashComponent(string name, string value) => HashComponent(name, "1", value);

    private static FingerprintComponent HashComponent(string name, string version, string value,
        IArtifactStore? artifacts = null, ICollection<IPreparedArtifact>? pending = null)
    {
        var hash = ContentHash.Sha256(Digest(value));
        ArtifactRef? content = null;
        if (artifacts is not null)
        {
            if (pending is not null && artifacts is IArtifactPreparationStore preparing)
            {
                var prepared = preparing.PrepareText(value, "application/vnd.omnicore.fingerprint-component+json",
                    ArtifactKind.Other, Sensitivity.Sensitive);
                if (!prepared.Reference.Redacted)
                {
                    if (prepared.Reference.Hash != hash)
                        throw new InvalidDataException("Prepared fingerprint component changed its canonical hash.");
                    pending.Add(prepared);
                    content = prepared.Reference;
                }
                return new FingerprintComponent(name, version, hash, content);
            }
            // ADR0018 still applies: never bypass the text store's redactor. A redacted
            // representation is not the exact configuration and must not masquerade as it.
            var stored = artifacts.PutText(value, "application/vnd.omnicore.fingerprint-component+json",
                ArtifactKind.Other, Sensitivity.Sensitive);
            if (!stored.Redacted)
            {
                if (stored.Hash != hash || !artifacts.Verify(stored.Hash, stored.Size))
                    throw new InvalidDataException("Fingerprint component artifact failed integrity verification.");
                content = stored;
            }
        }
        return new FingerprintComponent(name, version, hash, content);
    }
}
