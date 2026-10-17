using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class SessionSecurityRuntimeAdmissionTests
{
    private sealed class MonitorLease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    private sealed class PausingMutationTool(ManualResetEventSlim preflightEntered,
        ManualResetEventSlim continuePreflight, string markerPath) : ITool, IReconcilableTool
    {
        private readonly ToolDescriptor _descriptor = new(new ToolId("test.session.delete"),
            "Controlled destructive mutation", new InputSchema("{}"), [], false, true,
            ToolRisk.High, ComponentSource.Core(), ToolProtection.None, EffectClass.NonIdempotent);

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.NonIdempotent,
                new ResourceClaims([], [markerPath], [], null, []), ToolRisk.High, null));

        public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent,
            ToolExecutionContext context, CancellationToken cancellationToken)
        {
            preflightEntered.Set();
            if (!continuePreflight.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                throw new TimeoutException("test did not release preflight");
            return new ReconciliationSpec("before", "after", intent.Intent.ToolCallId.ToString());
        }

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            File.WriteAllText(markerPath, "effect");
            return Task.FromResult(new ToolResult("done", "done", null, 0, false, EffectOutcome.Applied));
        }
    }

    [Fact]
    public async Task Durable_allow_to_ask_change_during_preflight_blocks_started_and_effect_without_approval()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "omni-security-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        using var preflightEntered = new ManualResetEventSlim(false);
        using var continuePreflight = new ManualResetEventSlim(false);
        var markerPath = Path.Combine(workspace, "deleted-marker.txt");
        File.WriteAllText(markerPath, "keep");
        var sessionId = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, sessionId);
        stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(workspace).ToString(), workspace,
            ProfileId.New(), DateTimeOffset.UtcNow));
        var policy = SessionSecurityPolicySet.FromPreset(sessionId, 1, ModelPolicyCategory.FullAgent,
            SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow)
            with { DeletePolicy = DestructiveActionPolicy.Allow, RequirePriorRead = false,
                RequireExpectedVersionToken = false, RequirePostEditValidation = false };
        var initialPolicy = policy;
        var admissionGate = new object();
        stream.Append(policy, DurabilityClass.Barrier);
        SessionSecurityPolicySet? CurrentPolicy() => policy;

        try
        {
            var model = ModelPolicyPresets.FullAgent();
            var modelMutation = new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Allow,
                DestructiveActionPolicy.Allow, 8, 2000, 1.0, requirePriorRead: false,
                requireExpectedVersionToken: false, requirePostEditValidation: false, allowParallelMutations: false);
            model = new UserModelPolicy(model.Category, model.ToolPolicy, modelMutation, model.Source,
                model.Note, model.MaxModelStepsPerTurn, model.MaxToolCallsPerTurn, model.AllowOrchestration,
                model.MaxGenerationSeconds);
            var effective = new EffectiveModelPolicy(ModelPolicyKey.For("test", "session-security"), 1,
                model.Category, model.ToolPolicy, model.MutationPolicy, false, model.MaxModelStepsPerTurn,
                model.MaxToolCallsPerTurn, model.AllowOrchestration, model.MaxGenerationSeconds);
            var boundary = new ModelCapabilityBoundary(effective,
                new Dictionary<string, ModelToolCapability>(ModelCapabilityBoundary.CoreTools)
                { ["test.session.delete"] = ModelToolCapability.DeleteFile },
                new FileReadRegistry(workspace), sessionSecurityPolicy: CurrentPolicy);
            var tool = new PausingMutationTool(preflightEntered, continuePreflight, markerPath);
            var catalog = new FakeCatalog().Add(tool);
            var events = new List<DomainEventPayload>();
            var runtime = ToolRuntime.For(catalog,
                ScriptedPermissionPolicy.WithTool("test.session.delete", PermissionDecision.Allow),
                payload => { stream.Append(payload); events.Add(payload); return VoidBox.Instance; }, boundary);
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("test.session.delete"), "test", "{}");
            var cancellationToken = TestContext.Current.CancellationToken;
            var run = System.Threading.Tasks.Task.Run(() => runtime.Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace,
                    readRegistry: null, acquireSessionSecurityAdmission: () =>
                    { Monitor.Enter(admissionGate); return new MonitorLease(admissionGate); }),
                userApprovesAsk: false, cancellationToken));

            Assert.True(preflightEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken), "ToolRuntime never entered reconciliation preflight.");
            lock (admissionGate)
            {
                policy = policy with { PolicyRevision = 2, DeletePolicy = DestructiveActionPolicy.Ask,
                    Source = SessionSecurityPolicySource.UserSelection, ChangedAt = DateTimeOffset.UtcNow };
                policy.Validate();
                Assert.True(initialPolicy.IsMonotonicTightening(policy));
                stream.Append(policy, DurabilityClass.Barrier);
            }
            continuePreflight.Set();

            var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.False(outcome.Succeeded);
            Assert.False(outcome.FinalState == ToolCallState.Succeeded);
            Assert.Equal("keep", File.ReadAllText(markerPath));
            Assert.DoesNotContain(events, payload => payload is ToolCallStarted);
            Assert.True(store.CurrentSequence(sessionId) >= 2);
            var durablePolicies = store.ReadFrom(sessionId, 1).Select(codecs.Decode)
                .OfType<SessionSecurityPolicySet>().ToArray();
            Assert.Equal([DestructiveActionPolicy.Allow, DestructiveActionPolicy.Ask],
                durablePolicies.Select(item => item.DeletePolicy));
            Assert.Equal([1L, 2L], durablePolicies.Select(item => item.PolicyRevision));
        }
        finally
        {
            continuePreflight.Set();
            Directory.Delete(workspace, recursive: true);
        }
    }
}
