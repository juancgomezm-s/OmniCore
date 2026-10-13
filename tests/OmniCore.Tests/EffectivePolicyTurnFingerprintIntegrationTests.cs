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

namespace OmniCore.Tests;

public sealed class EffectivePolicyTurnFingerprintIntegrationTests
{
    private static readonly ProfileId SameAgentProfile = ProfileId.New();

    private sealed class PreferenceTool(string id, ToolProtection protection, string? contentIdentity = null,
        string schema = "{}") : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(new ToolId(id), "fixture preference tool",
            new InputSchema(schema), ["same-capability"], true, false, ToolRisk.Low,
            contentIdentity is null ? ComponentSource.Core() : new ComponentSource(SourceKind.Extension,
                ScopeLevel.Project, TrustLevel.Project, "fixture", "1", contentIdentity), protection, EffectClass.None);
        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));
        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(new ToolResult("ok", "ok", null, 1,
                false, EffectOutcome.None));
    }
    [Fact]
    public void Effective_policy_controls_the_sent_tool_set_and_durable_turn_fingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-effective-policy-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // One real HostTools catalog contains the mutation tool; the same instance is used for all runs.
            var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(),
                includeSimulationTools: false, includeMutationTools: true);
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
            var policyKey = ModelPolicyKey.For("fixture-provider", "fixture-model");
            var observe = Resolve(policyKey, harness, ModelPolicyPresets.ObserveOnly(), revision: 1);
            var patch = Resolve(policyKey, harness, ModelPolicyPresets.PatchOnly(), revision: 2);

            var observeRun = Execute(root, hostTools.Catalog(), observe, harness);
            var patchRun = Execute(root, hostTools.Catalog(), patch, harness);
            var patchAgain = Execute(root, hostTools.Catalog(), patch, harness);

            Assert.DoesNotContain("filesystem.patch", observeRun.ToolsSent);
            Assert.Contains("filesystem.patch", patchRun.ToolsSent);
            Assert.Equal(patchRun.ToolsSent, patchAgain.ToolsSent);

            Assert.NotEqual(observe.Fingerprint(), patch.Fingerprint());
            Assert.Equal(observe.Fingerprint(), observeRun.Fingerprint.ModelPolicyHash);
            Assert.Equal(patch.Fingerprint(), patchRun.Fingerprint.ModelPolicyHash);
            Assert.NotEqual(observeRun.Fingerprint.Hash(), patchRun.Fingerprint.Hash());
            Assert.NotEqual(Component(observeRun.Fingerprint, "tools.plan"),
                Component(patchRun.Fingerprint, "tools.plan"));
            Assert.NotEqual(patchRun.SessionId, patchAgain.SessionId);
            Assert.NotEqual(patchRun.RunId, patchAgain.RunId);
            Assert.NotEqual(patchRun.TurnId, patchAgain.TurnId);
            Assert.Equal(patchRun.Fingerprint.Hash(), patchAgain.Fingerprint.Hash());
            Assert.Equal(Component(patchRun.Fingerprint, "tools.plan"),
                Component(patchAgain.Fingerprint, "tools.plan"));
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void User_tool_preference_selects_only_an_already_eligible_equivalent_tool()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-preference-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = Resolve(ModelPolicyKey.For("fixture-provider", "fixture-model"),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                    GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8),
                ModelPolicyPresets.PatchOnly(), revision: 1);
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.source")).Add(FakeTool.Read("fixture.target"));
            var observation = Execute(root, catalog, policy,
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                    GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8),
                new Dictionary<string, string> { ["fixture.source"] = "fixture.target" }, useBoundary: false);

            Assert.DoesNotContain("fixture.source", observation.ToolsSent);
            Assert.Contains("fixture.target", observation.ToolsSent);
            Assert.Contains(observation.Fingerprint.Components, component => component.Name == "tools.preferences");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Tool_preference_does_not_make_a_boundary_hidden_alternative_visible()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-preference-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = Resolve(ModelPolicyKey.For("fixture-provider", "fixture-model"),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                    GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8),
                ModelPolicyPresets.PatchOnly(), revision: 1);
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.source")).Add(FakeTool.Read("fixture.target"));
            var observation = Execute(root, catalog, policy,
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8,
                    GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8),
                new Dictionary<string, string> { ["fixture.source"] = "fixture.target" },
                capabilities: new Dictionary<string, ModelToolCapability>
                { ["fixture.source"] = ModelToolCapability.WorkspaceRead });

            Assert.Contains("fixture.source", observation.ToolsSent);
            Assert.DoesNotContain("fixture.target", observation.ToolsSent);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Protected_tool_preference_routes_to_alternative_through_durable_permission_ask()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-preference-ask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog().Add(new PreferenceTool("fixture.protected", ToolProtection.Protected))
                .Add(new PreferenceTool("fixture.alternative", ToolProtection.None, new string('a', 64)));
            var executor = new ScriptedToolExecutor(catalog,
                ScriptedPermissionPolicy.WithTool("fixture.alternative", PermissionDecision.Allow), root,
                boundary: null, audit: new InMemoryAuditSink(),
                interactionResponder: _ => "allow_once", isInteractive: true);
            var requests = new List<ModelRequest>();
            var calls = 0;
            var callId = ToolCallId.New();
            var secondCallId = ToolCallId.New();
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                calls++;
                return calls == 1
                    ? new ModelResponse([
                        new ToolCallBlock(callId, "use-alt", "fixture.alternative", "{}"),
                        new ToolCallBlock(secondCallId, "use-alt-2", "fixture.alternative", "{}")],
                        StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(Path.Combine(root, "artifacts")),
                new InMemoryAuditSink(), new RedactionPolicy(), recordEffectiveFingerprint: true,
                toolPreferences: new Dictionary<string, string> { ["fixture.protected"] = "fixture.alternative" });

            var result = turn.Ask("use preferred alternative", "fixture system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.DoesNotContain("fixture.protected", requests[0].Tools.Select(tool => tool.Name));
            Assert.Contains("fixture.alternative", requests[0].Tools.Select(tool => tool.Name));
            Assert.Single(events.OfType<InteractionRequested>(), request => request.Kind == InteractionKind.Permission);
            Assert.NotEmpty(events.OfType<PermissionGranted>());
            var substitutionApproval = Assert.Single(events.OfType<ToolPreferenceSubstitutionApproved>());
            Assert.Equal("fixture.protected", substitutionApproval.SourceToolId);
            Assert.Equal("fixture.alternative", substitutionApproval.TargetToolId);
            var substitutionAsk = Assert.Single(events.OfType<InteractionRequested>(), request =>
                request.Kind == InteractionKind.Permission);
            Assert.Contains("fixture.protected", substitutionAsk.SubjectJson, StringComparison.Ordinal);
            Assert.Contains("fixture.alternative", substitutionAsk.SubjectJson, StringComparison.Ordinal);
            Assert.Contains(events.OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == callId);
            Assert.Contains(events.OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == secondCallId);

            // A fresh Turn in the same durable Session reuses the prior explicit Ask decision.
            var secondRun = TestRun.Open(store, session, "same protected substitution");
            var secondId = ToolCallId.New();
            var isolatedSessionId = ToolCallId.New();
            var secondTurnCalls = 0;
            var secondExecutor = new ScriptedToolExecutor(catalog,
                ScriptedPermissionPolicy.WithTool("fixture.alternative", PermissionDecision.Allow), root,
                boundary: null, audit: new InMemoryAuditSink(),
                interactionResponder: _ => "allow_once", isInteractive: true);
            var secondTurn = new ExplorerTurn((request, _) =>
            {
                secondTurnCalls++;
                return secondTurnCalls is 1 or 3
                    ? new ModelResponse([new ToolCallBlock(secondTurnCalls == 1 ? secondId : isolatedSessionId,
                        "use-alt-next", "fixture.alternative", "{}")],
                        StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
            }, secondExecutor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(Path.Combine(root, "artifacts-second")),
                new InMemoryAuditSink(), new RedactionPolicy(), recordEffectiveFingerprint: true,
                toolPreferences: new Dictionary<string, string> { ["fixture.protected"] = "fixture.alternative" });
            var secondResult = secondTurn.Ask("use preferred alternative", "fixture system", session,
                secondRun.RunId, secondRun.RootLane, "", CancellationToken.None);
            var allEvents = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(StopReason.EndTurn, secondResult.StopReason);
            Assert.Equal(1, allEvents.OfType<InteractionRequested>().Count(request => request.Kind == InteractionKind.Permission));
            Assert.Contains(allEvents.OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == secondId);

            // Reusing the same ExplorerTurn for another Session must not carry the first Session's receipt.
            var isolatedSession = SessionId.New();
            var isolatedRun = TestRun.Open(store, isolatedSession, "isolated preference session");
            var isolatedResult = secondTurn.Ask("use preferred alternative", "fixture system", isolatedSession,
                isolatedRun.RunId, isolatedRun.RootLane, "", CancellationToken.None);
            var isolatedEvents = store.ReadFrom(isolatedSession, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(StopReason.EndTurn, isolatedResult.StopReason);
            Assert.Single(isolatedEvents.OfType<InteractionRequested>(), request => request.Kind == InteractionKind.Permission);
            Assert.Contains(isolatedEvents.OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == isolatedSessionId);

            // A package-content change invalidates the receipt, even though source/target ids stay put.
            var changedCatalog = new FakeCatalog().Add(new PreferenceTool("fixture.protected", ToolProtection.Protected))
                .Add(new PreferenceTool("fixture.alternative", ToolProtection.None, new string('b', 64)));
            var thirdRun = TestRun.Open(store, session, "changed extension package");
            var thirdId = ToolCallId.New();
            var thirdCalls = 0;
            var thirdTurn = new ExplorerTurn((request, _) =>
            {
                thirdCalls++;
                return thirdCalls == 1
                    ? new ModelResponse([new ToolCallBlock(thirdId, "use-alt-package-change", "fixture.alternative", "{}")],
                        StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
            }, new ScriptedToolExecutor(changedCatalog,
                ScriptedPermissionPolicy.WithTool("fixture.alternative", PermissionDecision.Allow), root,
                boundary: null, audit: new InMemoryAuditSink(),
                interactionResponder: _ => "allow_once", isInteractive: true), changedCatalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(Path.Combine(root, "artifacts-third")),
                new InMemoryAuditSink(), new RedactionPolicy(), recordEffectiveFingerprint: true,
                toolPreferences: new Dictionary<string, string> { ["fixture.protected"] = "fixture.alternative" });
            var thirdResult = thirdTurn.Ask("use preferred alternative", "fixture system", session,
                thirdRun.RunId, thirdRun.RootLane, "", CancellationToken.None);
            var finalEvents = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(StopReason.EndTurn, thirdResult.StopReason);
            Assert.Equal(2, finalEvents.OfType<InteractionRequested>().Count(request => request.Kind == InteractionKind.Permission));
            Assert.Equal(2, finalEvents.OfType<ToolPreferenceSubstitutionApproved>().Count());
            Assert.Contains(finalEvents.OfType<ToolCallSucceeded>(), succeeded => succeeded.ToolCallId == thirdId);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static EffectiveModelPolicy Resolve(ModelPolicyKey key, HarnessPolicy harness,
        UserModelPolicy userPolicy, long revision)
    {
        var stored = new StoredModelPolicy(key, revision, userPolicy,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    private static RunObservation Execute(string workspace, FakeCatalog catalog, EffectiveModelPolicy policy,
        HarnessPolicy harness, IReadOnlyDictionary<string, string>? toolPreferences = null, bool useBoundary = true,
        IReadOnlyDictionary<string, ModelToolCapability>? capabilities = null)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(new EventStream(store, codecs, session), session, "policy fingerprint integration",
            agentProfile: SameAgentProfile);
        var boundary = new ModelCapabilityBoundary(policy, capabilities ?? ModelCapabilityBoundary.CoreTools,
            new FileReadRegistry(workspace));
        var requestToolNames = new List<string>();
        var providerCalls = 0;
        var fingerprint = new ExecutionFingerprint("fixture-model", "same-harness", "legacy-tools",
            "same-context", "same-overrides", "same-build", policy.Fingerprint(), "fixture-tokenizer");
        var turn = new ExplorerTurn((request, _) =>
            {
                providerCalls++;
                requestToolNames.AddRange(request.Tools.Select(tool => tool.Name));
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted-fixture", "fixture-model", null));
            }, new NeverExecuteTool(), catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            fingerprint, new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
            store, codecs, new FileArtifactStore(Path.Combine(workspace, "artifacts-" + Guid.NewGuid().ToString("N"))),
            new InMemoryAuditSink(), new RedactionPolicy(), harness: harness,
            boundary: useBoundary ? boundary : null, recordEffectiveFingerprint: true, toolPreferences: toolPreferences);

        var result = turn.Ask("same prompt", "same system instruction", session, run.RunId,
            run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Equal(1, providerCalls);
        var durableTurn = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
            .OfType<TurnStarted>(), started => started.LaneId == run.RootLane);
        Assert.NotNull(durableTurn.Fingerprint);
        return new RunObservation(session, run.RunId, durableTurn.TurnId,
            requestToolNames.ToArray(), durableTurn.Fingerprint!);
    }

    // ArtifactId identifies a receipt, not the content-addressed configuration identity.
    private static ContentHash Component(ExecutionFingerprint fingerprint, string name) =>
        Assert.Single(fingerprint.Components, component => component.Name == name).Hash;

    private sealed record RunObservation(SessionId SessionId, RunId RunId, TurnId TurnId,
        string[] ToolsSent, ExecutionFingerprint Fingerprint);

    private sealed class NeverExecuteTool : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
            CancellationToken cancellationToken, EventStream stream) =>
            throw new InvalidOperationException("The scripted provider returns EndTurn; no tool should execute.");
    }
}
