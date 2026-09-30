using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// ADR-0044 §4: DestructiveActionPolicy.Ask no se trata como Allow. La frontera lo expresa como
/// RequiresAsk y el ToolRuntime lo combina por mínimo (Deny &lt; Ask &lt; Allow, INV-028).
/// </summary>
public sealed class DestructiveAskBoundaryTests
{
    private static ModelCapabilityBoundary BoundaryWith(DestructiveActionPolicy delete,
        DestructiveActionPolicy move, IReadOnlyDictionary<string, ModelToolCapability>? map = null)
    {
        var key = ModelPolicyKey.For("p", "m");
        var full = ModelPolicyPresets.FullAgent();
        var mutation = new FileMutationPolicy(FileMutationMode.Full, delete, move, 8, 2000, 1.0,
            requirePriorRead: false, requireExpectedVersionToken: false, requirePostEditValidation: false,
            allowParallelMutations: false);
        var user = new UserModelPolicy(full.Category, full.ToolPolicy, mutation, full.Source, full.Note);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16, GuidanceLevel.Full, 3,
            PlanControl.ModelDriven, 8);
        var effective = EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, user, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), harness);
        return map is null
            ? new ModelCapabilityBoundary(effective)
            : new ModelCapabilityBoundary(effective, map);
    }

    [Theory]
    [InlineData("filesystem.delete")]
    [InlineData("filesystem.move")]
    public void Ask_yields_ask_outcome_not_allow(string tool)
    {
        var boundary = BoundaryWith(DestructiveActionPolicy.Ask, DestructiveActionPolicy.Ask);
        var d = boundary.Evaluate(BoundaryTests.Intent(tool, writes: new[] { "a.txt" }));
        Assert.True(d.RequiresAsk);
        Assert.True(d.Allowed); // no rechaza: el pipeline pide confirmación
    }

    [Theory]
    [InlineData("filesystem.delete")]
    [InlineData("filesystem.move")]
    public void Deny_stays_reject(string tool)
    {
        var boundary = BoundaryWith(DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny);
        var d = boundary.Evaluate(BoundaryTests.Intent(tool, writes: new[] { "a.txt" }));
        Assert.False(d.Allowed);
        Assert.False(d.RequiresAsk);
    }

    [Theory]
    [InlineData("filesystem.delete")]
    [InlineData("filesystem.move")]
    public void Allow_stays_allowed_without_ask(string tool)
    {
        var boundary = BoundaryWith(DestructiveActionPolicy.Allow, DestructiveActionPolicy.Allow);
        var d = boundary.Evaluate(BoundaryTests.Intent(tool, writes: new[] { "a.txt" }));
        Assert.True(d.Allowed);
        Assert.False(d.RequiresAsk);
    }

    [Fact]
    public void Delete_ask_does_not_affect_move_rule()
    {
        var boundary = BoundaryWith(DestructiveActionPolicy.Ask, DestructiveActionPolicy.Allow);
        Assert.False(boundary.Evaluate(BoundaryTests.Intent("filesystem.move", writes: new[] { "a" })).RequiresAsk);
        Assert.True(boundary.Evaluate(BoundaryTests.Intent("filesystem.delete", writes: new[] { "a" })).RequiresAsk);
    }

    // ---- ToolRuntime: mínimo entre capas ----

    private sealed class Sink
    {
        public List<DomainEventPayload> Events { get; } = new();

        public VoidBox Emit(DomainEventPayload p)
        {
            Events.Add(p);
            return VoidBox.Instance;
        }

        public int Count(string name) => Events.Count(e => e.Type().ToString() == name);
    }

    private static readonly IReadOnlyDictionary<string, ModelToolCapability> DeleteMap =
        new Dictionary<string, ModelToolCapability> { { "fake.write", ModelToolCapability.DeleteFile } };

    private static (ToolRuntime.Outcome Outcome, Sink Sink) Run(DestructiveActionPolicy delete, PermissionDecision layer,
        bool userApproves)
    {
        var sink = new Sink();
        var boundary = BoundaryWith(delete, DestructiveActionPolicy.Allow, DeleteMap);
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", layer);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit, boundary);
        var outcome = runtime.Run(
            new ValidatedToolCall(ToolCallId.New(), new ToolId("fake.write"), "pc", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), userApproves, TestContext.Current.CancellationToken);
        return (outcome, sink);
    }

    [Fact]
    public void Runtime_boundary_ask_lowers_allow_to_ask_and_denies_without_approval()
    {
        var (outcome, sink) = Run(DestructiveActionPolicy.Ask, PermissionDecision.Allow, userApproves: false);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, sink.Count("toolcall.permission_requested"));
        Assert.Equal(0, sink.Count("toolcall.succeeded"));
    }

    [Fact]
    public void Runtime_boundary_ask_runs_after_user_approval()
    {
        var (outcome, sink) = Run(DestructiveActionPolicy.Ask, PermissionDecision.Allow, userApproves: true);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, sink.Count("toolcall.permission_requested"));
        Assert.Equal(1, sink.Count("toolcall.succeeded"));
    }

    [Fact]
    public void Runtime_allow_rule_does_not_ask()
    {
        var (outcome, sink) = Run(DestructiveActionPolicy.Allow, PermissionDecision.Allow, userApproves: false);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, sink.Count("toolcall.permission_requested"));
    }

    [Fact]
    public void Runtime_boundary_ask_never_raises_a_deny()
    {
        var (outcome, sink) = Run(DestructiveActionPolicy.Ask, PermissionDecision.Deny, userApproves: true);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, sink.Count("toolcall.permission_requested"));
        Assert.Equal(0, sink.Count("toolcall.succeeded"));
    }

    [Fact]
    public void Runtime_boundary_deny_rejects_before_permissions()
    {
        var (outcome, sink) = Run(DestructiveActionPolicy.Deny, PermissionDecision.Allow, userApproves: true);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, sink.Count("toolcall.permission_requested"));
    }
}
