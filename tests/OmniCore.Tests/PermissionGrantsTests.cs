namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;
using Xunit;

public sealed class PermissionGrantsTests
{
    private static ToolIntent ExternalIntent() => new(ToolCallId.New(), new ToolId("process.exec"), "{}",
        EffectClass.NonIdempotent, ResourceClaims.Empty(), ToolRisk.High, null);

    private static ScriptedPermissionPolicy UserAskPolicy(RunMode mode) =>
        new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>())
            .WithUserPolicyTool("process.exec", PermissionDecision.Ask).WithModeDefaults(mode);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-grants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Workspace_grant_applies_to_same_workspace_and_not_another_workspace()
    {
        var directory = TempDirectory();
        var audit = new InMemoryAuditSink();
        var workspace = WorkspaceId.Parse("workspace-a");
        var otherWorkspace = WorkspaceId.Parse("workspace-b");
        var policy1 = UserAskPolicy(RunMode.Act).WithGrantStore(
            new FilePermissionGrantStore(directory, audit), workspace, RunId.New());
        var intent = ExternalIntent();
        Assert.Equal(PermissionDecision.Ask, policy1.Evaluate(intent).Final);
        var grant = policy1.RecordApprovedGrant(intent, GrantLifetime.Workspace, CancellationToken.None);
        Assert.NotNull(grant);

        var laterRun = UserAskPolicy(RunMode.Act).WithGrantStore(
            new FilePermissionGrantStore(directory, audit), workspace, RunId.New());
        Assert.Equal(PermissionDecision.Allow, laterRun.Evaluate(intent).Final);
        Assert.Equal(grant, laterRun.Evaluate(intent).AppliedGrant);

        var differentWorkspace = UserAskPolicy(RunMode.Act).WithGrantStore(
            new FilePermissionGrantStore(directory, audit), otherWorkspace, RunId.New());
        Assert.Equal(PermissionDecision.Ask, differentWorkspace.Evaluate(intent).Final);
    }

    [Fact]
    public void Grants_do_not_lift_deny_plan_denial_or_unrecognized_boundary_ask()
    {
        var directory = TempDirectory();
        var workspace = WorkspaceId.Parse("workspace-a");
        var store = new FilePermissionGrantStore(directory, new InMemoryAuditSink());
        var intent = ExternalIntent();
        var askPolicy = new GrantAwarePermissionPolicy(
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Act),
            store, workspace, RunId.New());
        askPolicy.RecordApprovedGrant(intent, GrantLifetime.Workspace, CancellationToken.None);

        var deny = new GrantAwarePermissionPolicy(
            ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Deny).WithModeDefaults(RunMode.Act),
            store, workspace, RunId.New());
        Assert.Equal(PermissionDecision.Deny, deny.Evaluate(intent).Final);

        var plan = new GrantAwarePermissionPolicy(
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Plan),
            store, workspace, RunId.New());
        Assert.Equal(PermissionDecision.Deny, plan.Evaluate(intent).Final);

        var boundaryPolicy = new GrantAwarePermissionPolicy(new BoundaryAskPolicy(), store, workspace, RunId.New());
        Assert.Throws<PermissionDeniedException>(() =>
            boundaryPolicy.RecordApprovedGrant(intent, GrantLifetime.Workspace, CancellationToken.None));
    }

    [Fact]
    public void Run_grants_are_limited_to_the_run_and_revocation_is_audited()
    {
        var directory = TempDirectory();
        var audit = new InMemoryAuditSink();
        var workspace = WorkspaceId.Parse("workspace-a");
        var run = RunId.New();
        var intent = ExternalIntent();
        var store = new FilePermissionGrantStore(directory, audit);
        var policy = UserAskPolicy(RunMode.Act).WithGrantStore(store, workspace, run);
        var id = policy.RecordApprovedGrant(intent, GrantLifetime.Run, CancellationToken.None)!;
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate(intent).Final);
        Assert.Equal(PermissionDecision.Ask, UserAskPolicy(RunMode.Act)
            .WithGrantStore(store, workspace, RunId.New()).Evaluate(intent).Final);
        Assert.Contains(audit.Records(), r => r.EventName == "permission.grant.created");

        var handler = new PermissionGrantCommandHandler(store, workspace, run);
        Assert.Contains(handler.Handle(new ListPermissionGrantsCommand(), CancellationToken.None).Grants,
            g => g.Id.Equals(id.ToString(), StringComparison.Ordinal));
        var revoked = handler.Handle(new RevokePermissionGrantCommand(id.ToString()), CancellationToken.None);
        Assert.True(revoked.Revoked);
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate(intent).Final);
        Assert.Contains(audit.Records(), r => r.EventName == "permission.grant.revoked");
    }

    private sealed class TestProcessTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(new ToolId("process.exec"), "test process",
            new InputSchema("{}"), Array.Empty<string>(), false, false, ToolRisk.High,
            ComponentSource.Core(), ToolProtection.None);

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) => new Prepared(
            new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.NonIdempotent,
                new ResourceClaims(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<NetworkGrant>(),
                    new ProcessClaim("test-child", Array.Empty<string>(), "External"), Array.Empty<string>()),
                ToolRisk.High, null));

        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(ToolResult.Ok("ok", null, 0, false,
                EffectOutcome.Applied));
    }

    private sealed class TestExecutableResolver : IExecutableResolver
    {
        public ExecutableResolution Resolve(string executable, string workspaceRoot) =>
            new(executable, Path.Combine(workspaceRoot, "test-child"));
    }

    [Fact]
    public void Persistent_interaction_choice_records_grant_and_permission_event()
    {
        var directory = TempDirectory();
        var workspace = WorkspaceId.Parse("workspace-a");
        var run = RunId.New();
        var audit = new InMemoryAuditSink();
        var store = new FilePermissionGrantStore(directory, audit);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>())
            .WithModeDefaults(RunMode.Act).WithGrantStore(store, workspace, run);
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(new FakeCatalog().Add(new TestProcessTool()), policy,
            payload => { events.Add(payload); return VoidBox.Instance; }, null, new TestExecutableResolver());

        var outcome = runtime.Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1", "{}"),
            new ToolPreparationContext("test", DateTimeOffset.UtcNow), new ToolExecutionContext("test"), true,
            CancellationToken.None, GrantLifetime.Workspace);

        Assert.True(outcome.Succeeded);
        var granted = Assert.IsType<PermissionGranted>(events.Single(e => e is PermissionGranted));
        Assert.NotNull(granted.GrantId);
        Assert.Equal(GrantLifetime.Workspace, granted.Lifetime);
        var interaction = Assert.IsType<InteractionRequested>(events.Single(e => e is InteractionRequested));
        Assert.Contains("allow_workspace", interaction.OptionsJson);
        Assert.Single(store.List(workspace));
        Assert.Contains(audit.Records(), r => r.EventName == "permission.grant.created");
    }

    private sealed class BoundaryAskPolicy : IPermissionPolicy, IGrantablePermissionPolicy
    {
        public bool CanCreatePersistentGrants => true;
        public PermissionDecisionRecord Evaluate(ToolIntent intent) => new(PermissionDecision.Ask,
            new[] { new LayerDecision("ModelCapabilityBoundary", PermissionDecision.Ask, "destructive-action-ask") }, null);
        public AuthorizedToolIntent Authorize(ToolIntent intent) => throw new NotSupportedException();
        public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant) =>
            throw new NotSupportedException();
        public GrantId? RecordApprovedGrant(ToolIntent intent, GrantLifetime lifetime,
            CancellationToken cancellationToken) => GrantId.New();
    }

    [Fact]
    public void Boundary_ask_offers_only_once_and_deny_even_if_policy_supports_grants()
    {
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(new FakeCatalog().Add(new TestProcessTool()), new BoundaryAskPolicy(),
            payload => { events.Add(payload); return VoidBox.Instance; }, null, new TestExecutableResolver());

        var outcome = runtime.Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1", "{}"),
            new ToolPreparationContext("test", DateTimeOffset.UtcNow), new ToolExecutionContext("test"), false,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        var interaction = Assert.IsType<InteractionRequested>(events.Single(e => e is InteractionRequested));
        Assert.Contains("allow_once", interaction.OptionsJson);
        Assert.DoesNotContain("allow_run", interaction.OptionsJson);
        Assert.DoesNotContain("allow_workspace", interaction.OptionsJson);
    }

    [Fact]
    public void Grant_claim_key_is_exact_and_workspace_storage_is_outside_repo_directory()
    {
        var directory = TempDirectory();
        var workspace = WorkspaceId.Parse("workspace-a");
        var audit = new InMemoryAuditSink();
        var store = new FilePermissionGrantStore(directory, audit);
        var first = ExternalIntent();
        var policy = new GrantAwarePermissionPolicy(
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Act),
            store, workspace, RunId.New());
        policy.RecordApprovedGrant(first, GrantLifetime.Workspace, CancellationToken.None);

        var changed = new ToolIntent(ToolCallId.New(), new ToolId("process.exec"), "{}", EffectClass.NonIdempotent,
            new ResourceClaims(new[] { "different-resource" }, Array.Empty<string>(), Array.Empty<NetworkGrant>(),
                null, Array.Empty<string>()), ToolRisk.High, null);
        Assert.NotEqual(FilePermissionGrantStore.ClaimsKey(first), FilePermissionGrantStore.ClaimsKey(changed));
        Assert.Contains(Path.Combine(directory, "permission-grants.tsv"), Directory.GetFiles(directory));
        Assert.Contains(Path.Combine(directory, "permission-grants.audit.tsv"), Directory.GetFiles(directory));
    }
}
