using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class SessionSecurityCompletionGateTests
{
    [Fact]
    public void Observe_only_session_blocks_the_same_host_owned_validator_before_start_or_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-observe-completion-gate-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        var marker = Path.Combine(root, "validator-ran.txt");
        try
        {
            var session = SessionId.New();
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var run = TestRun.Open(stream, session);
            stream.Append(SessionSecurityPolicySet.FromPreset(session, 1,
                ModelPolicyCategory.ObserveOnly, SessionSecurityProvenance.Native,
                SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow), DurabilityClass.Barrier);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var turn = TurnId.New();
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
                stream.Append(new TurnStarted(turn, run.RootLane));

            var (executable, arguments) = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", new[] { "/c", $"echo ran > \"{marker}\"" })
                : ("/bin/sh", new[] { "-c", $"printf ran > '{marker}'" });
            var full = ModelPolicyPresets.FullAgent();
            var effective = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "full-agent"), 1,
                full.Category, full.ToolPolicy, full.MutationPolicy, isFallback: false,
                full.MaxModelStepsPerTurn, full.MaxToolCallsPerTurn, full.AllowOrchestration,
                full.MaxGenerationSeconds);
            var boundary = new ModelCapabilityBoundary(effective, ModelCapabilityBoundary.CoreTools,
                new FileReadRegistry(workspace), sessionSecurityPolicy: () => server.ReadSessionSecurityPolicy(session));
            var artifacts = new FileArtifactStore(Path.Combine(data, "artifacts"));
            var gate = new ConfiguredCompletionGates(new WorkspaceGatesYaml { Build = [executable, .. arguments] },
                workspace, run.RunId, run.RootLane, run.RootTask, null, new InMemoryAuditSink(), artifacts,
                _ => "consent_once", interactive: true, boundary: boundary,
                acquireSessionSecurityAdmission: server.AcquireSessionSecurityAdmission);

            IReadOnlyList<ExternalCompletionGateResult> results;
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
                results = gate.Run(stream, CancellationToken.None);

            Assert.False(Assert.Single(results).Passed);
            var decoded = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var processCall = Assert.Single(decoded.OfType<ToolCallRequested>(), item => item.ToolName == "process.exec");
            Assert.Contains(decoded.OfType<ToolCallRejected>(), item => item.ToolCallId == processCall.ToolCallId
                && item.ErrorCode == ToolErrorCode.CapabilityRefused);
            Assert.DoesNotContain(decoded.OfType<ToolCallStarted>(), item => item.ToolCallId == processCall.ToolCallId);
            Assert.DoesNotContain(decoded.OfType<ToolCallSucceeded>(), item => item.ToolCallId == processCall.ToolCallId);
            Assert.False(File.Exists(marker), "the host-owned validator process must not start under ObserveOnly");
            Assert.Single(decoded.OfType<SessionSecurityPolicySet>());
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scoped_coder_session_allows_host_owned_validator_while_patch_only_model_cannot_dispatch_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-completion-gate-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        try
        {
            var session = SessionId.New();
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var run = TestRun.Open(stream, session);
            var sessionPolicy = SessionSecurityPolicySet.FromPreset(session, 1,
                ModelPolicyCategory.ScopedCoder, SessionSecurityProvenance.Native,
                SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow);
            stream.Append(sessionPolicy, DurabilityClass.Barrier);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var turn = TurnId.New();
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
                stream.Append(new TurnStarted(turn, run.RootLane));

            var model = ModelPolicyPresets.PatchOnly();
            Assert.DoesNotContain(ModelToolCapability.ValidationProcess, model.ToolPolicy.CapabilityCeiling);
            Assert.DoesNotContain(ModelToolCapability.GeneralProcess, model.ToolPolicy.CapabilityCeiling);
            var key = ModelPolicyKey.For("fixture", "patch-only");
            var effective = new EffectiveModelPolicy(key, 1, model.Category, model.ToolPolicy,
                model.MutationPolicy, isFallback: false, model.MaxModelStepsPerTurn,
                model.MaxToolCallsPerTurn, model.AllowOrchestration, model.MaxGenerationSeconds);
            var modelOnlyBoundary = new ModelCapabilityBoundary(effective);
            var modelProcess = modelOnlyBoundary.Evaluate(new ToolIntent(ToolCallId.New(),
                new ToolId("process.exec"), "{}", EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));
            Assert.False(modelProcess.Allowed);
            var boundary = new ModelCapabilityBoundary(effective, ModelCapabilityBoundary.CoreTools,
                new FileReadRegistry(workspace), sessionSecurityPolicy: () => server.ReadSessionSecurityPolicy(session));
            var artifacts = new FileArtifactStore(Path.Combine(data, "artifacts"));
            var gate = new ConfiguredCompletionGates(new WorkspaceGatesYaml { Build = ["dotnet", "--version"] },
                workspace, run.RunId, run.RootLane, run.RootTask, null, new InMemoryAuditSink(), artifacts,
                _ => "consent_once", interactive: true, boundary: boundary,
                acquireSessionSecurityAdmission: server.AcquireSessionSecurityAdmission);

            IReadOnlyList<ExternalCompletionGateResult> results;
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
                results = gate.Run(stream, CancellationToken.None);

            Assert.True(Assert.Single(results).Passed, Assert.Single(results).Summary);
            var decoded = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var call = Assert.Single(decoded.OfType<ToolCallRequested>(), item => item.ToolName == "process.exec");
            Assert.Contains(decoded.OfType<ToolCallSucceeded>(), item => item.ToolCallId == call.ToolCallId);
            var persistedPolicy = Assert.Single(decoded.OfType<SessionSecurityPolicySet>());
            Assert.Equal(sessionPolicy.PolicyRevision, persistedPolicy.PolicyRevision);
            Assert.Equal(sessionPolicy.ToUserModelPolicy(), persistedPolicy.ToUserModelPolicy());
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
