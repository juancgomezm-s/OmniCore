namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Tools;
using OmniCore.Infrastructure;
using System.Text.Json;

public sealed class RuntimeTurnFingerprintFactoryTests
{
    private static readonly ExecutionFingerprint Baseline =
        new("model", "harness", "legacy-toolkit", "context", "overrides", "build");

    private static FingerprintComponent Component(ExecutionFingerprint fingerprint, string name) =>
        Assert.Single(fingerprint.Components, component => component.Name == name);

    private static ExecutionFingerprint Apply(FakeCatalog catalog, string prompt = "system prompt", Plan? plan = null,
        IReadOnlyList<ToolDefinition>? visibleTools = null) =>
        RuntimeFingerprintFactory.WithTurnConfiguration(Baseline, catalog,
            visibleTools ?? catalog.Definitions(), prompt, plan);

    [Fact]
    public void Resolved_skills_are_canonical_and_empty_differs_from_unavailable()
    {
        var catalog = new FakeCatalog();
        var a = new ActiveSkillFingerprint("a", "1", ContentHash.Sha256(new string('a', 64)));
        var b = new ActiveSkillFingerprint("b", "2", ContentHash.Sha256(new string('b', 64)));
        ExecutionFingerprint With(IReadOnlyList<ActiveSkillFingerprint>? skills) =>
            RuntimeFingerprintFactory.WithTurnConfiguration(Baseline, catalog, catalog.Definitions(),
                "system", null, activeSkills: skills);
        Assert.NotEqual(Component(With(null), "skills.active").Hash, Component(With([]), "skills.active").Hash);
        Assert.Equal(With([a, b]).Hash(), With([b, a]).Hash());
        Assert.NotEqual(With([a]).Hash(), With([a with { Version = "2" }]).Hash());
        Assert.NotEqual(With([a]).Hash(), With([a with { ContentHash = b.ContentHash }]).Hash());
        Assert.Throws<ArgumentException>(() => With([a, a]));
        Assert.Throws<ArgumentException>(() => With([a with { Id = "" }]));
        var replaced = RuntimeFingerprintFactory.WithTurnConfiguration(With([a]), catalog,
            catalog.Definitions(), "system", null);
        Assert.Equal(With(null).Hash(), replaced.Hash()); // No stale skills inherited from baseline.
    }

