namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;

/// <summary>Offline configuration/CAS fixtures, not provider qualification or live UI acceptance.</summary>
public sealed class ReusableAgentProfileTests
{
    private static readonly ProfileId Id = ProfileId.Parse("0199a000-0000-7000-8000-000000000001");

    private static AgentProfile Profile(long revision = 1, string[]? reads = null,
        IReadOnlyList<ProcessRule>? processes = null, IReadOnlyList<NetworkRule>? networks = null,
        string[]? writes = null, string[]? secrets = null, bool shell = false,
        IReadOnlyList<ToolId>? tools = null, string name = "explorer") => new(Id, name, revision,
            PermissionScope.With(reads ?? ["**"], writes ?? [], processes ?? [], networks ?? [], secrets ?? [], shell),
            tools ?? [new ToolId("filesystem.read"), new ToolId("filesystem.list")]);

    private static ExecutionFingerprint Apply(AgentProfile profile, IArtifactStore? store = null,
        ProfileId? laneProfile = null, ExecutionFingerprint? baseline = null)
    {
        var catalog = new FakeCatalog();
        return RuntimeFingerprintFactory.WithTurnConfiguration(
            baseline ?? new ExecutionFingerprint("model", "harness", "tools", "context", "none", "build"),
            catalog, catalog.Definitions(), "system", null, store, laneProfile ?? Id,
            resolvedAgentProfile: profile);
    }

    private static FingerprintComponent Component(ExecutionFingerprint fingerprint) =>
        Assert.Single(fingerprint.Components, value => value.Name == "agent.profile");

    [Fact]
    public void Same_reusable_configuration_is_stable_and_reapplication_replaces_not_accumulates()
    {
        var first = Apply(Profile());
        var second = Apply(Profile(), baseline: first);
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal("2", Component(first).Version);
        Assert.Single(second.Components, value => value.Name == "agent.profile");
        Assert.Equal(Profile().Id, new AgentProfileRegistry([Profile()]).Find(Id)!.Id);
        Assert.Null(new AgentProfileRegistry([Profile()]).Find(ProfileId.New()));
    }

    [Fact]
    public void Every_resolved_configuration_axis_changes_its_component_not_other_components()
    {
        var baseline = Apply(Profile());
        AgentProfile[] variants =
        [
            Profile(revision: 2), Profile(name: "reviewer"), Profile(reads: ["src/**"]),
            Profile(writes: ["src/**"]), Profile(secrets: ["explicit-secret-handle"]), Profile(shell: true),
            Profile(processes: [new ProcessRule("dotnet", ["test"], PermissionDecision.Ask)]),
            Profile(networks: [new NetworkRule("example.test", PermissionDecision.Deny)]),
            Profile(tools: [new ToolId("filesystem.list"), new ToolId("filesystem.read")]),
        ];
        foreach (var profile in variants)
        {
            var changed = Apply(profile);
            Assert.NotEqual(Component(baseline).Hash, Component(changed).Hash);
            foreach (var other in baseline.Components.Where(value => value.Name != "agent.profile"))
                Assert.Equal(other.Hash, Assert.Single(changed.Components, value => value.Name == other.Name).Hash);
        }
    }

