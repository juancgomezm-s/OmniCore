namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Offline model fixtures through the actual permission/tool/Turn/CAS pipeline.</summary>
public sealed class EffectiveAgentProfileTests
{
    private static AgentProfile Profile(IReadOnlyList<string>? reads = null, IReadOnlyList<string>? writes = null,
        IReadOnlyList<ProcessRule>? processes = null, IReadOnlyList<NetworkRule>? networks = null,
        IReadOnlyList<string>? secrets = null, bool shell = false, IReadOnlyList<ToolId>? preferred = null,
        ProfileId? id = null, long revision = 1) => new(id ?? ProfileId.New(), "fixture", revision,
            PermissionScope.With(reads ?? ["**"], writes ?? [], processes ?? [], networks ?? [], secrets ?? [], shell),
            preferred ?? []);

    private static ToolIntent Intent(string tool = "fixture.inspect", IReadOnlyList<string>? reads = null,
        IReadOnlyList<string>? writes = null, ProcessClaim? process = null,
        IReadOnlyList<NetworkGrant>? networks = null, IReadOnlyList<string>? secrets = null) =>
        new(ToolCallId.New(), new ToolId(tool), "{}", EffectClass.None,
            new ResourceClaims(reads ?? [], writes ?? [], networks ?? [], process, secrets ?? []), ToolRisk.Low, null);

