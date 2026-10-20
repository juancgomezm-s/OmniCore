using OmniCore.Host;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ModelPolicyProfileControlsTests
{
    [Fact]
    public void Compiled_workflow_stage_tools_stay_visible_within_the_existing_cap()
    {
        var definitions = new[]
        {
            "filesystem.read", "filesystem.list", "search.text", "reference.resolve",
            "filesystem.write", "filesystem.patch", "dyn.core.verify_integration", "process.exec",
        }.Select(name => new ToolDefinition(name, name, "{}" )).ToArray();
        var capabilities = ModelCapabilityBoundary.CoreTools.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        capabilities["dyn.core.verify_integration"] = ModelToolCapability.ValidationProcess;
        var full = ModelPolicyPresets.FullAgent();
        var fullPolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "workflow-full"), 1,
            full.Category, new ModelToolPolicy(ToolMode.Direct, 6, false, full.ToolPolicy.CapabilityCeiling),
            full.MutationPolicy, false);
        var fullBoundary = new ModelCapabilityBoundary(fullPolicy, capabilities);
        var implement = CompiledWorkflowCatalog.Stage(WorkflowStage.Implement);
        var implementVisible = fullBoundary.VisibleTools(definitions,
            explicitPriority: new[] { "filesystem.read" }.Concat(implement.RequiredSuccessfulToolIds).ToArray())
            .Select(tool => tool.Name).ToArray();

        Assert.False(implement.RequiresEveryTool);
        Assert.Equal(6, implementVisible.Length);
        Assert.Contains("filesystem.read", implementVisible);
        Assert.All(implement.RequiredSuccessfulToolIds, required => Assert.Contains(required, implementVisible));
        Assert.Equal(6, fullBoundary.MaxVisibleTools());

        var verify = CompiledWorkflowCatalog.Stage(WorkflowStage.Verify);
        var verifyVisible = fullBoundary.VisibleTools(definitions,
            explicitPriority: verify.RequiredSuccessfulToolIds).Select(tool => tool.Name).ToArray();
        Assert.Equal(6, verifyVisible.Length);
        Assert.Contains("dyn.core.verify_integration", verifyVisible);

        var patchOnly = ModelPolicyPresets.PatchOnly();
        var patchPolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "workflow-patch-only"), 1,
            patchOnly.Category, new ModelToolPolicy(ToolMode.Direct, 6, false,
                patchOnly.ToolPolicy.CapabilityCeiling), patchOnly.MutationPolicy, false);
        var patchBoundary = new ModelCapabilityBoundary(patchPolicy, capabilities);
        var eligibleChoices = implement.RequiredSuccessfulToolIds.Where(patchBoundary.IsToolVisible).ToArray();
        var patchVisible = patchBoundary.VisibleTools(definitions,
            explicitPriority: new[] { "filesystem.read" }.Concat(implement.RequiredSuccessfulToolIds).ToArray())
            .Select(tool => tool.Name).ToArray();

        Assert.Equal(new[] { "filesystem.write", "filesystem.patch" }, eligibleChoices);
        Assert.Contains("filesystem.read", patchVisible);
        Assert.Contains("filesystem.patch", patchVisible);
        Assert.Contains("filesystem.write", patchVisible);
        Assert.True(patchVisible.Length <= patchBoundary.MaxVisibleTools());
        Assert.Equal(6, patchBoundary.MaxVisibleTools());
    }

    [Fact]
    public void Explicit_full_access_exposes_the_entire_eligible_catalog_despite_unknown_traits()
    {
        var key = ModelPolicyKey.For("chatgpt", "gpt-6-luna");
        var profile = new OmniCore.Host.ModelProfileResolver().Resolve(
            new OmniCore.Models.ModelDefinition(key.ModelId, key.ProviderId, 8192, 8192, 2048), null);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        Assert.Equal(6, harness.MaxVisibleTools); // Provisional metadata, not a qualification failure.
        var stored = new StoredModelPolicy(key, 1, ModelPolicyPresets.FullAgent(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var effective = EffectiveModelPolicy.Resolve(key, stored, harness);
        var boundary = new ModelCapabilityBoundary(effective);
        var definitions = OmniHost.CreateActTools().Catalog().Definitions();
        var eligible = definitions.Where(tool => boundary.IsToolVisible(tool.Name)).Select(tool => tool.Name).Order().ToArray();
        Assert.True(eligible.Length > 6);
        Assert.Equal(eligible, boundary.VisibleTools(definitions).Select(tool => tool.Name).Order().ToArray());
        Assert.True(boundary.UsesFullToolCatalog());
        Assert.Equal(0.5, profile.Trait("ToolCallReliability", 0)); // No fabricated empirical evidence.
    }

    [Fact]
    public void Full_access_keeps_explicitly_narrowed_capabilities_and_tool_limit()
    {
        var preset = ModelPolicyPresets.FullAgent();
        var policy = new UserModelPolicy(ModelPolicyCategory.FullAgent,
            new ModelToolPolicy(ToolMode.Discovered, 2, true,
                new HashSet<ModelToolCapability> { ModelToolCapability.WorkspaceRead }),
            preset.MutationPolicy, "user", null);
        var key = ModelPolicyKey.For("fixture", "frontier");
        var effective = EffectiveModelPolicy.Resolve(key, new StoredModelPolicy(key, 1, policy,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), new HarnessPolicy(
                ToolCallFormat.Native, ToolMode.Direct, 6, GuidanceLevel.Full, 1, PlanControl.ModelDriven, 4));
        var boundary = new ModelCapabilityBoundary(effective);
        Assert.Equal(2, boundary.VisibleTools(OmniHost.CreateActTools().Catalog().Definitions()).Count);
        Assert.False(boundary.IsToolVisible("shell.exec"));
        Assert.False(boundary.IsToolVisible("filesystem.patch"));
    }

    [Fact]
    public void Adding_a_policy_note_preserves_all_explicit_full_agent_controls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-full-note-" + Guid.NewGuid().ToString("N"));
        using var host = ModelPolicyHost.Create(directory,
            [new ModelRegistryModelDescriptor("luna", "fixture", 8192, 8192, 2048)]);
        var saved = host.Set(new("fixture", "luna"), 0, "FullAgent", "User authorization", TestContext.Current.CancellationToken);
        Assert.True(saved.AllowOrchestration);
        Assert.Equal(600, saved.MaxGenerationSeconds);
        Assert.Equal(int.MaxValue, saved.MaxVisibleTools);
    }

    [Theory]
    [InlineData(ModelPolicyCategory.ScopedCoder, false)]
    [InlineData(ModelPolicyCategory.ScopedCoder, true)]
    [InlineData(ModelPolicyCategory.FullAgent, false)]
    [InlineData(ModelPolicyCategory.FullAgent, true)]
    public void Six_tool_direct_harness_keeps_authorized_file_creation_and_patch_visible(
        ModelPolicyCategory category, bool includeMedia)
    {
        var preset = ModelPolicyPresets.For(category);
        var key = ModelPolicyKey.For("fixture", "small-coder");
        var stored = new StoredModelPolicy(key, 1, preset, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 6,
            GuidanceLevel.DomainOnly, 1, PlanControl.RuntimeDriven, 4);
        var effective = EffectiveModelPolicy.Resolve(key, stored, harness);
        var tools = OmniHost.CreateActTools(mediaReadTool: includeMedia
            ? new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator()) : null).Catalog();
        // ExplorerTurn excludes tool.search in Direct mode: hidden tools cannot be discovered.
        var visible = new ModelCapabilityBoundary(effective).VisibleTools(
            tools.Definitions().Where(tool => tool.Name != "tool.search")).Select(tool => tool.Name).ToArray();
        var basicTools = new[] { "user.ask", "filesystem.list", "filesystem.read", "search.text",
            "filesystem.patch", "filesystem.write" };
        if (category == ModelPolicyCategory.FullAgent)
        {
            Assert.All(basicTools, tool => Assert.Contains(tool, visible));
            Assert.Contains("process.exec", visible);
            Assert.Contains("shell.exec", visible);
            Assert.Equal(int.MaxValue, effective.ToolPolicy.MaxVisibleTools);
            Assert.True(effective.ToolPolicy.AllowToolDiscovery);
        }
        else
        {
            Assert.Equal(basicTools, visible);
            Assert.Equal(6, effective.ToolPolicy.MaxVisibleTools);
            Assert.False(effective.ToolPolicy.AllowToolDiscovery);
        }
        Assert.Equal(preset.MutationPolicy, effective.MutationPolicy);
    }

    [Fact]
    public void Preset_catalogs_keep_basic_read_search_and_user_ask_before_optional_tools()
    {
        var temp = Path.Combine(Path.GetTempPath(), "omnicore-profile-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actTools = OmniHost.CreateActTools(artifactReadTool: new ArtifactReadTool(
                new FileArtifactStore(temp), _ => false));
            var definitions = actTools.Catalog().Definitions();
            var patch = ModelPolicyPresets.PatchOnly();
            var patchPolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "small-model"), 1,
                patch.Category, patch.ToolPolicy, patch.MutationPolicy, false);
            var patchVisible = new ModelCapabilityBoundary(patchPolicy).VisibleTools(definitions)
                .Select(tool => tool.Name).ToArray();

            Assert.Contains("user.ask", patchVisible);
            Assert.Contains("search.text", patchVisible);
            Assert.Contains("filesystem.read", patchVisible);
            Assert.Contains("filesystem.patch", patchVisible);
            Assert.DoesNotContain("filesystem.search", patchVisible);

            var observe = ModelPolicyPresets.ObserveOnly();
            var mediaTools = OmniHost.CreateExplorerTools(mediaReadTool: new MediaReadTool(
                _ => false, _ => null, new PathBoundaryValidator()));
            var mediaDefinitions = mediaTools.Catalog().Definitions();
            var observeBoundary = new ModelCapabilityBoundary(new EffectiveModelPolicy(
                ModelPolicyKey.For("fixture", "media-observer"), 1, observe.Category,
                observe.ToolPolicy, observe.MutationPolicy, false));
            var visible = observeBoundary.VisibleTools(mediaDefinitions).Select(tool => tool.Name).ToArray();
            Assert.Equal(ModelToolCapability.WorkspaceRead, ModelCapabilityBoundary.CoreTools["media.read"]);
            Assert.Contains("media.read", visible);
            var mediaIntent = new ToolIntent(ToolCallId.New(), new ToolId("media.read"),
                "{\"path\":\"image.png\"}", EffectClass.None,
                new ResourceClaims(["image.png"], [], [], null, []), ToolRisk.Low, null);
            Assert.True(observeBoundary.Evaluate(mediaIntent).Allowed);

            var deniedReadPolicy = new ModelToolPolicy(ToolMode.Direct, 6, false,
                new HashSet<ModelToolCapability> { ModelToolCapability.Search });
            var deniedBoundary = new ModelCapabilityBoundary(new EffectiveModelPolicy(
                ModelPolicyKey.For("fixture", "media-no-read"), 1, ModelPolicyCategory.Custom,
                deniedReadPolicy, observe.MutationPolicy, false));
            Assert.False(deniedBoundary.IsToolVisible("media.read"));
            Assert.False(deniedBoundary.Evaluate(mediaIntent).Allowed);

            var observePolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "observer"), 1,
                observe.Category, observe.ToolPolicy, observe.MutationPolicy, false);
            var observeVisible = new ModelCapabilityBoundary(observePolicy).VisibleTools(definitions)
                .Select(tool => tool.Name).ToArray();
            Assert.Contains("user.ask", observeVisible);
            Assert.Contains("search.text", observeVisible);
            Assert.Contains("filesystem.read", observeVisible);
            Assert.DoesNotContain("mode.propose", observeVisible);
            Assert.DoesNotContain("plan.propose", observeVisible);
            Assert.DoesNotContain("filesystem.patch", observeVisible);

            // artifact.read is now correctly classified as WorkspaceRead, but sits after the
            // baseline set so narrow profiles do not lose search, references, or patching.
            Assert.Equal(ModelToolCapability.WorkspaceRead,
                ModelCapabilityBoundary.CoreTools["artifact.read"]);
            var full = ModelPolicyPresets.FullAgent();
            var fullPolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "full"), 1,
                full.Category, full.ToolPolicy, full.MutationPolicy, false);
            Assert.Contains("artifact.read", new ModelCapabilityBoundary(fullPolicy)
                .VisibleTools(definitions).Select(tool => tool.Name));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Profile_controls_are_exactly_persisted_and_cannot_widen_the_category_ceiling()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-profile-controls", Guid.NewGuid().ToString("N"));
        var key = new ModelPolicyKeyDto("fixture", "model-a");
        using var host = ModelPolicyHost.Create(directory,
            [new ModelRegistryModelDescriptor("model-a", "fixture", 32_000, 24_000, 4_000)]);
        var created = host.Set(key, 0, "PatchOnly", null, CancellationToken.None);

        var updated = host.SetProfileControls(key, created.Revision, 2, 5, 4,
            ["WorkspaceRead", "Search", "PatchExisting"], "PatchExisting", 1, 80, 0.1,
            "Deny", "Deny", true, true, true, false, 120, CancellationToken.None);

        Assert.Equal(2, updated.MaxModelStepsPerTurn);
        Assert.Equal(5, updated.MaxToolCallsPerTurn);
        Assert.Equal(4, updated.MaxVisibleTools);
        Assert.Equal(1, updated.MaxFilesPerTurn);
        Assert.Equal(80, updated.MaxChangedLinesPerTurn);
        Assert.Equal(0.1, updated.MaxRewriteRatio);
        Assert.False(updated.AllowOrchestration);
        Assert.Equal(120, updated.MaxGenerationSeconds);
        Assert.Equal(new[] { "WorkspaceRead", "Search", "PatchExisting" }, updated.CapabilityCeiling);
        var restored = host.Get(key, CancellationToken.None)!;
        Assert.Equal(updated.MaxModelStepsPerTurn, restored.MaxModelStepsPerTurn);
        Assert.Equal(updated.MaxToolCallsPerTurn, restored.MaxToolCallsPerTurn);
        Assert.Equal(updated.AllowOrchestration, restored.AllowOrchestration);
        Assert.Equal(updated.MaxGenerationSeconds, restored.MaxGenerationSeconds);
        Assert.Throws<ArgumentException>(() => host.SetProfileControls(key, updated.Revision, 2, 5, 4,
            ["ReplaceFile"], "PatchExisting", 1, 80, 0.1, "Deny", "Deny", true, true, true,
            false, 120, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => host.SetProfileControls(key, updated.Revision, 2, 5, 4,
            ["WorkspaceRead"], "PatchExisting", 1, 80, 0.1, "Deny", "Deny", true, true, true,
            true, 120, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => host.SetProfileControls(key, updated.Revision, 3, 5, 4,
            ["WorkspaceRead"], "PatchExisting", 1, 80, 0.1, "Deny", "Deny", true, true, true,
            false, 120, CancellationToken.None));
    }
}
