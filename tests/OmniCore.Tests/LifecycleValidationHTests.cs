using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;

namespace OmniCore.Tests;

public sealed class LifecycleValidationHTests
{
    [Fact]
    public void Lifecycle_start_and_terminal_points_reject_nonconsumable_capabilities()
    {
        foreach (var point in new[] { LifecyclePoint.SessionStarting, LifecyclePoint.SessionStarted,
                     LifecyclePoint.RunStarting, LifecyclePoint.TaskCreated, LifecyclePoint.LaneStarting,
                     LifecyclePoint.RunFailed })
        {
            Assert.True(LifecycleHookSupport.IsSupported(point));
            Assert.False(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.Restrict));
            Assert.False(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.RequireValidation));
        }
        foreach (var point in new[] { LifecyclePoint.RunCompleted, LifecyclePoint.RunFailed })
        {
            Assert.True(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.Observe));
            Assert.False(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.Restrict));
            Assert.False(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.RequireValidation));
        }
        foreach (var point in new[] { LifecyclePoint.AfterModel, LifecyclePoint.AfterTool,
                     LifecyclePoint.ToolFailed, LifecyclePoint.BeforeValidation, LifecyclePoint.AfterValidation })
            Assert.True(LifecycleHookSupport.IsCapabilitySupported(point, HookCapability.Restrict));
    }

    [Fact]
    public void Real_act_creation_dispatches_session_run_task_and_lane_lifecycle_points()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-lifecycle-start-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.lifecycle");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.lifecycle
            version: 1.0
            displayName: Fixture lifecycle
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              hooks:
                - point: SessionStarting
                  capabilities: [Observe]
                - point: SessionStarted
                  capabilities: [Observe]
                - point: RunStarting
                  capabilities: [Observe]
                - point: TaskCreated
                  capabilities: [Observe]
                - point: LaneStarting
                  capabilities: [Observe]
            requests:
              capabilities: [hook.register, hook.observe]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        try
        {
            LifecycleRpcFixture.Reset();
            var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "user-data"));
            new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
            var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(workspace, ".omnicore", "extensions"), TrustLevel.Project,
                workspaceTrusted: true));
            new ExtensionConsentStore(paths, workspace).Grant(candidate,
                new HashSet<string>(["hook.register", "hook.observe"], StringComparer.Ordinal));
            var store = new InMemoryEventStore();
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(),
                platformPathsFactory: () => paths);
            server.ConfigureLifecycleRpcInvoker(static () => new LifecycleRpcFixture());
            var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                + JsonObj.Field("objective", "inspect") + "," + JsonObj.Field("workspace", workspace) + "}"),
                CancellationToken.None);

            Assert.Equal("ok", ack.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var applied = store.ReadFrom(session, 1).Select(EventCodecs.Create().Decode)
                .OfType<HookApplied>().ToArray();
            Assert.Equal(new[] { LifecyclePoint.SessionStarting, LifecyclePoint.SessionStarted,
                    LifecyclePoint.RunStarting, LifecyclePoint.TaskCreated, LifecyclePoint.LaneStarting }
                .Select(point => point.ToString()).ToArray(),
                applied.Select(item => item.Point).ToArray());
            Assert.All(applied, item => Assert.Null(item.TurnId));
            Assert.Equal(5, LifecycleRpcFixture.Calls);

            LifecycleRpcFixture.Reset();
            var inputPaths = OmniHost.CreatePlatformPaths(Path.Combine(root, "input-user-data"));
            new WorkspaceTrustStore(inputPaths).SetTrusted(workspace, true);
            var inputCandidate = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(workspace, ".omnicore", "extensions"), TrustLevel.Project,
                workspaceTrusted: true));
            new ExtensionConsentStore(inputPaths, workspace).Grant(inputCandidate,
                new HashSet<string>(["hook.register", "hook.observe"], StringComparer.Ordinal));
            var inputServer = new OmniServer(store: new InMemoryEventStore(), codecs: EventCodecs.Create(),
                audit: new InMemoryAuditSink(), platformPathsFactory: () => inputPaths);
            inputServer.ConfigureWorkspaceRoot(workspace);
            inputServer.ConfigureLifecycleRpcInvoker(static () => new LifecycleRpcFixture());
            var inputAck = inputServer.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "session.input")
                + "," + JsonObj.Field("text", "ordinary input") + "}"), CancellationToken.None);
            Assert.Equal("ok", inputAck.Status);
            var inputSession = Assert.IsType<SessionId>(inputServer.LastSessionId());
            var inputApplied = inputServer.AcquireStore().ReadFrom(inputSession, 1).Select(EventCodecs.Create().Decode)
                .OfType<HookApplied>().ToArray();
            Assert.Equal(new[] { LifecyclePoint.SessionStarting, LifecyclePoint.SessionStarted,
                    LifecyclePoint.RunStarting, LifecyclePoint.TaskCreated, LifecyclePoint.LaneStarting }
                .Select(point => point.ToString()).ToArray(),
                inputApplied.Select(item => item.Point).ToArray());
            Assert.Null(inputApplied[0].RunId);
            Assert.Null(inputApplied[1].RunId);
            Assert.All(inputApplied.Skip(2), item => Assert.Equal(inputServer.LastRunId(), item.RunId));
            Assert.Equal(5, LifecycleRpcFixture.Calls);
            Assert.Equal(Assert.Single(inputServer.AcquireStore().ReadFrom(inputSession, 1)
                .Select(EventCodecs.Create().Decode).OfType<TaskCreated>()).TaskId.ToString(),
                LifecycleRpcFixture.LastTaskId);
            Assert.Equal(inputServer.LastLaneId()!.Value.ToString(), LifecycleRpcFixture.LastLaneId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Delegated_task_and_lane_creation_are_observed_at_owners_without_child_hook_inheritance()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(fx.WorkspaceRoot + "-data", "platform"));
        new WorkspaceTrustStore(paths).SetTrusted(fx.WorkspaceRoot, true);
        var package = Path.Combine(fx.WorkspaceRoot, ".omnicore", "extensions", "fixture.lifecycle.child");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.lifecycle.child
            version: 1.0
            displayName: Fixture child lifecycle
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              hooks:
                - point: TaskCreated
                  capabilities: [Observe]
                - point: LaneStarting
                  capabilities: [Observe]
            requests:
              capabilities: [hook.register, hook.observe]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
            Path.Combine(fx.WorkspaceRoot, ".omnicore", "extensions"), TrustLevel.Project,
            workspaceTrusted: true));
        new ExtensionConsentStore(paths, fx.WorkspaceRoot).Grant(candidate,
            new HashSet<string>(["hook.register", "hook.observe"], StringComparer.Ordinal));
        LifecycleRpcFixture.Reset();
        fx.Server.ConfigureLifecycleRpcInvoker(static () => new LifecycleRpcFixture());
        fx.Ask();
        fx.Grant();
        var request = DelegationCommands.Create(fx.Request() with { MaxTokens = 12_000 }, Ids.NewV7());
        var admitted = fx.Server.SendUserAction(request, TestContext.Current.CancellationToken);
        Assert.Equal("ok", admitted.Status);
        var child = Assert.Single(fx.Payloads.OfType<DelegationCreated>()).Delegation;
        var taskApplication = Assert.Single(fx.Payloads.OfType<HookApplied>(), item => item.Point == "TaskCreated");
        var taskAppliedEnvelope = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt =>
            fx.Codecs.Decode(evt) is HookApplied applied && applied.Point == taskApplication.Point);
        Assert.Equal(child.ChildTaskId, taskAppliedEnvelope.TaskId);
        Assert.Equal(child.ChildLaneId, taskAppliedEnvelope.LaneId);
        Assert.Equal(child.ChildTaskId.ToString(), LifecycleRpcFixture.LastTaskId);
        Assert.Equal(child.ChildLaneId.ToString(), LifecycleRpcFixture.LastLaneId);
        Assert.Equal(1, LifecycleRpcFixture.Calls);

        var modelCalls = 0;
        ExplorerTurn.TurnResult? childTurnResult = null;
        var executed = fx.Server.ExecuteDelegation(child.DelegationId, (work, token) =>
            childTurnResult = fx.Explorer((_, _) =>
            {
                modelCalls++;
                return new ModelResponse([new TextBlock("child done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }).Ask(work.Objective, "system", work.Session, work.Run, work.Delegation.ChildLaneId,
                "", token), TestContext.Current.CancellationToken);
        Assert.Equal("ok", executed.Status);
        Assert.Equal(StopReason.EndTurn, childTurnResult?.StopReason);
        Assert.Equal(1, modelCalls);
        Assert.Equal(2, LifecycleRpcFixture.Calls);
        Assert.Equal(child.ChildTaskId.ToString(), LifecycleRpcFixture.LastTaskId);
        Assert.Equal(child.ChildLaneId.ToString(), LifecycleRpcFixture.LastLaneId);
        var laneApplication = Assert.Single(fx.Payloads.OfType<HookApplied>(), item => item.Point == "LaneStarting");
        var laneAppliedEnvelope = Assert.Single(fx.Store.ReadFrom(fx.Session, 1), evt =>
            fx.Codecs.Decode(evt) is HookApplied applied && applied.Point == laneApplication.Point);
        Assert.Equal(child.ChildTaskId, laneAppliedEnvelope.TaskId);
        Assert.Equal(child.ChildLaneId, laneAppliedEnvelope.LaneId);
        Assert.DoesNotContain(fx.Payloads.OfType<HookApplied>(), item => item.TurnId is not null);
    }

    [Theory]
    [InlineData(false, "RunCompleted")]
    [InlineData(true, "RunFailed")]
    public void Run_coupon_dispatches_terminal_lifecycle_at_the_matching_transition(bool failRequired,
        string expectedPoint)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var stream = new EventStream(store, codecs, session);
        if (failRequired)
        {
            var plan = PlanId.New();
            var item = PlanItemId.New();
            stream.Append(new PlanCreated(plan, run.RunId, item, "objective"));
            stream.Append(new PlanItemReady(item));
            stream.Append(new PlanItemStarted(item));
            stream.Append(new PlanItemFailed(item, "fixture failure"));
        }
        var before = store.ReadFrom(session, 1);
        var points = new List<LifecyclePoint>();
        var coupon = new RunCoupon(RunProjection.Replay(session, run.RunId, codecs, before),
            TaskGraphProjection.Replay(codecs, before), PlanProjection.Replay(codecs, before));

        var completed = coupon.CheckCompletionAndGate(new PlanService(), new ProgressReconciler(),
            store, codecs, session, stream, runExternalGates: static () => [],
            lifecycleDispatch: point =>
            {
                points.Add(point);
                stream.Append(new HookApplied(run.RunId, null, "fixture.terminal", point.ToString(), "observed"));
                return HookDispatchResult.Empty;
            });

        Assert.Equal(!failRequired, completed);
        Assert.Equal(expectedPoint, Assert.Single(points).ToString());
        var terminal = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
        var terminalIndex = Array.FindIndex(terminal, item => item is RunFailed or RunCompleted);
        var appliedIndex = Array.FindIndex(terminal, item => item is HookApplied applied
            && applied.Point == expectedPoint);
        Assert.True(terminalIndex >= 0 && appliedIndex > terminalIndex);
    }

    [Fact]
    public void Failed_completion_gate_does_not_dispatch_run_completed_hook()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var before = store.ReadFrom(session, 1);
        var stream = new EventStream(store, codecs, session);
        var points = new List<LifecyclePoint>();
        var manifest = new ExtensionManifest
        {
            ManifestVersion = 1,
            Id = "fixture.gate.package",
            Version = "1.0",
            DisplayName = "fixture",
            Publisher = "tests",
            OmnicoreApi = "1.0",
            Runtime = new ExtensionRuntimeManifest { Kind = "process", Command = "runtime.exe", Args = [] },
            Provides = new ExtensionProvidesManifest { CompletionGates = ["integration"] },
        };
        var extension = new DiscoveredExtension(Path.GetTempPath(), manifest, ExtensionDiscoveryStatus.Ready,
            null, new string('a', 64), new string('b', 64), TrustLevel.Project);
        var rpc = new CompletionGateRpcFixture();
        var validator = new ExtensionCompletionValidator(extension, [], Path.GetTempPath(), session,
            run.RunId, rpc, completionGates: ["integration"]);
        var gateResults = validator.Run(TestContext.Current.CancellationToken, boundedRunContextJson: "{}")
            .Select(result => result with { Passed = false }).ToArray();
        var coupon = new RunCoupon(RunProjection.Replay(session, run.RunId, codecs, before),
            TaskGraphProjection.Replay(codecs, before), PlanProjection.Replay(codecs, before));

        var completed = coupon.CheckCompletionAndGate(new PlanService(), new ProgressReconciler(),
            store, codecs, session, stream,
            runExternalGates: () => gateResults,
            lifecycleDispatch: point =>
            {
                points.Add(point);
                stream.Append(new HookApplied(run.RunId, null, "fixture.terminal", point.ToString(), "observed"));
                return HookDispatchResult.Empty;
            });

        Assert.False(completed);
        Assert.DoesNotContain(points, point => point == LifecyclePoint.RunCompleted);
        Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<HookApplied>(),
            applied => applied.Point == LifecyclePoint.RunCompleted.ToString());
        Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode), item => item is RunCompleted);
        Assert.Equal("completion/gate", rpc.Method);
        Assert.Equal("integration", rpc.GateId);
    }

    [Fact]
    public void Validation_context_is_valid_bounded_redacted_and_has_real_tool_evidence()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, new string('o', 30_000));
        var stream = new EventStream(store, codecs, session);
        var toolCall = ToolCallId.New();
        stream.Append(new ToolCallRequested(toolCall, "fixture-call", "process.exec",
            "{\"authorization\":\"Bearer verysecretvalue123\"}"));
        stream.Append(new ToolCallPrepared(toolCall, "{}"));
        stream.Append(new PermissionEvaluated(toolCall, PermissionDecision.Allow, "{}", null));
        stream.Append(new ToolCallAuthorized(toolCall));
        stream.Append(new ToolCallStarted(toolCall, EffectClass.NonIdempotent, null));
        stream.Append(new ToolCallSucceeded(toolCall,
            "{\"output\":\"Authorization: Bearer verysecretvalue123\"}"));

        var json = LifecycleValidationContextBuilder.Build(session, run.RunId, TurnId.New(),
            new string('o', 30_000), stream.EventsSince(1).Where(evt => evt.RunId == run.RunId),
            codecs, new RedactionPolicy());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var evidence = document.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
        var toolEvidence = Assert.Single(evidence, item => item.TryGetProperty("tool", out var tool)
            && tool.GetString() == "process.exec");
        Assert.Equal("succeeded", toolEvidence.GetProperty("outcome").GetString());
        Assert.Equal("NonIdempotent", toolEvidence.GetProperty("effect").GetString());
        Assert.DoesNotContain("verysecretvalue123", json, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 8_192);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Required_skill_validator_ids_with_periods_are_selected_exactly()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-validator-id-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            static LoadedSkill Skill(string root, string skillId, string ruleId) =>
                new(new DiscoveredSkill(root, new SkillManifest { Id = skillId, Version = "1", BudgetTokens = 64 },
                        ScopeLevel.Session, TrustLevel.Trusted, new string('a', 64), SkillLifecycleState.Loaded, null),
                    "instructions", new string('b', 64), [new LoadedSkillResource(SkillResourceKind.Validator,
                        "validators/check.json", "{\"id\":\"" + ruleId
                            + "\",\"kind\":\"file-contains\",\"path\":\"output.txt\",\"text\":\"ok\"}",
                        new string('c', 64))], new string('d', 64));
            var validator = new SkillCompletionValidator([
                Skill(root, "fixture.skill.v2", "check.output.v1"),
                Skill(root, "fixture.other", "unselected")]);
            var requested = new HashSet<string>(StringComparer.Ordinal)
                { "skill.fixture.skill.v2.check.output.v1" };
            var outcomes = validator.Run(root, _ => new SkillValidationReadResult(true, "ok"),
                CancellationToken.None, requestedHookIds: requested);
            var outcome = Assert.Single(outcomes);
            Assert.True(outcome.Passed);
            Assert.Equal("skill.fixture.skill.v2.check.output.v1", outcome.ValidatorId);
            Assert.Equal("skill:fixture.skill.v2:check.output.v1", outcome.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Required_extension_validator_uses_declared_identity_and_does_not_run_siblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-validator-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = new ExtensionManifest
            {
                ManifestVersion = 1,
                Id = "fixture.package.v2",
                Version = "1.0",
                DisplayName = "fixture",
                Publisher = "tests",
                OmnicoreApi = "1.0",
                Runtime = new ExtensionRuntimeManifest { Kind = "process", Command = "runtime.exe", Args = [] },
                Provides = new ExtensionProvidesManifest { Validators = ["check.one", "check.two"] },
            };
            var extension = new DiscoveredExtension(root, manifest, ExtensionDiscoveryStatus.Ready, null,
                new string('a', 64), new string('b', 64), TrustLevel.Project);
            var rpc = new ValidationRpcFixture();
            var validator = new ExtensionCompletionValidator(extension, manifest.Provides.Validators!,
                root, SessionId.New(), RunId.New(), rpc);
            var requested = new HashSet<string>(StringComparer.Ordinal)
                { "extension.fixture.package.v2.check.one" };

            var outcomes = validator.Run(CancellationToken.None, "{}", requested);

            var outcome = Assert.Single(outcomes);
            Assert.True(outcome.Passed);
            Assert.Equal("extension.fixture.package.v2.check.one", outcome.ValidatorId);
            Assert.Equal("extension:fixture.package.v2:check.one", outcome.Key);
            Assert.Equal("completion/validate", rpc.Method);
            Assert.Equal(new[] { "check.one" }, rpc.RequestedIds);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ValidationRpcFixture : IExtensionRpcInvoker
    {
        public string? Method { get; private set; }
        public string[] RequestedIds { get; private set; } = [];

        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, ResourceClaims authorizedClaims, CancellationToken cancellationToken)
        {
            Method = method;
            using var request = System.Text.Json.JsonDocument.Parse(parametersJson);
            RequestedIds = request.RootElement.GetProperty("validators").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            var rows = string.Join(",", RequestedIds.Select(id => "{\"id\":\"" + id
                + "\",\"passed\":true,\"summary\":\"passed\"}"));
            return System.Threading.Tasks.Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":["
                + rows + "]}}");
        }
    }

    private sealed class CompletionGateRpcFixture : IExtensionRpcInvoker
    {
        public string? Method { get; private set; }
        public string? GateId { get; private set; }

        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, ResourceClaims authorizedClaims, CancellationToken cancellationToken)
        {
            Method = method;
            using var request = System.Text.Json.JsonDocument.Parse(parametersJson);
            GateId = request.RootElement.GetProperty("gates")[0].GetString();
            return System.Threading.Tasks.Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":["
                + "{\"id\":\"integration\",\"passed\":true,\"summary\":\"ready\"}]}}");
        }
    }

    private sealed class LifecycleRpcFixture : IExtensionRpcInvoker
    {
        private static int _calls;
        public static string? LastTaskId { get; private set; }
        public static string? LastLaneId { get; private set; }
        public static int Calls => Volatile.Read(ref _calls);
        public static void Reset()
        {
            Interlocked.Exchange(ref _calls, 0);
            LastTaskId = null;
            LastLaneId = null;
        }
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, ResourceClaims authorizedClaims, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using (var request = System.Text.Json.JsonDocument.Parse(parametersJson))
            {
                LastTaskId = request.RootElement.TryGetProperty("taskId", out var task) ? task.GetString() : null;
                LastLaneId = request.RootElement.TryGetProperty("laneId", out var lane) ? lane.GetString() : null;
            }
            return System.Threading.Tasks.Task.FromResult(
                "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"kind\":\"continue\"}}");
        }
    }
}
