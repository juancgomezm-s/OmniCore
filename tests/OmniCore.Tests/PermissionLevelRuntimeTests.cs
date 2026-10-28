using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class PermissionLevelRuntimeTests
{
    [Theory]
    [InlineData(PermissionProfile.Autonomous, RunMode.Act, true)]
    [InlineData(PermissionProfile.Autonomous, RunMode.Orchestrate, true)]
    [InlineData(PermissionProfile.Balanced, RunMode.Act, false)]
    [InlineData(PermissionProfile.Conservative, RunMode.Act, false)]
    public void Destructive_confirmation_defaults_follow_the_current_permission_level(
        PermissionProfile profile, RunMode mode, bool succeeds)
    {
        var events = new List<DomainEventPayload>();
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(mode).WithProfile(profile);
        var runtime = ToolRuntime.For(new FakeCatalog().Add(FakeTool.Read("filesystem.delete")), policy,
            payload => { events.Add(payload); return VoidBox.Instance; }, Boundary(ModelPolicyPresets.FullAgent()));
        var outcome = runtime.Run(new(ToolCallId.New(), new("filesystem.delete"), "test", "{}"),
            new("test", DateTimeOffset.UtcNow), new("test"), false, TestContext.Current.CancellationToken);
        Assert.Equal(succeeds, outcome.Succeeded);
        if (succeeds) Assert.DoesNotContain(events, item => item is InteractionRequested);
        else Assert.DoesNotContain(events, item => item is ToolCallStarted);
    }

    [Theory]
    [InlineData(PermissionDecision.Ask)]
    [InlineData(PermissionDecision.Deny)]
    public void Free_level_keeps_explicit_tool_rules(PermissionDecision rule)
    {
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act)
            .WithProfile(PermissionProfile.Autonomous).WithUserPolicyTool("shell.exec", rule);
        Assert.Equal(rule, policy.Evaluate(Shell()).Final);
        Assert.False(policy.AllowsAutomaticToolActionCurrently(Shell()));
    }

    [Fact]
    public void Free_level_does_not_expand_an_observing_model_ceiling()
    {
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act);
        var runtime = ToolRuntime.For(new FakeCatalog().Add(FakeTool.Read("filesystem.delete")), policy,
            _ => VoidBox.Instance, Boundary(ModelPolicyPresets.ObserveOnly()));
        Assert.False(runtime.Run(new(ToolCallId.New(), new("filesystem.delete"), "test", "{}"),
            new("test", DateTimeOffset.UtcNow), new("test"), false, TestContext.Current.CancellationToken).Succeeded);
    }

    [Theory]
    [InlineData(PermissionProfile.Autonomous, RunMode.Act, PermissionDecision.Allow)]
    [InlineData(PermissionProfile.Autonomous, RunMode.Orchestrate, PermissionDecision.Allow)]
    [InlineData(PermissionProfile.Balanced, RunMode.Act, PermissionDecision.Ask)]
    [InlineData(PermissionProfile.Conservative, RunMode.Act, PermissionDecision.Ask)]
    [InlineData(PermissionProfile.Autonomous, RunMode.Plan, PermissionDecision.Deny)]
    public void Shell_follows_level_and_run_mode(PermissionProfile profile, RunMode mode, PermissionDecision expected)
    {
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(mode).WithProfile(profile);
        Assert.Equal(expected, policy.Evaluate(Shell()).Final);
    }

    [Fact]
    public void Changing_level_is_visible_to_the_existing_policy_before_the_next_effect()
    {
        var level = PermissionProfile.Autonomous;
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act)
            .WithCurrentUserPermissions(() => (level, new Dictionary<string, PermissionDecision>()));
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Shell()).Final);
        level = PermissionProfile.Balanced;
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate(Shell()).Final);
        Assert.False(policy.AllowsAutomaticToolActionCurrently(Shell()));
    }

    [Fact]
    public void Automatic_sandbox_choice_does_not_mutate_reusable_context()
    {
        var context = new ToolExecutionContext("test");
        var once = context.WithAutomaticSandboxConsentForThisCall();
        Assert.False(context.WeakSandboxConsentForThisCall);
        Assert.True(once.WeakSandboxConsentForThisCall);
        Assert.Equal(context.WorkspaceRoot, once.WorkspaceRoot);
    }

    private static ToolIntent Shell() => new(ToolCallId.New(), new("shell.exec"), "{}",
        EffectClass.NonIdempotent, new([], [], [], new("cmd", ["/c", "echo hello"], "External"), []), ToolRisk.High, null);

    private static ModelCapabilityBoundary Boundary(UserModelPolicy policy) => new(new EffectiveModelPolicy(
        ModelPolicyKey.For("test", "permission-level"), 1, policy.Category, policy.ToolPolicy, policy.MutationPolicy,
        false, policy.MaxModelStepsPerTurn, policy.MaxToolCallsPerTurn, policy.AllowOrchestration, policy.MaxGenerationSeconds));
}