    [Fact]
    public void Input_collections_and_nested_argv_cannot_mutate_profile_or_hash()
    {
        var reads = new[] { "**" };
        var argv = new[] { "test" };
        var process = new[] { new ProcessRule("dotnet", argv, PermissionDecision.Allow) };
        var tools = new[] { new ToolId("filesystem.read") };
        var profile = Profile(reads: reads, processes: process, tools: tools);
        var before = Apply(profile).Hash();
        reads[0] = "outside/**";
        argv[0] = "exec";
        process[0] = new ProcessRule("other", [], PermissionDecision.Deny);
        tools[0] = new ToolId("filesystem.write");
        Assert.Equal(before, Apply(profile).Hash());
        Assert.Equal("**", Assert.Single(profile.PermissionCeiling.Reads));
        Assert.Equal("test", Assert.Single(Assert.Single(profile.PermissionCeiling.Process).ArgvPatterns));
        Assert.Equal("filesystem.read", Assert.Single(profile.PreferredTools).ToString());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)profile.PermissionCeiling.Reads)[0] = "changed");
    }

    [Fact]
    public void Missing_resolution_keeps_legacy_v1_and_does_not_inherit_a_resolved_profile()
    {
        var catalog = new FakeCatalog();
        var resolved = Apply(Profile());
        var legacy = RuntimeFingerprintFactory.WithTurnConfiguration(resolved, catalog,
            catalog.Definitions(), "system", null, agentProfile: Id);
        Assert.Equal("1", Component(legacy).Version);
        Assert.NotEqual(Component(resolved).Hash, Component(legacy).Hash);
        Assert.Throws<ArgumentException>(() => Apply(Profile(), laneProfile: ProfileId.New()));
    }

    [Fact]
    public void Configuration_content_roundtrips_through_exact_CAS_and_prepare_does_not_publish_early()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reusable-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var prepared = RuntimeFingerprintFactory.PrepareTurnConfiguration(
                new ExecutionFingerprint("model", "harness", "tools", "context", "none", "build"),
                catalog, catalog.Definitions(), "system", null, store, Id, resolvedAgentProfile: Profile());
            var component = Component(prepared.Fingerprint);
            Assert.NotNull(component.Content);
            Assert.Null(store.GetText(component.Content.Hash));
            foreach (var artifact in prepared.Artifacts) artifact.Publish();
            var content = store.GetText(component.Content.Hash);
            Assert.NotNull(content);
            using var json = JsonDocument.Parse(content);
            Assert.Equal("resolved.configuration", json.RootElement.GetProperty("source").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt64());
            Assert.False(json.RootElement.GetProperty("permissionCeiling").GetProperty("allowShell").GetBoolean());
            Assert.Equal("filesystem.read", json.RootElement.GetProperty("preferredTools")[0].GetString());
            Assert.True(store.Verify(component.Hash, component.Content.Size));
            Assert.Equal(component.Hash, Component(Apply(Profile(), new FileArtifactStore(root))).Hash);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_User_configuration_resolves_reusable_identity_and_permissions()
    {
        var registry = AgentProfileConfiguration.Load(Yaml, ScopeLevel.User);
        var profile = Assert.IsType<AgentProfile>(registry.Find(Id));
        Assert.Equal("explorer", profile.Name);
        Assert.Equal(1, profile.Revision);
        Assert.Empty(profile.PermissionCeiling.Writes);
        Assert.False(profile.PermissionCeiling.AllowShell);
        Assert.Equal("filesystem.read", Assert.Single(profile.PreferredTools).ToString());
        Assert.Equal(Component(Apply(profile)).Hash,
            Component(Apply(AgentProfileConfiguration.Load(Yaml, ScopeLevel.User).Find(Id)!)).Hash);
        Assert.Throws<ArgumentException>(() => AgentProfileConfiguration.Load(Yaml, ScopeLevel.Workspace));
    }

    [Fact]
    public void Missing_unknown_or_duplicate_configuration_never_supplies_authority()
    {
        Assert.Throws<InvalidDataException>(() => AgentProfileConfiguration.Load(
            Yaml.Replace("      writes: []\n", "", StringComparison.Ordinal), ScopeLevel.User));
        Assert.Throws<InvalidDataException>(() => AgentProfileConfiguration.Load(
            Yaml.Replace("      allowShell: false", "      allowShell: maybe", StringComparison.Ordinal), ScopeLevel.User));
        Assert.Throws<InvalidDataException>(() => AgentProfileConfiguration.Load(
            Yaml.Replace("    revision: 1", "    revision: 1\n    inventedGrant: true", StringComparison.Ordinal), ScopeLevel.User));
        Assert.Throws<ArgumentException>(() => new AgentProfileRegistry([Profile(), Profile(revision: 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Profile(revision: 0));
        Assert.Throws<ArgumentException>(() => Profile(tools: [new ToolId("filesystem.read"), new ToolId("filesystem.read")]));
    }

    private const string Yaml = """
        agentProfiles:
          explorer:
            id: 0199a000-0000-7000-8000-000000000001
            revision: 1
            permissions:
              reads: ["**"]
              writes: []
              process: []
              network: []
              secrets: []
              allowShell: false
            preferredTools: [filesystem.read]
        """;
}