    private static void WithRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-effective-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Ceiling_intersects_mode_and_grants_never_lift_denials_or_ceiling_asks()
    {
        WithRoot(root =>
        {
            var basePolicy = new ScriptedPermissionPolicy([]).WithModeDefaults(RunMode.Act);
            var profile = Profile(preferred: [new ToolId("filesystem.write")]);
            var policy = new AgentProfilePermissionPolicy(basePolicy, profile, new PathBoundaryValidator(), root);
            var write = Intent("filesystem.write", writes: ["blocked.cs"]);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(write).Final);
            Assert.Contains(policy.Evaluate(write).Layers, layer => layer.Layer == "AgentProfile"
                && layer.Decision == PermissionDecision.Deny);
            Assert.Throws<PermissionDeniedException>(() => policy.Authorize(write));
            Assert.Throws<PermissionDeniedException>(() => policy.AuthorizeApproved(write, GrantId.New()));
            Assert.Throws<PermissionDeniedException>(() => policy.RecordApprovedGrant(write, GrantLifetime.Once, CancellationToken.None));

            var planPolicy = new AgentProfilePermissionPolicy(
                new ScriptedPermissionPolicy([]).WithModeDefaults(RunMode.Plan), Profile(writes: ["**"]),
                new PathBoundaryValidator(), root);
            Assert.Equal(PermissionDecision.Deny, planPolicy.Evaluate(write).Final); // A profile never raises PLAN.
            var askProfile = Profile(processes: [new ProcessRule("fixture-exe", ["test"], PermissionDecision.Ask)]);
            var askPolicy = new AgentProfilePermissionPolicy(basePolicy, askProfile, new PathBoundaryValidator(), root);
            var process = Intent(process: new ProcessClaim("fixture-exe", ["test"], "Observational"));
            Assert.Equal(PermissionDecision.Ask, askPolicy.Evaluate(process).Final);
            Assert.Throws<PermissionDeniedException>(() => askPolicy.AuthorizeApproved(process, GrantId.New()));
        });
    }

    [Fact]
    public void Relative_paths_are_physical_workspace_globs_not_outside_or_traversal_authority()
    {
        WithRoot(root =>
        {
            var policy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]),
                Profile(reads: ["src/**/*.cs"]), new PathBoundaryValidator(), root);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(reads: ["src/a.cs"])).Final);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(reads: ["src/deep/a.cs"])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(reads: ["src/a.txt"])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(reads: ["../outside.cs"])).Final);
            var absolute = Path.Combine(Path.GetDirectoryName(root)!, "explicit-fixture.cs");
            var explicitPolicy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]),
                Profile(reads: [absolute]), new PathBoundaryValidator(), root);
            Assert.Equal(PermissionDecision.Allow, explicitPolicy.Evaluate(Intent(reads: [absolute])).Final);
            Assert.Equal(PermissionDecision.Deny, explicitPolicy.Evaluate(Intent(reads: [absolute + ".other"])).Final);
        });
    }

    [Fact]
    public void Process_rules_match_complete_argv_and_network_secret_shell_are_independent_ceilings()
    {
        WithRoot(root =>
        {
            var profile = Profile(processes: [new ProcessRule("fixture-exe", ["test", "project"], PermissionDecision.Allow)],
                networks: [new NetworkRule("build", PermissionDecision.Allow)], secrets: ["allowed-handle"]);
            var policy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), profile,
                new PathBoundaryValidator(), root);
            var build = new ProcessClaim("fixture-exe", ["test", "project"], "WorkspaceEffect", true);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(process: build)).Final);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(process: build,
                networks: [new NetworkGrant("restore.example.test", 443)])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(process: build with { Args = ["test", "project", "extra"] })).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(process: build with { EffectClass = "External" })).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(networks: [new NetworkGrant("restore.example.test", 443)])).Final);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(secrets: ["allowed-handle"])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(secrets: ["other-handle"])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent("shell.exec")).Final);
        });
    }

    [Fact]
    public void Conflicting_matching_rules_take_minimum_and_cannot_be_approved_away()
    {
        WithRoot(root =>
        {
            var profile = Profile(processes: [new ProcessRule("*", ["test"], PermissionDecision.Allow),
                    new ProcessRule("fixture-exe", ["test"], PermissionDecision.Deny)],
                networks: [new NetworkRule("*.test", PermissionDecision.Allow), new NetworkRule("blocked.test", PermissionDecision.Deny)]);
            var policy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), profile,
                new PathBoundaryValidator(), root);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(process: new ProcessClaim("fixture-exe", ["test"], "Observational"))).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(networks: [new NetworkGrant("blocked.test", 443)])).Final);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent(networks: [new NetworkGrant("allowed.test", 443)])).Final);
        });
    }

    [Fact]
    public void Actual_tool_pipeline_rejects_preferred_write_without_executing_or_emitting_started()
    {
        WithRoot(root =>
        {
            var tool = new CountingWrite();
            var catalog = new FakeCatalog().Add(tool);
            var policy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]),
                Profile(preferred: [tool.Descriptor.Id]), new PathBoundaryValidator(), root);
            var executor = ScriptedToolExecutor.WithWorkspace(catalog, policy, root);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane));
            var result = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "fixture-call", "{}"),
                true, CancellationToken.None, new EventStream(store, codecs, session));
            Assert.False(result.Succeeded);
            Assert.Equal(0, tool.Executions);
            Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode), payload => payload is ToolCallStarted);
            Assert.Contains(result.Events, payload => payload is PermissionEvaluated evaluated
                && evaluated.Decision == PermissionDecision.Deny);
        });
    }

    [Fact]
    public void Turn_uses_applied_ceiling_preferences_and_records_exact_profile_content_not_a_requested_label()
    {
        WithRoot(root =>
        {
            var first = FakeTool.Read("fixture.first");
            var preferred = FakeTool.Read("fixture.preferred");
            var catalog = new FakeCatalog().Add(first).Add(preferred);
            var profile = Profile(preferred: [preferred.Descriptor.Id]);
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), profile, new PathBoundaryValidator(), root), root);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session, agentProfile: profile.Id);
            ModelRequest? request = null;
            var turn = Turn(executor, catalog, store, codecs, artifacts, (value, _) =>
            {
                request = value;
                return new ModelResponse([new TextBlock("fixture response")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "", null));
            });
            Assert.Equal(StopReason.EndTurn, turn.Ask("hello", "fixture", session, run.RunId, run.RootLane, "", CancellationToken.None).StopReason);
            Assert.NotNull(request);
            Assert.Equal("fixture.preferred", Assert.Single(request.Tools).Name);
            var fingerprint = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()).Fingerprint!;
            var component = Assert.Single(fingerprint.Components, part => part.Name == "agent.profile");
            Assert.Equal("2", component.Version);
            Assert.NotNull(component.Content);
            using var json = JsonDocument.Parse(artifacts.GetText(component.Content.Hash)!);
            Assert.Equal(profile.Id.ToString(), json.RootElement.GetProperty("profileId").GetString());
            Assert.Equal("resolved.configuration", json.RootElement.GetProperty("source").GetString());
            Assert.Empty(json.RootElement.GetProperty("permissionCeiling").GetProperty("writes").EnumerateArray());
            Assert.Equal("fixture.preferred", json.RootElement.GetProperty("preferredTools")[0].GetString());
            Assert.Contains(component.Content, Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is TurnStarted).ArtifactRefs);
        });
    }

    [Fact]
    public void Mismatched_lane_and_applied_profile_fail_before_provider_dispatch()
    {
        WithRoot(root =>
        {
            var profile = Profile();
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), profile, new PathBoundaryValidator(), root), root);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session); // Different configuration identity is intentional.
            var calls = 0;
            var turn = Turn(executor, catalog, store, codecs, new FileArtifactStore(root), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Must reject before dispatch.");
            });
            Assert.Equal(StopReason.Error, turn.Ask("hello", "fixture", session, run.RunId, run.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(0, calls);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
        });
    }

    private static ExplorerTurn Turn(ScriptedToolExecutor executor, FakeCatalog catalog, IEventStore store,
        EventCodecs codecs, IArtifactStore artifacts, Func<ModelRequest, CancellationToken, ModelResponse> complete) =>
        new(complete, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), store, codecs, artifacts,
            new InMemoryAuditSink(), new RedactionPolicy(),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 1, GuidanceLevel.Full, 1, PlanControl.Assisted, 4),
            recordEffectiveFingerprint: true);

    private sealed class CountingWrite : ITool
    {
        public int Executions { get; private set; }
        public ToolDescriptor Descriptor { get; } = new(new ToolId("fixture.write"), "write fixture", new InputSchema("{}"),
            [], false, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None, EffectClass.Rerunnable);
        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) => new Prepared(
            new ToolIntent(call.ToolCallId, call.ToolId, "{}", EffectClass.Rerunnable,
                new ResourceClaims([], ["blocked.txt"], [], null, []), ToolRisk.Low, null));
        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent,
            ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++;
            return System.Threading.Tasks.Task.FromResult(new ToolResult("unexpected", "unexpected", null, 0, false, EffectOutcome.Applied));
        }
    }
}
