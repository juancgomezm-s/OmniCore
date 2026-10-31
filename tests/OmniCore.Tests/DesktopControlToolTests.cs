using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class DesktopControlToolTests
{
    [Fact]
    public void Inactive_skill_has_no_catalog_definition_search_entry_or_guessed_identity()
    {
        using var fixture = new Fixture();
        var host = HostTools.Default();
        using var tool = new DesktopControlTool(fixture.Gate);
        host.ComposeDesktopTool(tool, fixture.Gate.Selected);
        Assert.Null(host.Catalog().Find(new ToolId(DesktopControlTool.Id)));
        Assert.DoesNotContain(host.Catalog().Definitions(), d => d.Name == DesktopControlTool.Id);
        Assert.DoesNotContain(host.Catalog().RegisteredTools(), t => t.Descriptor.Id.ToString() == DesktopControlTool.Id);
        Assert.IsType<PreparationRejected>(tool.Prepare(Call(), Preparation(fixture)));
        Assert.False(tool.HasStarted);
        fixture.Activate();
        host.ComposeDesktopTool(tool, fixture.Gate.Selected);
        Assert.Same(tool, host.Catalog().Find(new ToolId(DesktopControlTool.Id)));
        fixture.Gate.Select([]);
        host.ComposeDesktopTool(tool, false);
        Assert.Null(host.Catalog().Find(new ToolId(DesktopControlTool.Id)));
    }

    [Fact]
    public async Task Release_after_prepare_rejects_before_any_native_process_launch()
    {
        using var f = new Fixture(); f.Activate(); using var tool = new DesktopControlTool(f.Gate);
        var intent = Assert.IsType<Prepared>(tool.Prepare(Call(), Preparation(f))).Intent;
        var authorized = ScriptedPermissionPolicy.WithTool(DesktopControlTool.Id, PermissionDecision.Allow).Authorize(intent);
        f.Store.Release(f.Session, ScopeLevel.BuiltIn, "desktop-control");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(authorized, new ToolExecutionContext(f.Root), TestContext.Current.CancellationToken));
        Assert.False(tool.HasStarted);
    }

    [Fact]
    public void Session_identity_and_package_hash_are_rechecked()
    {
        using var f = new Fixture(); f.Activate(); Assert.True(f.Gate.IsCurrent());
        var other = new DesktopSkillGate(SessionId.New(), f.Store, f.SkillDirectory);
        other.Select([f.Loaded]); Assert.False(other.IsCurrent());
        File.AppendAllText(Path.Combine(f.SkillDirectory, "instructions.md"), "\nChanged after activation.");
        Assert.False(f.Gate.IsCurrent());
    }

    [Fact]
    public void Project_or_unsealed_imitation_and_delegated_snapshot_cannot_select_desktop()
    {
        using var f = new Fixture();
        f.Gate.Select([f.Loaded with { Skill = f.Loaded.Skill with { Scope = ScopeLevel.Project, Trust = TrustLevel.Project } }]);
        Assert.False(f.Gate.Selected);
        f.Gate.Select([f.Loaded with { Skill = f.Loaded.Skill with { Manifest = f.Loaded.Skill.Manifest with { Sealed = false } } }]);
        Assert.False(f.Gate.Selected);
        f.Gate.Select([]); Assert.False(f.Gate.Selected);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("invoke")]
    [InlineData("set_value")]
    public void Permission_deny_does_not_start_native_component_and_activation_grants_nothing(string operation)
    {
        using var f = new Fixture(); f.Activate(); using var tool = new DesktopControlTool(f.Gate);
        var intent = Assert.IsType<Prepared>(tool.Prepare(Call(operation), Preparation(f))).Intent;
        var policy = ScriptedPermissionPolicy.WithTool(DesktopControlTool.Id, PermissionDecision.Deny);
        Assert.Throws<PermissionDeniedException>(() => policy.Authorize(intent));
        Assert.False(tool.HasStarted);
        Assert.Equal(EffectClass.NonIdempotent, intent.Effect);
        Assert.Equal(ModelToolCapability.GeneralProcess, ModelCapabilityBoundary.CoreTools[DesktopControlTool.Id]);
    }

    [Theory]
    [InlineData("capabilities", EffectClass.None)]
    [InlineData("get_window_state", EffectClass.None)]
    [InlineData("click", EffectClass.NonIdempotent)]
    [InlineData("type_text", EffectClass.NonIdempotent)]
    [InlineData("invoke", EffectClass.NonIdempotent)]
    [InlineData("set_value", EffectClass.NonIdempotent)]
    public void Effects_are_classified_without_io(string operation, EffectClass expected)
    {
        using var f = new Fixture(); f.Activate(); using var tool = new DesktopControlTool(f.Gate);
        Assert.Equal(expected, Assert.IsType<Prepared>(tool.Prepare(Call(operation), Preparation(f))).Intent.Effect);
        Assert.False(tool.HasStarted);
    }

    [Theory]
    [InlineData("{\"operation\":\"shell.exec\",\"arguments\":{}}")]
    [InlineData("{\"operation\":\"click\",\"arguments\":{},\"executable\":\"other.exe\"}")]
    [InlineData("{\"operation\":\"click\",\"arguments\":[]}")]
    public void Arbitrary_dispatch_and_executable_parameters_are_rejected(string json)
    {
        using var f = new Fixture(); f.Activate(); using var tool = new DesktopControlTool(f.Gate);
        Assert.IsType<PreparationRejected>(tool.Prepare(new ValidatedToolCall(ToolCallId.New(), new(DesktopControlTool.Id), "fixture", json), Preparation(f)));
        Assert.False(tool.HasStarted);
    }

    private static ValidatedToolCall Call(string operation = "capabilities") => new(ToolCallId.New(), new(DesktopControlTool.Id), "fixture",
        "{\"operation\":\"" + operation + "\",\"arguments\":{}}");
    private static ToolPreparationContext Preparation(Fixture f) => new(f.Root, DateTimeOffset.UtcNow);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-desktop-test-" + Guid.NewGuid().ToString("N"));
        public string SkillDirectory => Path.Combine(Root, "skill");
        public SessionId Session { get; } = SessionId.New();
        public SessionSkillActivationStore Store { get; }
        public DesktopSkillGate Gate { get; }
        public LoadedSkill Loaded { get; }
        public Fixture()
        {
            Directory.CreateDirectory(SkillDirectory);
            File.WriteAllText(Path.Combine(SkillDirectory, "skill.yaml"), """
                id: desktop-control
                version: 0.1.0
                sealed: true
                modelInvocable: true
                activation:
                  explicit: true
                tools: [desktop.control]
                """);
            File.WriteAllText(Path.Combine(SkillDirectory, "instructions.md"), "Native fixture instructions.");
            Store = new SessionSkillActivationStore(Path.Combine(Root, "data"));
            var loader = new SkillManifestLoader();
            Loaded = loader.Activate(loader.DiscoverSingle(SkillDirectory, ScopeLevel.BuiltIn, TrustLevel.Core)!);
            Gate = new DesktopSkillGate(Session, Store, SkillDirectory);
        }
        public void Activate()
        {
            Store.Activate(Session, ScopeLevel.BuiltIn, "desktop-control", Loaded.Skill.ManifestSha256, Loaded.PackageContentSha256!);
            Gate.Select([Loaded]);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
