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

    private static EffectiveModelPolicy Resolve(ModelPolicyKey key, HarnessPolicy harness,
        UserModelPolicy userPolicy, long revision)
    {
        var stored = new StoredModelPolicy(key, revision, userPolicy,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    private static RunObservation Execute(string workspace, FakeCatalog catalog, EffectiveModelPolicy policy,
        HarnessPolicy harness)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.Open(new EventStream(store, codecs, session), session, "policy fingerprint integration");
        var boundary = new ModelCapabilityBoundary(policy, ModelCapabilityBoundary.CoreTools,
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
            boundary: boundary, recordEffectiveFingerprint: true);

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