    [Fact]
    public void Unprovided_skills_are_explicitly_unavailable_not_inferred_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-skills-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var fingerprint = RuntimeFingerprintFactory.WithTurnConfiguration(Baseline, catalog,
                catalog.Definitions(), "system", null, artifacts);
            var skills = Component(fingerprint, "skills.active");
            Assert.NotNull(skills.Content);
            using var json = JsonDocument.Parse(artifacts.GetText(skills.Content.Hash)!);
            Assert.Equal("unavailable", json.RootElement.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("skills").ValueKind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_profile_is_explicitly_unavailable_and_never_inferred_from_baseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-missing-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var known = RuntimeFingerprintFactory.WithTurnConfiguration(Baseline, catalog, catalog.Definitions(),
                "system prompt", null, store, ProfileId.New());
            var unknown = RuntimeFingerprintFactory.WithTurnConfiguration(known, catalog, catalog.Definitions(),
                "system prompt", null, store);
            var component = Component(unknown, "agent.profile");
            Assert.NotEqual(Component(known, "agent.profile").Hash, component.Hash);
            Assert.NotNull(component.Content);
            using var json = JsonDocument.Parse(store.GetText(component.Content.Hash)!);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("profileId").ValueKind);
            Assert.Equal("unavailable", json.RootElement.GetProperty("source").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Hidden_catalog_tool_does_not_change_visible_tools_fingerprint()
    {
        var visibleOnly = new FakeCatalog().Add(FakeTool.Read("visible.read"));
        var withHidden = new FakeCatalog().Add(FakeTool.Read("visible.read"))
            .Add(FakeTool.Write("hidden.write"));

        var first = Apply(visibleOnly);
        var second = Apply(withHidden, visibleTools: visibleOnly.Definitions());

        Assert.Equal(Component(first, "tools.plan").Hash, Component(second, "tools.plan").Hash);
        Assert.Equal(first.ToolkitHash, second.ToolkitHash);
    }

    [Fact]
    public void Visible_schema_change_changes_only_tools_plan_component()
    {
        var catalog = new FakeCatalog().Add(FakeTool.Read("visible.read"));
        var definition = Assert.Single(catalog.Definitions());
        var original = Apply(catalog, visibleTools: [definition]);
        var changed = Apply(catalog, visibleTools:
            [new ToolDefinition(definition.Name, definition.Description, "{\"type\":\"object\",\"required\":[\"path\"]}")]);

        Assert.NotEqual(Component(original, "tools.plan").Hash, Component(changed, "tools.plan").Hash);
        Assert.Equal(Component(original, "prompt.template").Hash, Component(changed, "prompt.template").Hash);
        Assert.Equal(Component(original, "plan.revision").Hash, Component(changed, "plan.revision").Hash);
    }

    [Fact]
    public void Source_version_change_changes_tools_plan_even_when_visible_name_and_schema_match()
    {
        var firstCatalog = new FakeCatalog().Add(new VersionedTool("fixture.inspect", "1"));
        var secondCatalog = new FakeCatalog().Add(new VersionedTool("fixture.inspect", "2"));

        var first = Apply(firstCatalog);
        var second = Apply(secondCatalog);

        Assert.Equal(Assert.Single(firstCatalog.Definitions()).Name, Assert.Single(secondCatalog.Definitions()).Name);
        Assert.Equal(Assert.Single(firstCatalog.Definitions()).InputSchemaJson,
            Assert.Single(secondCatalog.Definitions()).InputSchemaJson);
        Assert.NotEqual(Component(first, "tools.plan").Hash, Component(second, "tools.plan").Hash);
        Assert.Equal(Component(first, "prompt.template").Hash, Component(second, "prompt.template").Hash);
        Assert.Equal(Component(first, "plan.revision").Hash, Component(second, "plan.revision").Hash);
    }

    [Fact]
    public void Prompt_and_plan_revision_change_only_their_own_components()
    {
        var catalog = new FakeCatalog().Add(FakeTool.Read("visible.read"));
        var planId = PlanId.New();
        var runId = RunId.New();
        var plan1 = new Plan(planId, runId, 1, Array.Empty<PlanItem>());
        var plan2 = new Plan(planId, runId, 2, Array.Empty<PlanItem>());

        var baseline = Apply(catalog, "system one", plan1);
        var promptChanged = Apply(catalog, "system two", plan1);
        var planChanged = Apply(catalog, "system one", plan2);

        Assert.NotEqual(Component(baseline, "prompt.template").Hash,
            Component(promptChanged, "prompt.template").Hash);
        Assert.Equal(Component(baseline, "tools.plan").Hash, Component(promptChanged, "tools.plan").Hash);
        Assert.Equal(Component(baseline, "plan.revision").Hash, Component(promptChanged, "plan.revision").Hash);

        Assert.NotEqual(Component(baseline, "plan.revision").Hash, Component(planChanged, "plan.revision").Hash);
        Assert.Equal(Component(baseline, "tools.plan").Hash, Component(planChanged, "tools.plan").Hash);
        Assert.Equal(Component(baseline, "prompt.template").Hash, Component(planChanged, "prompt.template").Hash);
    }

    [Fact]
    public void Applying_turn_configuration_is_idempotent_and_does_not_mutate_baseline()
    {
        var catalog = new FakeCatalog().Add(FakeTool.Read("visible.read"));
        var originalComponents = Baseline.Components.ToArray();

        var once = Apply(catalog);
        var twice = RuntimeFingerprintFactory.WithTurnConfiguration(once, catalog,
            catalog.Definitions(), "system prompt", null);

        Assert.Equal(5, once.Components.Count);
        Assert.Equal(5, twice.Components.Count);
        Assert.Equal(once.Hash(), twice.Hash());
        Assert.Empty(Baseline.Components);
        Assert.Equal(originalComponents, Baseline.Components);
    }

    // Metadata-only fixture: the fingerprint reads Descriptor.Source.Version; no tool operation runs.
    private sealed class VersionedTool : ITool
    {
        public ToolDescriptor Descriptor { get; }

        public VersionedTool(string id, string version) => Descriptor = new ToolDescriptor(new ToolId(id),
            "fixture descriptor", new InputSchema("{}"), Array.Empty<string>(), true, false, ToolRisk.Low,
            new ComponentSource(SourceKind.BuiltIn, ScopeLevel.BuiltIn, TrustLevel.Core, "fixture", version),
            ToolProtection.None, EffectClass.None);

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            throw new InvalidOperationException("Fingerprint tests must not prepare tools.");

        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromException<ToolResult>(
                new InvalidOperationException("Fingerprint tests must not execute tools."));
    }
}
