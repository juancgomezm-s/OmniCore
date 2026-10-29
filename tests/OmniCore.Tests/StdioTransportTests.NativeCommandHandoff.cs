using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Cli;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    private sealed class UnavailableStrongProbeForWorkflowTest : ISandboxCapabilitiesProbe
    {
        public SandboxCapabilities Probe() => new(false, true, true, "test: Strong unavailable before launch");
    }

    private sealed class RecordingSandboxCapabilitiesProbeForWorkflowTest(ISandboxCapabilitiesProbe inner,
        ConcurrentQueue<string> observations) : ISandboxCapabilitiesProbe
    {
        public SandboxCapabilities Probe()
        {
            var capabilities = inner.Probe();
            observations.Enqueue($"strong={capabilities.StrongAvailable};weak={capabilities.WeakAvailable};none={capabilities.NoneAvailable}");
            return capabilities;
        }
    }

    private sealed record WorkflowProcessBoundaryObservation(int Launch, string Operation, long ElapsedMs,
        string? RequestedStrength = null, string? Result = null, string? ExceptionType = null,
        int? ExitCode = null, bool? TimedOut = null, int? OutputChunks = null, int? OutputCharacters = null);

    private sealed class RecordingWorkflowSandboxLauncher(ISandboxProcessLauncher inner,
        ConcurrentQueue<WorkflowProcessBoundaryObservation> observations) : ISandboxProcessLauncher, IStrongSandboxPreflight
    {
        private int _launchNumber;
        private int _observationCount;

        public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = inner is IStrongSandboxPreflight preflight
                    ? preflight.CheckStrongAvailability(launch)
                    : StrongSandboxPreflightResult.Unknown;
                Record(new WorkflowProcessBoundaryObservation(Math.Max(1, Volatile.Read(ref _launchNumber) + 1),
                    "preflight.return", stopwatch.ElapsedMilliseconds, launch.RequestedStrength.ToString(), result.ToString()));
                return result;
            }
            catch (Exception exception)
            {
                Record(new WorkflowProcessBoundaryObservation(Math.Max(1, Volatile.Read(ref _launchNumber) + 1),
                    "preflight.throw", stopwatch.ElapsedMilliseconds, launch.RequestedStrength.ToString(),
                    ExceptionType: exception.GetType().Name));
                throw;
            }
        }

        public async ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _launchNumber);
            var stopwatch = Stopwatch.StartNew();
            Record(new WorkflowProcessBoundaryObservation(number, "start.begin", 0,
                launch.RequestedStrength.ToString()));
            try
            {
                var control = await inner.StartAsync(launch, cancellationToken).ConfigureAwait(false);
                Record(new WorkflowProcessBoundaryObservation(number, "start.return", stopwatch.ElapsedMilliseconds,
                    launch.RequestedStrength.ToString()));
                return new RecordingWorkflowSandboxControl(control, number, launch.RequestedStrength.ToString(), Record);
            }
            catch (Exception exception)
            {
                Record(new WorkflowProcessBoundaryObservation(number, "start.throw", stopwatch.ElapsedMilliseconds,
                    launch.RequestedStrength.ToString(), ExceptionType: exception.GetType().Name));
                throw;
            }
        }

        private void Record(WorkflowProcessBoundaryObservation observation)
        {
            if (Interlocked.Increment(ref _observationCount) <= 64) observations.Enqueue(observation);
        }

        private sealed class RecordingWorkflowSandboxControl(ISandboxProcessControl inner, int launch,
            string requestedStrength,
            Action<WorkflowProcessBoundaryObservation> record) : ISandboxProcessControl
        {
            public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
            {
                var stopwatch = Stopwatch.StartNew();
                record(new WorkflowProcessBoundaryObservation(launch, "wait.begin", 0, requestedStrength));
                try
                {
                    var output = await inner.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    record(new WorkflowProcessBoundaryObservation(launch, "wait.return", stopwatch.ElapsedMilliseconds,
                        requestedStrength,
                        ExitCode: output.ExitCode, TimedOut: output.TimedOut,
                        OutputChunks: output.Chunks.Count, OutputCharacters: output.Chunks.Sum(chunk => chunk.Text.Length)));
                    return output;
                }
                catch (Exception exception)
                {
                    record(new WorkflowProcessBoundaryObservation(launch, "wait.throw", stopwatch.ElapsedMilliseconds,
                        requestedStrength,
                        ExceptionType: exception.GetType().Name));
                    throw;
                }
            }

            public async ValueTask TerminateAsync(CancellationToken cancellationToken)
            {
                var stopwatch = Stopwatch.StartNew();
                record(new WorkflowProcessBoundaryObservation(launch, "terminate.begin", 0, requestedStrength));
                try
                {
                    await inner.TerminateAsync(cancellationToken).ConfigureAwait(false);
                    record(new WorkflowProcessBoundaryObservation(launch, "terminate.return", stopwatch.ElapsedMilliseconds,
                        requestedStrength));
                }
                catch (Exception exception)
                {
                    record(new WorkflowProcessBoundaryObservation(launch, "terminate.throw", stopwatch.ElapsedMilliseconds,
                        requestedStrength,
                        ExceptionType: exception.GetType().Name));
                    throw;
                }
            }

            public async ValueTask DisposeAsync()
            {
                var stopwatch = Stopwatch.StartNew();
                record(new WorkflowProcessBoundaryObservation(launch, "dispose.begin", 0, requestedStrength));
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                    record(new WorkflowProcessBoundaryObservation(launch, "dispose.return", stopwatch.ElapsedMilliseconds,
                        requestedStrength));
                }
                catch (Exception exception)
                {
                    record(new WorkflowProcessBoundaryObservation(launch, "dispose.throw", stopwatch.ElapsedMilliseconds,
                        requestedStrength,
                        ExceptionType: exception.GetType().Name));
                    throw;
                }
            }
        }
    }

    private sealed class UnavailableStrongProcessLauncherForHumanShellTest(ISandboxProcessLauncher inner)
        : ISandboxProcessLauncher, IStrongSandboxPreflight
    {
        public SandboxStrength? LastRequestedStrength { get; private set; }

        public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch) =>
            StrongSandboxPreflightResult.UnavailableBeforeLaunch;

        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            LastRequestedStrength = launch.RequestedStrength;
            return inner.StartAsync(launch, cancellationToken);
        }
    }

    [Fact]
    public async Task Native_workflow_weak_then_natural_launch_preserves_process_and_publication_cleanup()
    {
        await Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it(
            forceStrongUnavailableBeforeLaunch: true, exerciseTimeoutCleanup: false);
        await Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it(
            forceStrongUnavailableBeforeLaunch: false, exerciseTimeoutCleanup: false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it(
        bool forceStrongUnavailableBeforeLaunch, bool exerciseTimeoutCleanup)
    {
        var invocationId = Guid.NewGuid().ToString("D");
        var program = "public static class Program\n{\n    public static int Main()\n    {\n        /* FEATURE_MODULE_HOOK */\n        return 0;\n    }\n}\n";
        const string implementation = "public static class FeatureModule { public static bool IsInitialized { get; private set; } public static void Initialize() => IsInitialized = true; }\n";
        var programVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(program))).ToLowerInvariant();
        var fixtureWorkspace = string.Empty;
        var ordinaryLockBasenameFile = new byte[] { 0x4F, 0x4D, 0x4E, 0x49, 0x2D, 0x46, 0x49, 0x58, 0x54, 0x55, 0x52, 0x45 };
        var ordinaryEmptyLockBasenamePath = Path.Combine("config", "subdir", ".artifact-gc.lease");
        NativeCommandHandoffFixture? responseAuditFixture = null;
        var invocationReceiptHeld = new TaskCompletionSource<PersistedConversationCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInvocationReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlledProviderHoldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandboxCapabilityObservations = new ConcurrentQueue<string>();
        var processLauncherObservations = new ConcurrentQueue<WorkflowProcessBoundaryObservation>();
        Func<ISandboxCapabilitiesProbe>? workflowSandboxProbe = forceStrongUnavailableBeforeLaunch
            ? static () => new UnavailableStrongProbeForWorkflowTest()
            : () => new RecordingSandboxCapabilitiesProbeForWorkflowTest(
                OperatingSystem.IsWindows() ? new WindowsAppContainerCapabilitiesProbe()
                    : OperatingSystem.IsLinux() ? new LinuxBubblewrapCapabilitiesProbe()
                    : new NoIsolationSandboxCapabilitiesProbe(), sandboxCapabilityObservations);
        async Task HoldInvocationAfterReceiptAsync(PersistedConversationCommand binding)
        {
            if (!string.Equals(binding.CommandId, invocationId, StringComparison.Ordinal)) return;
            invocationReceiptHeld.TrySetResult(binding);
            await releaseInvocationReceipt.Task.WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        string WorkflowResponse(int index, string _)
        {
            if (exerciseTimeoutCleanup && index == 1)
            {
                controlledProviderHoldEntered.TrySetResult();
                Thread.Sleep(TimeSpan.FromMilliseconds(750));
            }
            // A real provider round-trip can exceed the historical 30 s test-only gate under
            // loaded Windows CI. Keep one deterministic delay within the already-declared
            // two-minute workflow deadline; this is fixture latency, not a new model request.
            if (!forceStrongUnavailableBeforeLaunch && index == 1)
                Thread.Sleep(TimeSpan.FromSeconds(35));
            var response = index switch
            {
                0 => CliEndToEndTests.ResponsesTextResponse("principal workflow context"),
                1 => CliEndToEndTests.ResponsesToolCallResponse("explore-entrypoint", "filesystem.read",
                    "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                2 => CliEndToEndTests.ResponsesTextResponse("EXPLORE_RESULT_EVIDENCE: src/FeatureApp/Program.cs is the executable entry point and contains FEATURE_MODULE_HOOK."),
                3 => CliEndToEndTests.ResponsesToolCallResponse("implement-read-entrypoint", "filesystem.read",
                    "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                4 => CliEndToEndTests.ResponsesToolCallResponse("implement-module", "filesystem.write",
                    JsonSerializer.Serialize(new { path = "src/FeatureApp/FeatureModule.cs", content = implementation })),
                5 => CliEndToEndTests.ResponsesToolCallResponse("implement-connect", "filesystem.patch",
                    JsonSerializer.Serialize(new { path = "src/FeatureApp/Program.cs", expectedVersion = programVersion,
                        oldText = "/* FEATURE_MODULE_HOOK */", newText = "FeatureModule.Initialize();" })),
                6 => CliEndToEndTests.ResponsesToolCallResponse("implement-review", "filesystem.read",
                    "{\"path\":\"src/FeatureApp/Program.cs\"}"),
                7 => CliEndToEndTests.ResponsesTextResponse("IMPLEMENT_RESULT_EVIDENCE: FeatureModule.Initialize is connected from the application entry point."),
                8 => CliEndToEndTests.ResponsesToolCallResponse("verify-integration", "dyn.core.verify_integration",
                    JsonSerializer.Serialize(new
                    {
                        executable = new SystemExecutableResolver().Resolve("dotnet", fixtureWorkspace).ResolvedPath,
                        argv = new[] { "--version" }, cwd = fixtureWorkspace, timeoutSeconds = 120,
                    })),
                9 => CliEndToEndTests.ResponsesTextResponse("VERIFY_RESULT_EVIDENCE: the declared integration check passed."),
                _ => CliEndToEndTests.ResponsesTextResponse("VERIFY_RESULT_EVIDENCE: the declared integration check passed."),
            };
            responseAuditFixture?.RecordProviderResponse(index, response);
            return response;
        }

        await using var fixture = await NativeCommandHandoffFixture.StartAsync(string.Empty,
            includeWorkflowAgentProfile: true, providerResponse: WorkflowResponse,
            // Exercise the explicit Weak consent route, not Autonomous's per-call fallback.
            // Permit the fixture's exact file operations without introducing a separate Ask.
            permissionsYaml: "profile: balanced\nrules:\n  filesystem.write: allow\n  filesystem.patch: allow\n  dyn.core.verify_integration: allow\n",
            includeZeroModelPricing: true, isolateAgentRoots: true, workflowModel: true,
            includeNativeWeakSandboxCapability: true,
            workflowSandboxCapabilitiesProbeForTests: workflowSandboxProbe,
            workflowProcessLauncherDecoratorForTests: inner =>
                new RecordingWorkflowSandboxLauncher(inner, processLauncherObservations),
            afterCommandExecutionReceiptForTests: HoldInvocationAfterReceiptAsync);
        try
        {
        fixtureWorkspace = fixture.Workspace;
        responseAuditFixture = fixture;
        fixture.PreserveRootOnDispose = true;
        fixture.PreserveEvidenceOnDispose = true;
        File.WriteAllBytes(Path.Combine(fixture.RootDirectory, "config", ".artifact-gc.lease"), ordinaryLockBasenameFile);
        var ordinaryEmptyLockBasenameFullPath = Path.Combine(fixture.RootDirectory, ordinaryEmptyLockBasenamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(ordinaryEmptyLockBasenameFullPath)!);
        File.WriteAllBytes(ordinaryEmptyLockBasenameFullPath, Array.Empty<byte>());
        InitializeWorkflowGitRepository(fixture.Workspace, program);
        var initialHead = RunFixtureGit(fixture.Workspace, "rev-parse", "HEAD");
        var initialTree = RunFixtureGit(fixture.Workspace, "rev-parse", "HEAD^{tree}");
        var worktreeData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), fixture.Workspace);
        var ownedTreesRoot = Path.Combine(worktreeData, "worktrees");
        Assert.False(Directory.Exists(ownedTreesRoot));

        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        var initialScope = fixture.QueryJson("humanActionScope");
        var runId = initialScope.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup conversation did not create a Run.");
        var eventsBefore = fixture.ReadEvents(sessionId);
        Assert.DoesNotContain(eventsBefore, item => item.Payload is LaneWorktreeCreated or LaneWorktreeBound);
        Assert.True(fixture.QueryJson("sessionSecurity").GetProperty("allowOrchestration").GetBoolean());

        var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select", runId, mode = "orq",
            expectedRevision = initialScope.GetProperty("revision").GetInt64(),
            effort = "ultracode", adaptive = true,
            allowedModes = "plan,act,orq", maxAgents = 4, maxDepth = 3, maxTurns = 12,
            maxToolCalls = 36, maxElapsedSeconds = 300, maxSpendUsd = 5m,
        }));
        var modeAck = Ack(fixture.ExchangeTrusted(mode, sessionId, runId));
        Assert.True(modeAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Act selection failed: status={modeAck.Status}, error={modeAck.Error}, reason={modeAck.Outcome?.Reason}");
        fixture.RecordProtocolAudit("run.mode.select", sessionId, runId, "accepted; exact revision; HMAC redacted");
        var catalog = fixture.QueryJson("command.catalog");
        var workflow = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:orq-auth");
        Assert.True(workflow.GetProperty("enabled").GetBoolean(),
            "The compiled workflow must be enabled by the signed Orchestrate authorization.");
        var humanShell = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:shell.exec");
        Assert.True(humanShell.GetProperty("enabled").GetBoolean(),
            "A quiescent Run with explicit Orchestrate authorization retains the human shell command entry.");

        var stableSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var currentRevision = fixture.QueryJson("humanActionScope").GetProperty("revision").GetInt64();
        var staleMode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select", runId, mode = "plan", expectedRevision = currentRevision - 1,
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected,
            Ack(fixture.ExchangeTrusted(staleMode, sessionId, runId)).Outcome?.Kind);
        Assert.Equal(stableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal("orq", fixture.QueryJson("humanActionScope").GetProperty("mode").GetString());
        fixture.RecordProtocolAudit("run.mode.select stale revision", sessionId, runId, "rejected; journal unchanged; HMAC redacted");

        var wrongSessionProof = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select", runId, mode = "plan", expectedRevision = currentRevision,
        }));
        var wrongSessionAck = Ack(fixture.ExchangeTrustedWithProofScope(wrongSessionProof,
            fixture.Workspace, Guid.NewGuid().ToString("D"), runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongSessionAck.Outcome?.Kind);
        fixture.RecordProtocolAudit("run.mode.select wrong Session proof", sessionId, runId, "rejected; journal unchanged; HMAC redacted");
        var wrongWorkspace = Path.Combine(fixture.RootDirectory, "foreign-workspace");
        Directory.CreateDirectory(wrongWorkspace);
        var wrongWorkspaceAck = Ack(fixture.ExchangeTrustedWithProofScope(wrongSessionProof,
            wrongWorkspace, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongWorkspaceAck.Outcome?.Kind);
        fixture.RecordProtocolAudit("run.mode.select wrong workspace proof", sessionId, runId, "rejected; journal unchanged; HMAC redacted");
        Assert.Equal(stableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.False(StdioProtocol.IsSupportedCommand("delegation.create"));
        fixture.RecordProtocolAudit("delegation.create raw protocol command", sessionId, runId,
            "not supported by existing stdio allowlist; no command dispatched");

        var args = new[]
        {
            "Connect FeatureModule to the application entry point",
            "--verify-executable", new SystemExecutableResolver().Resolve("dotnet", fixture.Workspace).ResolvedPath,
            "--verify-argv-json", "[\"--version\"]",
        };
        var invocation = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = workflow.GetProperty("canonicalCommandId").GetString(),
            arguments = args,
            expectedSessionId = sessionId,
            expectedRunId = runId,
            expectedModeRevision = catalog.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
            catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = workflow.GetProperty("sourceFingerprint").GetString(),
        }));
        var admitted = Ack(fixture.ExchangeTrusted(invocation, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, admitted.Outcome?.Kind);
        Assert.Equal(invocationId, admitted.CommandId);
        fixture.RecordProtocolAudit("command.invoke.user core:orq-auth", sessionId, runId,
            "accepted; exact catalog fingerprint and revisions; HMAC redacted");
        if (exerciseTimeoutCleanup)
        {
            await controlledProviderHoldEntered.Task.WaitAsync(TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            var controlledTimeout = await Assert.ThrowsAsync<TimeoutException>(() =>
                WaitForWorkflowInvocationAsync(fixture, sessionId, runId, invocationId,
                    invocationReceiptHeld.Task, releaseInvocationReceipt, sandboxCapabilityObservations,
                    processLauncherObservations, TimeSpan.FromMilliseconds(100)));
            Assert.Contains("runnerQuiesced=true", controlledTimeout.Message, StringComparison.Ordinal);
            Assert.Contains("timeoutSnapshotJson=", controlledTimeout.Message, StringComparison.Ordinal);
            Assert.Contains("\"recentEvents\"", controlledTimeout.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("principal workflow context", controlledTimeout.Message, StringComparison.Ordinal);
            var cancelledReceipt = fixture.ReadReceipt(invocationId);
            Assert.Contains(cancelledReceipt?.ExecutionState, new[] { "failed", "cancelled", "unknown" });
            fixture.RecordProtocolAudit("controlled workflow timeout diagnostic", sessionId, runId,
                "owned Run cancelled and terminal before evidence capture; metadata emitted in bounded test failure");
            return;
        }

        var weakConsentResponded = await WaitForWorkflowInvocationAsync(fixture, sessionId, runId, invocationId,
            invocationReceiptHeld.Task, releaseInvocationReceipt, sandboxCapabilityObservations,
            processLauncherObservations);
        if (forceStrongUnavailableBeforeLaunch) Assert.True(weakConsentResponded);

        var eventsAfterWorkflow = fixture.ReadEvents(sessionId);
        var workflowRun = RunId.Parse(runId);
        var workflowJournal = eventsAfterWorkflow.Where(item => item.Envelope.RunId == workflowRun)
            .Select(item => item.Envelope).ToArray();
        var codecs = EventCodecs.Create();
        var implementPlan = Assert.Single(PlanProjection.Replay(codecs, workflowJournal).Items(), plan =>
            plan.Metadata.GetValueOrDefault(WorkflowStageContract.StageKey) == "Implement");
        Assert.Equal(PlanItemState.Completed, implementPlan.State);
        var verifyPlan = Assert.Single(PlanProjection.Replay(codecs, workflowJournal).Items(), plan =>
            plan.Metadata.GetValueOrDefault(WorkflowStageContract.StageKey) == "Verify");
        Assert.Equal(PlanItemState.Completed, verifyPlan.State);
        var verifierRequest = Assert.Single(eventsAfterWorkflow, item => item.Payload is ToolCallRequested request
            && request.ToolName == "dyn.core.verify_integration");
        var verifierToolCall = Assert.IsType<ToolCallRequested>(verifierRequest.Payload);
        var verifierSuccess = Assert.Single(eventsAfterWorkflow, item => item.Payload is ToolCallSucceeded success
            && success.ToolCallId == verifierToolCall.ToolCallId);
        Assert.Contains("Proceso terminó con código 0", Assert.IsType<ToolCallSucceeded>(verifierSuccess.Payload).ResultJson);
        var weakConsentRequests = eventsAfterWorkflow.Where(item => item.Payload is InteractionRequested request
            && request.Kind == InteractionKind.WeakSandboxConsent).ToArray();
        Assert.Equal(weakConsentResponded ? 1 : 0, weakConsentRequests.Length);
        if (weakConsentResponded)
        {
            var weakConsent = Assert.IsType<InteractionRequested>(weakConsentRequests[0].Payload);
            var weakConsentResolved = Assert.Single(eventsAfterWorkflow, item => item.Payload is InteractionResolved response
                && response.InteractionId == weakConsent.InteractionId);
            Assert.Equal("consent_once", Assert.IsType<InteractionResolved>(weakConsentResolved.Payload).OptionId);
        }
        else
        {
            Assert.False(forceStrongUnavailableBeforeLaunch);
            Assert.DoesNotContain(eventsAfterWorkflow, item => item.Payload is InteractionResolved response
                && response.OptionId == "consent_once");
        }
        var implementationTask = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is PlanItemLinked link && link.PlanItemId == implementPlan.Id)
            .Payload is PlanItemLinked linked ? linked.Link.TaskId : throw new InvalidDataException("Implement stage has no linked Task.");
        var worktreeEventEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Envelope.TaskId == implementationTask && item.Payload is LaneWorktreeCreated);
        var binding = Assert.IsType<LaneWorktreeCreated>(worktreeEventEnvelope.Payload);
        var taskEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is TaskCreated task && task.TaskId == implementationTask);
        var task = Assert.IsType<TaskCreated>(taskEnvelope.Payload);
        var laneEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is LaneCreated lane && lane.TaskId == implementationTask && lane.LaneId == binding.LaneId);
        var laneCreated = Assert.IsType<LaneCreated>(laneEnvelope.Payload);
        var delegationEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is DelegationCreated created && created.Delegation.ChildTaskId == implementationTask
                && created.Delegation.ChildLaneId == binding.LaneId);
        var delegation = Assert.IsType<DelegationCreated>(delegationEnvelope.Payload).Delegation;
        var executionEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is AgentExecutionStarted started && started.LaneId == binding.LaneId
                && started.ExecutionId == binding.ExecutionId);
        Assert.IsType<AgentExecutionStarted>(executionEnvelope.Payload);
        var provisioningEnvelope = Assert.Single(eventsAfterWorkflow, item => item.Envelope.RunId == workflowRun
            && item.Payload is LaneProvisioning provisioning && provisioning.LaneId == binding.LaneId);
        Assert.True(provisioningEnvelope.Envelope.Sequence < worktreeEventEnvelope.Envelope.Sequence);
        Assert.Equal(implementationTask, task.TaskId);
        Assert.Equal(workflowRun, task.RunId);
        Assert.Equal(implementationTask, laneCreated.TaskId);
        Assert.Equal(binding.LaneId, laneCreated.LaneId);
        Assert.Equal(binding.TaskId, delegation.ChildTaskId);
        Assert.Equal(binding.LaneId, delegation.ChildLaneId);
        Assert.Equal(binding.ExecutionId, worktreeEventEnvelope.Envelope.ExecutionId);
        Assert.Equal(sessionId, worktreeEventEnvelope.Envelope.SessionId.ToString());
        Assert.Equal(runId, worktreeEventEnvelope.Envelope.RunId?.ToString());
        Assert.Equal(binding.TaskId, worktreeEventEnvelope.Envelope.TaskId);
        Assert.Equal(binding.LaneId, worktreeEventEnvelope.Envelope.LaneId);
        Assert.True(Directory.Exists(binding.WorktreePath));
        Assert.Equal(binding.SnapshotCommit, RunFixtureGit(binding.WorktreePath, "rev-parse", "HEAD"));
        Assert.Equal(initialHead, RunFixtureGit(binding.WorktreePath, "rev-parse", "HEAD^"));
        Assert.Equal(initialTree, RunFixtureGit(binding.WorktreePath, "rev-parse", "HEAD^{tree}"));
        Assert.Equal(initialTree, binding.SnapshotTree);
        Assert.Equal(initialHead, RunFixtureGit(fixture.Workspace, "rev-parse", "HEAD"));
        Assert.Contains(binding.TaskPacketRef, worktreeEventEnvelope.Envelope.ArtifactRefs);
        Assert.StartsWith(Path.GetFullPath(ownedTreesRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(binding.WorktreePath), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        var artifactStore = OmniHost.CreateArtifactStore(worktreeData);
        Assert.Equal("application/vnd.omnicore.task-packet+json", binding.TaskPacketRef.MediaType);
        Assert.Equal(Sensitivity.Sensitive, binding.TaskPacketRef.Sensitivity);
        Assert.InRange(binding.TaskPacketRef.Size, 1L, 1024L * 1024L);
        Assert.True(artifactStore.Verify(binding.TaskPacketRef.Hash, binding.TaskPacketRef.Size));
        var packetJson = artifactStore.GetText(binding.TaskPacketRef.Hash)
            ?? throw new InvalidDataException("The owned TaskPacket artifact has no text payload.");
        var packet = JsonSerializer.Deserialize(packetJson, TaskPacketJsonContext.Default.TaskPacket)
            ?? throw new InvalidDataException("The owned TaskPacket artifact did not decode.");
        Assert.Equal(sessionId, packet.SessionId.ToString());
        Assert.Equal(runId, packet.RunId.ToString());
        Assert.Equal(binding.TaskId, packet.TaskId);
        Assert.Equal(binding.LaneId, packet.LaneId);
        Assert.Equal(binding.ExecutionId, packet.ExecutionId);
        Assert.Equal(delegation.DelegationId, packet.DelegationId);
        Assert.Equal(delegation.PacketRef, packet.SelectedProjectionRef);
        Assert.Equal(initialHead, packet.HeadCommit);
        Assert.Equal(binding.OwnershipId, packet.OwnershipId);
        Assert.Equal(Path.GetFullPath(binding.RepositoryRoot), Path.GetFullPath(packet.RepositoryRoot));
        Assert.Equal(Path.GetFullPath(binding.GitCommonDirectory), Path.GetFullPath(packet.GitCommonDirectory));
        Assert.Equal(Path.GetFullPath(binding.WorktreePath), Path.GetFullPath(packet.WorktreePath));
        Assert.Equal(binding.SnapshotCommit, packet.SnapshotCommit);
        Assert.Equal(binding.SnapshotTree, packet.SnapshotTree);
        Assert.Equal(WorktreeBase.SnapshotOfWorkingTree, packet.Base);
        Assert.Equal(implementPlan.Metadata.GetValueOrDefault(WorkflowStageContract.ProfileKey), packet.ProfileId.ToString());
        Assert.Equal(binding.TaskPacketRef.Hash, Assert.IsType<LaneWorktreeCreated>(
            eventsAfterWorkflow.Single(item => item.Payload is LaneWorktreeCreated value
                && value.LaneId == binding.LaneId).Payload).TaskPacketRef.Hash);
        Assert.True(Directory.Exists(ownedTreesRoot));
        Assert.Contains(Directory.EnumerateDirectories(ownedTreesRoot, "*", SearchOption.AllDirectories),
            path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(binding.WorktreePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        var sequenceBeforeQueries = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var agents = fixture.QueryJson("agents");
        var lane = Assert.Single(agents.GetProperty("lanes").EnumerateArray(), item =>
            item.GetProperty("laneId").GetString() == binding.LaneId.ToString());
        Assert.Equal(binding.TaskId.ToString(), lane.GetProperty("taskId").GetString());
        var preview = fixture.QueryJson("worktree.preview:" + binding.LaneId);
        Assert.Equal(binding.OwnershipId, preview.GetProperty("ownershipId").GetString());
        Assert.Equal(binding.LaneId.ToString(), preview.GetProperty("laneId").GetString());
        Assert.True(preview.GetProperty("applicable").GetBoolean());
        var foreignLane = fixture.QueryJson("worktree.preview:" + LaneId.New());
        Assert.Equal(JsonValueKind.Null, foreignLane.ValueKind);
        Assert.Equal(sequenceBeforeQueries, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal(1, eventsAfterWorkflow.Count(item => item.Envelope.RunId == workflowRun
            && item.Payload is NativeCommandInvocationStarted started && started.InvocationId.ToString() == invocationId));
        Assert.Equal(1, eventsAfterWorkflow.Count(item => item.Envelope.RunId == workflowRun
            && item.Payload is NativeCommandInvocationFinished finished && finished.InvocationId.ToString() == invocationId));
        Assert.Equal(10, fixture.Provider.RequestCount);

        var processBoundaries = processLauncherObservations.ToArray();
        var successfulStarts = processBoundaries.Where(item => item.Operation == "start.return").ToArray();
        Assert.Single(successfulStarts);
        if (!forceStrongUnavailableBeforeLaunch) Assert.NotEmpty(sandboxCapabilityObservations);
        var expectedStrength = weakConsentResponded ? nameof(SandboxStrength.Weak) : nameof(SandboxStrength.Strong);
        Assert.Equal(expectedStrength, successfulStarts[0].RequestedStrength);
        if (weakConsentResponded)
            Assert.Contains(processBoundaries, item => item.Operation == "preflight.return"
                && item.Result == nameof(StrongSandboxPreflightResult.UnavailableBeforeLaunch));
        else
            Assert.Contains(processBoundaries, item => item.Operation == "preflight.return"
                && item.Result == nameof(StrongSandboxPreflightResult.Available));
        Assert.Contains(processBoundaries, item => item.Operation == "wait.return" && item.ExitCode == 0
            && item.TimedOut == false);
        var boundaryJson = JsonSerializer.Serialize(new
        {
            fixtureForcedStrongUnavailableBeforeLaunch = forceStrongUnavailableBeforeLaunch,
            sandboxCapabilities = sandboxCapabilityObservations.ToArray(),
            processBoundaries,
        });
        Assert.InRange(Encoding.UTF8.GetByteCount(boundaryJson), 1, 12 * 1024);
        fixture.RecordProtocolAudit("workflow verifier launcher boundaries", sessionId, runId,
            "scope=invocation:" + invocationId + "; observations=" + boundaryJson);

        fixture.RecordProtocolAudit("worktree.preview", sessionId, runId, "passive; no journal delta");
        await fixture.StopHostAsync();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ArtifactSnapshotLeaseContendedForTests = () => captureStarted.TrySetResult();
        var workspaceArtifactLeasePath = Path.Combine(worktreeData, ".artifact-gc.lease");
        using (var heldFixtureLease = new FileStream(workspaceArtifactLeasePath, FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, heldFixtureLease.Length);
            var captureTask = Task.Run(() => fixture.PreserveEvidence("workflow-provision"));
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var completedBeforeRelease = await Task.WhenAny(captureTask,
                Task.Delay(100, TestContext.Current.CancellationToken)) == captureTask;
            heldFixtureLease.Dispose();
            var artifactDirectory = await captureTask.WaitAsync(TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.False(completedBeforeRelease,
                "Evidence capture must wait while a fixture-owned artifact publication lease is held.");
            var capturedLeasePath = Path.Combine(artifactDirectory, "fixture",
                Path.GetRelativePath(fixture.RootDirectory, workspaceArtifactLeasePath));
            Assert.False(File.Exists(capturedLeasePath),
                "The empty publication lock file is ephemeral coordination state and is excluded from the quiescent snapshot.");
            Assert.Equal(ordinaryLockBasenameFile,
                File.ReadAllBytes(Path.Combine(artifactDirectory, "fixture", "config", ".artifact-gc.lease")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(artifactDirectory, "fixture", ordinaryEmptyLockBasenamePath)));
            fixture.PreserveRootOnDispose = false;
            Assert.True(Directory.Exists(Path.Combine(artifactDirectory, "fixture", "workspace", ".git")));
        }
        }
        catch (Exception testFailure)
        {
            fixture.RecordPrimaryTestFailure(testFailure);
            throw;
        }
    }

    private static async Task<bool> WaitForWorkflowInvocationAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string runId, string invocationId, Task<PersistedConversationCommand> invocationReceiptHeld,
        TaskCompletionSource releaseInvocationReceipt, ConcurrentQueue<string> sandboxCapabilityObservations,
        ConcurrentQueue<WorkflowProcessBoundaryObservation> processLauncherObservations,
        TimeSpan? timeoutForTest = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeoutForTest ?? TimeSpan.FromMinutes(2));
        var weakSandboxConsentResponded = false;
        PersistedConversationCommand heldReceipt;
        try
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The workflow exceeded its existing two-minute deadline before the post-receipt gate.");
            heldReceipt = await invocationReceiptHeld.WaitAsync(remaining,
                TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            releaseInvocationReceipt.TrySetResult();
            var missingReceipt = fixture.ReadReceipt(invocationId);
            var timeoutSnapshot = fixture.CaptureWorkflowTimeoutSnapshot(sessionId, invocationId,
                sandboxCapabilityObservations, processLauncherObservations);
            var cancellation = await fixture.CancelOwnedWorkflowAndWaitAsync(sessionId, runId, invocationId,
                    releaseInvocationReceipt).ConfigureAwait(false);
            if (!cancellation.Quiesced)
            {
                fixture.PreserveEvidenceOnDispose = false;
                fixture.PreserveRootOnDispose = true;
            }
            throw new TimeoutException("The target invocation did not reach the test-only post-receipt gate. "
                + $"receipt={missingReceipt?.State}/{missingReceipt?.OutcomeKind}/"
                + $"{missingReceipt?.ExecutionState}/{missingReceipt?.ExecutionCode}; "
                + $"providerRequests={fixture.Provider.RequestCount}; diagnostics={string.Join(",", fixture.Diagnostics.TakeLast(8))}; "
                + $"snapshotPath={timeoutSnapshot.Path}; timeoutSnapshotJson={timeoutSnapshot.Json}; "
                + $"cancelSend={cancellation.SendStatus}; cancelAck={cancellation.AckOutcome}; runnerQuiesced={cancellation.Quiesced.ToString().ToLowerInvariant()}.");
        }
        var eventsAtReceipt = fixture.ReadEvents(sessionId);
        var weakRequestEvent = eventsAtReceipt.LastOrDefault(item => item.Payload is InteractionRequested request
            && request.Kind == InteractionKind.WeakSandboxConsent
            && !eventsAtReceipt.Any(resolved => resolved.Payload is InteractionResolved response
                && response.InteractionId == request.InteractionId));
        var sourceReceiptAtGate = fixture.ReadReceipt(invocationId);
        Assert.Equal(invocationId, heldReceipt.CommandId);
        Assert.NotNull(sourceReceiptAtGate);
        if (weakRequestEvent.Payload is InteractionRequested weakConsent)
        {
            Assert.True(sourceReceiptAtGate is { State: CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" },
                "The consent path must hold only after its original invocation receipt is deferred. "
                + $"actual={sourceReceiptAtGate?.State}/{sourceReceiptAtGate?.OutcomeKind}/"
                + $"{sourceReceiptAtGate?.ExecutionState}/{sourceReceiptAtGate?.ExecutionCode}.");
            var requestEnvelope = weakRequestEvent.Envelope;
            var weakRunId = requestEnvelope.RunId?.ToString()
                ?? throw new InvalidDataException("Weak sandbox consent has no Run scope.");
            var taskId = requestEnvelope.TaskId?.ToString()
                ?? throw new InvalidDataException("Weak sandbox consent has no Task scope.");
            var laneId = requestEnvelope.LaneId?.ToString()
                ?? throw new InvalidDataException("Weak sandbox consent has no Lane scope.");
            var turnId = requestEnvelope.TurnId?.ToString()
                ?? throw new InvalidDataException("Weak sandbox consent has no Turn scope.");
            var toolCallId = requestEnvelope.ToolCallId?.ToString()
                ?? throw new InvalidDataException("Weak sandbox consent has no ToolCall scope.");
            try
            {
                var recoveryWhileRunnerHeld = fixture.QueryJson("interaction.recovery");
                Assert.Equal("none", recoveryWhileRunnerHeld.GetProperty("weakSandbox").GetProperty("state").GetString());
            }
            finally { releaseInvocationReceipt.TrySetResult(); }
            await WaitForWeakSandboxRecoveryReadyAsync(fixture, invocationId, weakConsent.InteractionId,
                taskId, laneId, turnId, toolCallId);
            var policyRevision = fixture.QueryJson("sessionSecurity").GetProperty("revision").GetInt64();
            var response = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "weakSandboxConsent",
                expectedRunId = weakRunId, taskId, laneId, turnId, toolCallId,
                invocationId, interactionId = weakConsent.InteractionId.ToString(),
                optionId = "consent_once", expectedPolicyRevision = policyRevision,
            }));
            var consentAck = Ack(fixture.ExchangeTrusted(response, sessionId, weakRunId));
            var continuationReceipt = fixture.ReadReceipt(response.MessageId);
            var sourceReceipt = fixture.ReadReceipt(invocationId);
            var recentEvents = string.Join(",", fixture.ReadEvents(sessionId).TakeLast(12)
                .Select(item => item.Payload.GetType().Name));
            Assert.True(consentAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                "Weak consent continuation was rejected. "
                + $"status={consentAck.Status}; error={consentAck.Error ?? "none"}; "
                + $"reason={consentAck.Outcome?.Reason ?? "none"}; "
                + $"receipt={continuationReceipt?.State}/{continuationReceipt?.OutcomeKind}/"
                + $"{continuationReceipt?.ExecutionState}/{continuationReceipt?.ExecutionCode}; "
                + $"sourceReceipt={sourceReceipt?.State}/{sourceReceipt?.OutcomeKind}/"
                + $"{sourceReceipt?.ExecutionState}/{sourceReceipt?.ExecutionCode}; "
                + $"providerRequests={fixture.Provider.RequestCount}; "
                + $"diagnosticCount={fixture.Diagnostics.Count}; recentEvents={recentEvents}");
            fixture.RecordProtocolAudit("interaction.respond weakSandboxConsent", sessionId, runId,
                "human-signed consent_once for the exact verifier ToolCall; HMAC redacted");
            weakSandboxConsentResponded = true;
        }
        else
        {
            Assert.True(sourceReceiptAtGate is { State: CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "completed" },
                "Without Weak consent, the Strong-requested workflow must have completed rather than silently downgraded. "
                + $"actual={sourceReceiptAtGate?.State}/{sourceReceiptAtGate?.OutcomeKind}/"
                + $"{sourceReceiptAtGate?.ExecutionState}/{sourceReceiptAtGate?.ExecutionCode}.");
            releaseInvocationReceipt.TrySetResult();
        }
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadEvents(sessionId);
            if (events.Any(item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId))
            {
                var finish = events.Last(item => item.Payload is NativeCommandInvocationFinished value
                    && value.InvocationId.ToString() == invocationId).Payload as NativeCommandInvocationFinished;
                Assert.Equal("completed", finish?.OutcomeCode);
                return weakSandboxConsentResponded;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The stdio Orchestrate workflow did not reach a durable terminal invocation event. "
            + $"providerRequests={fixture.Provider.RequestCount}; diagnostics={string.Join(",", fixture.Diagnostics)}; "
            + $"events={string.Join(",", fixture.ReadEvents(sessionId).TakeLast(20).Select(item => item.Payload.GetType().Name))}.");
    }

    private static async Task<JsonElement> WaitForWeakSandboxRecoveryReadyAsync(NativeCommandHandoffFixture fixture,
        string invocationId, InteractionId interactionId, string taskId, string laneId, string turnId, string toolCallId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        string state = "unavailable";
        string reason = "none";
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var recovery = fixture.QueryJson("interaction.recovery");
            var weak = recovery.GetProperty("weakSandbox");
            state = weak.GetProperty("state").GetString() ?? "invalid";
            reason = weak.TryGetProperty("reasonCode", out var reasonNode)
                ? reasonNode.GetString() ?? "invalid" : "none";
            if (state == "pending")
            {
                Assert.Equal(invocationId, weak.GetProperty("invocationId").GetString());
                Assert.Equal(interactionId.ToString(), weak.GetProperty("interactionId").GetString());
                Assert.Equal(taskId, weak.GetProperty("taskId").GetString());
                Assert.Equal(laneId, weak.GetProperty("laneId").GetString());
                Assert.Equal(turnId, weak.GetProperty("turnId").GetString());
                Assert.Equal(toolCallId, weak.GetProperty("toolCallId").GetString());
                return weak;
            }
            if (state == "blocked" && reason is not "weak_consent_scope_unavailable")
                throw new InvalidOperationException($"Weak consent recovery is blocked: {reason}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        var receipt = fixture.ReadReceipt(invocationId);
        throw new TimeoutException("Weak consent recovery did not become publicly actionable. "
            + $"state={state}; reason={reason}; sourceReceipt={receipt?.State}/{receipt?.OutcomeKind}/"
            + $"{receipt?.ExecutionState}/{receipt?.ExecutionCode}; providerRequests={fixture.Provider.RequestCount}.");
    }

    private static void InitializeWorkflowGitRepository(string workspace, string program)
    {
        var project = Path.Combine(workspace, "src", "FeatureApp");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Program.cs"), program, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(project, "FeatureApp.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workspace, "README.md"), "isolated stdio workflow worktree fixture\n", new UTF8Encoding(false));
        RunFixtureGit(workspace, "init", "--initial-branch=main");
        RunFixtureGit(workspace, "config", "user.name", "OmniCore Fixture");
        RunFixtureGit(workspace, "config", "user.email", "fixture@example.invalid");
        RunFixtureGit(workspace, "add", "README.md", "src/FeatureApp/Program.cs", "src/FeatureApp/FeatureApp.csproj");
        RunFixtureGit(workspace, "commit", "-m", "fixture baseline");
    }

    private static string RunFixtureGit(string repository, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Git fixture process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Owned Git fixture command exceeded its 15 second limit.");
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture command failed: " + error);
        return output.Trim();
    }

    [Theory]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"messages\":[{\"role\":\"system\",\"text\":\"no\"}]}")]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"policy\":\"FullAgent\",\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"messages\":[{\"role\":\"tool\",\"text\":\"x\",\"toolCall\":{}}]}")]
    [InlineData("{\"formatId\":1,\"schemaVersion\":1,\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":null,\"schemaVersion\":1,\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":{},\"schemaVersion\":1,\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":\"1\",\"messages\":[{\"role\":\"user\",\"text\":\"x\"}]}")]
    [InlineData("{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"messages\":null}")]
    public void Conversation_transcript_import_parser_rejects_authority_fields_roles_and_duplicates(string input)
    {
        Assert.ThrowsAny<InvalidDataException>(() => ConversationTranscriptImport.Parse(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void Conversation_transcript_import_parser_rejects_invalid_utf8_as_a_typed_input_error()
    {
        var invalidUtf8 = new byte[] { (byte)'{', (byte)'"', 0xFF, (byte)'}' };
        Assert.Throws<InvalidDataException>(() => ConversationTranscriptImport.Parse(invalidUtf8));
    }

    [Fact]
    public void Conversation_transcript_import_rejects_utf8_escaping_that_would_overrun_event_or_replay_frame()
    {
        var text = new string('界', 20_000);
        var message = "{\"role\":\"user\",\"text\":\"" + text + "\"}";
        var source = "{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"messages\":["
            + string.Join(",", Enumerable.Repeat(message, 5)) + "]}";
        Assert.True(Encoding.UTF8.GetByteCount(source) < ConversationTranscriptImport.MaximumSourceBytes);
        Assert.Throws<InvalidDataException>(() => ConversationTranscriptImport.Parse(Encoding.UTF8.GetBytes(source)));
    }

    [Fact]
    public async Task Native_transcript_import_is_receipted_passive_observe_only_and_feeds_later_authorized_context()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"));
        var transcript = "{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"title\":\"Imported check\",\"messages\":["
            + "{\"role\":\"user\",\"text\":\"imported-user-marker ñ🙂\"},"
            + "{\"role\":\"assistant\",\"text\":\"imported-assistant-marker\"}]}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(transcript));
        var import = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "conversation.import", documentBase64 = base64,
        }));
        var firstAck = Ack(fixture.ExchangeTrusted(import, null, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, firstAck.Outcome?.Kind);
        Assert.Equal(0, fixture.Provider.RequestCount);
        var importedIdentity = fixture.QueryJson("session.model.selection");
        var sessionId = importedIdentity.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Imported Session was not selected.");
        var security = fixture.QueryJson("sessionSecurity");
        Assert.Equal("ObserveOnly", security.GetProperty("category").GetString());
        Assert.Equal("Imported", security.GetProperty("provenance").GetString());
        Assert.Equal("ImportReview", security.GetProperty("source").GetString());
        var journal = fixture.ReadEvents(sessionId);
        Assert.Empty(journal.Select(item => item.Payload).OfType<RunCreated>());
        Assert.Empty(journal.Select(item => item.Payload).OfType<UserInputReceived>());
        var importedEvent = Assert.Single(journal.Select(item => item.Payload).OfType<ConversationTranscriptImported>());
        Assert.Equal(ConversationTranscriptImport.FormatId, importedEvent.FormatId);
        Assert.Equal(import.MessageId, importedEvent.CommandId);
        Assert.Equal(ConversationTranscriptImport.Parse(Encoding.UTF8.GetBytes(transcript)).Sha256, importedEvent.SourceSha256);
        Assert.Collection(importedEvent.Messages,
            message => { Assert.Equal("user", message.Role); Assert.Equal("imported-user-marker ñ🙂", message.Text); },
            message => { Assert.Equal("assistant", message.Role); Assert.Equal("imported-assistant-marker", message.Text); });

        var replay = Ack(fixture.ExchangeTrusted(import, null, null));
        Assert.Equal(firstAck.FirstSeq, replay.FirstSeq);
        Assert.Equal(firstAck.LastSeq, replay.LastSeq);
        Assert.Single(fixture.ReadEvents(sessionId).Select(item => item.Payload).OfType<ConversationTranscriptImported>());
        Assert.Equal(0, fixture.Provider.RequestCount);

        await fixture.RestartHostAsync();
        var afterRestart = Ack(fixture.ExchangeTrusted(import, null, null));
        Assert.Equal(firstAck.FirstSeq, afterRestart.FirstSeq);
        Assert.Single(fixture.ReadEvents(sessionId).Select(item => item.Payload).OfType<ConversationTranscriptImported>());

        var changedPayload = WireEnvelope.Command(import.MessageId, JsonSerializer.Serialize(new
        {
            cmd = "conversation.import",
            documentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(transcript.Replace("imported-user-marker", "changed-marker", StringComparison.Ordinal))),
        }));
        var conflict = Ack(fixture.ExchangeTrusted(changedPayload, null, null));
        Assert.Equal("command_id_conflict", conflict.Error);
        Assert.Single(fixture.ReadEvents(sessionId).Select(item => item.Payload).OfType<ConversationTranscriptImported>());

        var currentSecurity = fixture.QueryJson("sessionSecurity");
        var followUp = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "conversation.start", text = "authorized-follow-up-marker", mode = "plan",
            expectedRevision = (long?)null,
            expectedPolicyRevision = currentSecurity.GetProperty("revision").GetInt64(),
            expectedDefaultRevision = (string?)null,
        }));
        var followUpAck = Ack(fixture.ExchangeTrusted(followUp, sessionId, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, followUpAck.Outcome?.Kind);
        await fixture.WaitForProviderBodiesAsync(1, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var providerBody = fixture.ProviderBodies.Single();
        Assert.True(providerBody.Contains("imported-user-marker", StringComparison.Ordinal), providerBody);
        Assert.True(providerBody.Contains("imported-assistant-marker", StringComparison.Ordinal), providerBody);
        Assert.True(providerBody.Contains("authorized-follow-up-marker", StringComparison.Ordinal), providerBody);
        Assert.Equal(1, fixture.Provider.RequestCount);
        await fixture.WaitForReceiptExecutionAsync(followUp.MessageId, "completed");
    }

    [Fact]
    public async Task Native_orchestrate_selection_reads_back_the_canonical_orq_wire_mode()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"));
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
        var before = fixture.QueryJson("humanActionScope");
        var runId = before.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup turn did not leave an active Run.");
        var defaultMode = fixture.QueryJson("modePreference").GetProperty("mode").GetString();

        var selection = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "orq",
            expectedRevision = before.GetProperty("revision").GetInt64(),
            effort = "ultracode",
            adaptive = true,
            allowedModes = "plan,act,orq",
            maxAgents = 2,
            maxDepth = 1,
            maxTurns = 8,
            maxToolCalls = 12,
            maxElapsedSeconds = 900,
            maxSpendUsd = 1m,
        }));
        var ack = Ack(fixture.ExchangeTrusted(selection, sessionId, runId));
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Orchestrate selection failed: status={ack.Status}, error={ack.Error}, reason={ack.Outcome?.Reason}");

        var scope = fixture.QueryJson("humanActionScope");
        Assert.Equal(sessionId, scope.GetProperty("sessionId").GetString());
        Assert.Equal(runId, scope.GetProperty("runId").GetString());
        Assert.Equal("orq", scope.GetProperty("mode").GetString());

        var authority = fixture.QueryJson("modeAuthority");
        Assert.Equal(runId, authority.GetProperty("runId").GetString());
        Assert.Equal("orq", authority.GetProperty("mode").GetString());
        Assert.Contains("orq", authority.GetProperty("authorization").GetProperty("allowedModes")
            .EnumerateArray().Select(item => item.GetString()));

        // Selecting a Run mode must not mutate the separate user default preference.
        Assert.Equal(defaultMode, fixture.QueryJson("modePreference").GetProperty("mode").GetString());
        Assert.Equal(1, fixture.Provider.RequestCount);
    }

    [Fact]
    public async Task Native_human_shell_runs_through_host_without_provider_and_is_not_inherited_by_later_model_context()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            permissionsYaml: "profile: autonomous\nrules:\n  shell.exec: allow\n", isolateAgentRoots: true);
        try
        {
        var sessionId = fixture.CreateEmptySession();
        var preference = fixture.QueryJson("modePreference");
        var planDefault = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "mode.default.set", mode = "plan", expectedRevision = preference.GetProperty("revision").GetInt64(),
        }));
        var planAck = fixture.Server.SendUserAction(planDefault, CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, planAck.Outcome?.Kind);
        var securitySnapshot = fixture.QueryJson("sessionSecurity");
        var providerRequestsBefore = fixture.Provider.RequestCount;
        var output = new StringBuilder();
        var diagnostics = new List<string>();
        var command = OperatingSystem.IsWindows()
            ? "echo NATIVE_HUMAN_SHELL_MARKER & cd & echo NATIVE_HUMAN_SHELL_STDERR 1>&2"
            : "printf 'NATIVE_HUMAN_SHELL_MARKER\\n'; pwd; printf 'NATIVE_HUMAN_SHELL_STDERR\\n' >&2";
        var planExit = await fixture.ExecuteHumanShellCommandAsync(command, _ => { }, diagnostics.Add,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, planExit);
        Assert.Contains(diagnostics, item => item.Contains("Act default", StringComparison.OrdinalIgnoreCase));
        Assert.Null(fixture.QueryJson("humanActionScope").GetProperty("runId").GetString());
        Assert.Equal(0, fixture.Provider.RequestCount);
        var actPreference = fixture.QueryJson("modePreference");
        var actDefault = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "mode.default.set", mode = "act", expectedRevision = actPreference.GetProperty("revision").GetInt64(),
        }));
        var actAck = fixture.Server.SendUserAction(actDefault, CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, actAck.Outcome?.Kind);
        var exitCode = await fixture.ExecuteHumanShellCommandAsync(command, chunk => output.Append(chunk), diagnostics.Add,
            TestContext.Current.CancellationToken);

        Assert.True(exitCode == 0,
            $"Human shell failed: exit={exitCode}, output={output}, diagnostics={string.Join(" | ", diagnostics)}, security={securitySnapshot.GetRawText()}");
        Assert.Contains("NATIVE_HUMAN_SHELL_MARKER", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("NATIVE_HUMAN_SHELL_STDERR", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(fixture.Workspace, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics, item => item.Contains("cli.tool.ok", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.Contains("cli.tool.failed", StringComparison.Ordinal));
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        var scope = fixture.QueryJson("humanActionScope");
        var runId = Assert.IsType<string>(scope.GetProperty("runId").GetString());
        Assert.Equal("act", scope.GetProperty("mode").GetString());
        var journal = fixture.ReadEvents(sessionId);
        var shellCall = Assert.Single(journal, item => item.Envelope.RunId?.ToString() == runId
            && item.Payload is ToolCallRequested call && call.ToolName == "shell.exec");
        var shellInvocationId = Assert.IsType<ToolCallRequested>(shellCall.Payload).ProviderCallId
            .Replace("native-action-", "", StringComparison.Ordinal);
        var shellReceipt = fixture.ReadReceipt(shellInvocationId);
        Assert.Equal("completed", shellReceipt?.ExecutionState);
        Assert.Equal(runId, shellReceipt?.ExecutionRunId);
        Assert.Contains(journal, item => item.Envelope.RunId?.ToString() == runId && item.Envelope.ToolCallId == shellCall.Envelope.ToolCallId
            && item.Payload is ToolCallSucceeded);
        var shellResult = Assert.IsType<ToolCallSucceeded>(Assert.Single(journal,
            item => item.Envelope.ToolCallId == shellCall.Envelope.ToolCallId && item.Payload is ToolCallSucceeded).Payload);
        using (var display = JsonDocument.Parse(Assert.IsType<string>(shellResult.DisplayOutput)))
        {
            Assert.Equal(0, display.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("NATIVE_HUMAN_SHELL_MARKER", display.RootElement.GetProperty("output").GetString());
            Assert.Contains("NATIVE_HUMAN_SHELL_STDERR", display.RootElement.GetProperty("output").GetString());
            Assert.False(display.RootElement.GetProperty("outputTruncated").GetBoolean());
        }
        var shellState = Assert.Single(journal, item => item.Payload is ProcessSessionStateRecorded state
            && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
            && state.SourceCallId == shellCall.Envelope.ToolCallId?.ToString() && state.State == "exited");
        Assert.Equal(runId, shellState.Envelope.RunId?.ToString());
        Assert.DoesNotContain(journal, item => item.Payload is ModelStepStarted
            && item.Envelope.Sequence > shellCall.Envelope.Sequence);

        var previousCliRuntime = CliApp.UseRuntimeForTests(fixture.Runtime);
        try
        {
            var cliExit = await CliApp.RunAsync([OperatingSystem.IsWindows()
                ? "!echo NATIVE_CLI_SHELL_MARKER" : "!printf NATIVE_CLI_SHELL_MARKER"]);
            Assert.Equal(0, cliExit);
        }
        finally { _ = CliApp.UseRuntimeForTests(previousCliRuntime); }
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        var cliShellCall = Assert.Single(fixture.ReadEvents(sessionId), item => item.Payload is ToolCallRequested call
            && call.ToolName == "shell.exec" && call.ProviderCallId != "native-action-" + shellInvocationId);
        var cliInvocationId = Assert.IsType<ToolCallRequested>(cliShellCall.Payload).ProviderCallId
            .Replace("native-action-", "", StringComparison.Ordinal);
        Assert.Equal("completed", fixture.ReadReceipt(cliInvocationId)?.ExecutionState);
        var beforeCliExitThreeSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var cliExitThreeCommand = OperatingSystem.IsWindows() ? "!exit /b 3" : "!exit 3";
        previousCliRuntime = CliApp.UseRuntimeForTests(fixture.Runtime);
        int cliExitThree;
        try { cliExitThree = await CliApp.RunAsync([cliExitThreeCommand]); }
        finally { _ = CliApp.UseRuntimeForTests(previousCliRuntime); }
        Assert.Equal(3, cliExitThree);
        var cliExitThreeEvents = fixture.ReadEvents(sessionId);
        var cliExitThreeCall = Assert.Single(cliExitThreeEvents, item => item.Envelope.Sequence > beforeCliExitThreeSequence
            && item.Payload is ToolCallRequested call && call.ToolName == "shell.exec");
        var cliExitThreeInvocationId = Assert.IsType<ToolCallRequested>(cliExitThreeCall.Payload).ProviderCallId
            .Replace("native-action-", "", StringComparison.Ordinal);
        Assert.Equal("completed", fixture.ReadReceipt(cliExitThreeInvocationId)?.ExecutionState);
        Assert.Equal("native_shell_exit_nonzero", fixture.ReadReceipt(cliExitThreeInvocationId)?.ExecutionCode);
        Assert.DoesNotContain(cliExitThreeEvents, item => item.Envelope.ToolCallId == cliExitThreeCall.Envelope.ToolCallId
            && item.Payload is InteractionRequested);

        var exitOutput = new StringBuilder();
        var exitCommand = OperatingSystem.IsWindows() ? "exit /b 7" : "exit 7";
        var nonZeroExit = await fixture.ExecuteHumanShellCommandAsync(exitCommand, chunk => exitOutput.Append(chunk),
            diagnostics.Add, TestContext.Current.CancellationToken);
        Assert.Equal(7, nonZeroExit);
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);

        // OS exit code 3 is a valid completed process result. It must not be confused with
        // RunTurnCoreAsync's internal input-required sentinel.
        var exitThreeCommand = OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3";
        var beforeExitThreeSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var exitThree = await fixture.ExecuteHumanShellCommandAsync(exitThreeCommand, _ => { }, diagnostics.Add,
            TestContext.Current.CancellationToken);
        Assert.Equal(3, exitThree);
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        var exitThreeEvents = fixture.ReadEvents(sessionId);
        var exitThreeCall = Assert.Single(exitThreeEvents, item => item.Envelope.Sequence > beforeExitThreeSequence
            && item.Payload is ToolCallRequested call
            && call.ToolName == "shell.exec" && call.ProviderCallId != "native-action-" + shellInvocationId
            && call.ProviderCallId != "native-action-" + cliInvocationId);
        var exitThreeInvocationId = Assert.IsType<ToolCallRequested>(exitThreeCall.Payload).ProviderCallId
            .Replace("native-action-", "", StringComparison.Ordinal);
        var exitThreeReceipt = fixture.ReadReceipt(exitThreeInvocationId);
        Assert.Equal("completed", exitThreeReceipt?.ExecutionState);
        Assert.Equal("native_shell_exit_nonzero", exitThreeReceipt?.ExecutionCode);
        Assert.Contains(exitThreeEvents, item => item.Envelope.ToolCallId == exitThreeCall.Envelope.ToolCallId
            && item.Payload is ToolCallSucceeded);
        Assert.DoesNotContain(exitThreeEvents, item => item.Envelope.ToolCallId == exitThreeCall.Envelope.ToolCallId
            && item.Payload is InteractionRequested);
        var cancelOutput = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var orphanMarker = Path.Combine(fixture.Workspace, "human-shell-orphan-marker.txt");
        var childReadyMarker = Path.Combine(fixture.Workspace, "human-shell-child-ready.txt");
        Process? unrelatedProcess = null;
        try
        {
            string longCommand;
            if (OperatingSystem.IsWindows())
            {
                CopyProcessHelperInto(fixture.Workspace);
                var helper = Path.Combine(fixture.Workspace, "OmniCore.SandboxTestProcess.exe");
                unrelatedProcess = Process.Start(new ProcessStartInfo(helper, "sleep")
                { UseShellExecute = false, CreateNoWindow = true })
                    ?? throw new InvalidOperationException("Could not start the independent fixture process.");
                Assert.False(unrelatedProcess.HasExited);
                longCommand = "\"" + helper + "\" spawn-delayed-marker \"" + orphanMarker + "\"";
            }
            else
            {
                var sleep = new SystemExecutableResolver().Resolve("sleep", fixture.Workspace).ResolvedPath;
                unrelatedProcess = Process.Start(new ProcessStartInfo(sleep, "120")
                    { UseShellExecute = false, CreateNoWindow = true })
                    ?? throw new InvalidOperationException("Could not start the independent fixture process.");
                Assert.False(unrelatedProcess.HasExited);
                var markerPath = orphanMarker.Replace("'", "'\"'\"'", StringComparison.Ordinal);
                var readyPath = childReadyMarker.Replace("'", "'\"'\"'", StringComparison.Ordinal);
                longCommand = $"(printf 'ready' > '{readyPath}'; sleep 8; printf 'orphan-marker' > '{markerPath}'; sleep 120) & "
                    + $"while [ ! -f '{readyPath}' ]; do sleep 0.05; done; printf 'DESCENDANT:ready\\n'; sleep 120";
            }
            using var cancellation = new CancellationTokenSource();
            var invocationStart = Stopwatch.StartNew();
            var cancelTask = fixture.ExecuteHumanShellCommandAsync(longCommand, chunk =>
            {
                const string prefix = "DESCENDANT:";
                var marker = chunk.IndexOf(prefix, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var end = chunk.IndexOfAny(['\r', '\n'], marker);
                    var token = chunk[(marker + prefix.Length)..(end < 0 ? chunk.Length : end)];
                    if (OperatingSystem.IsWindows() && int.TryParse(token, out var descendantPid))
                        cancelOutput.TrySetResult(descendantPid);
                    else if (!OperatingSystem.IsWindows() && string.Equals(token, "ready", StringComparison.Ordinal))
                        cancelOutput.TrySetResult(-1);
                }
            }, diagnostics.Add, cancellation.Token);
            Assert.True(invocationStart.Elapsed < TimeSpan.FromSeconds(1),
                "The async human-shell API blocked before returning its Task.");
            var descendantPid = await cancelOutput.Task.WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(File.Exists(childReadyMarker),
                    "The namespace-scoped descendant did not prove write access before cancellation.");
                Assert.False(File.Exists(orphanMarker),
                    "The namespace-scoped descendant reached its delayed marker before cancellation began.");
            }
            var cancelTimer = Stopwatch.StartNew();
            cancellation.Cancel();
            var cancelExit = await cancelTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(cancelExit == 130,
                $"Human shell cancellation returned {cancelExit}; diagnostics={string.Join(" | ", diagnostics)}");
            Assert.True(cancelTimer.Elapsed < TimeSpan.FromSeconds(10),
                $"Cancellation did not terminate the human shell promptly: {cancelTimer.Elapsed}.");
            Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
            if (OperatingSystem.IsWindows())
            {
                var descendantDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
                var descendantAlive = true;
                while (DateTimeOffset.UtcNow < descendantDeadline)
                {
                    descendantAlive = IsProcessAlive(descendantPid);
                    if (!descendantAlive) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
                }
                Assert.False(descendantAlive, "Stopping the human shell left its fixture descendant alive.");
                Assert.False(File.Exists(orphanMarker), "Stopping the human shell let its delayed descendant write the marker.");
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(9), TestContext.Current.CancellationToken);
                Assert.False(File.Exists(orphanMarker),
                    "Stopping the Linux shell let its delayed, namespace-scoped descendant write the host-visible marker.");
            }
            Assert.False(unrelatedProcess!.HasExited, "Stopping the human shell killed an unrelated fixture process.");
        }
        finally
        {
            if (unrelatedProcess is not null)
            {
                try
                {
                    if (!unrelatedProcess.HasExited)
                    {
                        unrelatedProcess.Kill(entireProcessTree: true);
                        await unrelatedProcess.WaitForExitAsync(CancellationToken.None);
                    }
                }
                finally { unrelatedProcess.Dispose(); }
            }
        }
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);

        var security = fixture.QueryJson("sessionSecurity");
        var followupScope = fixture.QueryJson("humanActionScope");
        var followupRunId = NullableJsonString(followupScope.GetProperty("runId"));
        var followup = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "conversation.start", text = "Describe the prior human command output without guessing.",
            mode = "act", expectedRevision = followupRunId is null
                ? (long?)null : followupScope.GetProperty("revision").GetInt64(),
            expectedPolicyRevision = security.GetProperty("revision").GetInt64(),
            expectedDefaultRevision = (string?)null,
        }));
        var followupAck = Ack(fixture.ExchangeTrusted(followup, sessionId, followupRunId));
        Assert.True(followupAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Follow-up conversation start was rejected: status={followupAck.Status}, error={followupAck.Error}, reason={followupAck.Outcome?.Reason}.");
        await fixture.WaitForTurnCompletedAsync(sessionId, followupAck.LastSeq ?? 0);
        await fixture.WaitForProviderBodiesAsync(providerRequestsBefore + 1, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("NATIVE_HUMAN_SHELL_MARKER", fixture.ProviderBodies.Last(), StringComparison.Ordinal);
        Assert.DoesNotContain("NATIVE_CLI_SHELL_MARKER", fixture.ProviderBodies.Last(), StringComparison.Ordinal);
        Assert.DoesNotContain("NATIVE_CANCEL_MARKER", fixture.ProviderBodies.Last(), StringComparison.Ordinal);
        Assert.Equal(providerRequestsBefore + 1, fixture.Provider.RequestCount);
        var sourceProcessStateCount = fixture.ReadEvents(sessionId).Count(item =>
            item.Payload is ProcessSessionStateRecorded processState
            && processState.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
            && processState.SourceCallId == shellCall.Envelope.ToolCallId?.ToString());
        await fixture.RestartHostAsync();
        Assert.Equal("completed", fixture.ReadReceipt(shellInvocationId)?.ExecutionState);
        Assert.Equal(sourceProcessStateCount, fixture.ReadEvents(sessionId).Count(item =>
            item.Payload is ProcessSessionStateRecorded processState
            && processState.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
            && processState.SourceCallId == shellCall.Envelope.ToolCallId?.ToString()));
        Assert.Equal(providerRequestsBefore + 1, fixture.Provider.RequestCount);
        }
        catch (Exception failure)
        {
            fixture.RecordPrimaryTestFailure(failure);
            throw;
        }
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("weak-consent")]
    [InlineData("deny")]
    [InlineData("observe-only")]
    public async Task Native_human_shell_permission_and_weak_consent_use_signed_resume_without_provider(string scenario)
    {
        if (scenario == "weak-consent" && !OperatingSystem.IsWindows())
        {
            Assert.Skip("Persistent Weak process sessions are supported by this cut on Windows only.");
            return;
        }
        var userDecision = scenario is "ask" or "weak-consent" ? "ask" : scenario == "deny" ? "deny" : "allow";
        var permissions = "profile: autonomous\nrules:\n  shell.exec: " + userDecision + "\n";
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            permissionsYaml: permissions, isolateAgentRoots: true, includeNativeWeakSandboxCapability: true);
        var sessionId = fixture.CreateEmptySession();
        var modePreference = fixture.QueryJson("modePreference");
        var actDefault = fixture.Server.SendUserAction(WireEnvelope.Command(Guid.NewGuid().ToString("D"),
            JsonSerializer.Serialize(new { cmd = "mode.default.set", mode = "act",
                expectedRevision = modePreference.GetProperty("revision").GetInt64() })), CancellationToken.None);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, actDefault.Outcome?.Kind);

        if (scenario == "observe-only")
        {
            var security = fixture.QueryJson("sessionSecurity");
            var observeOnly = fixture.Server.SendUserAction(WireEnvelope.Command(Guid.NewGuid().ToString("D"),
                JsonSerializer.Serialize(new { cmd = "session.security.set", sessionId, category = "ObserveOnly",
                    expectedRevision = security.GetProperty("revision").GetInt64() })), CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, observeOnly.Outcome?.Kind);
        }

        if (scenario == "weak-consent")
        {
            var actualWeakLauncher = OmniHost.CreateProcessSandboxLauncher(SystemProcessRuntime.Instance());
            fixture.Runtime.HumanShellProcessLauncherFactoryForTests = () =>
                new UnavailableStrongProcessLauncherForHumanShellTest(actualWeakLauncher);
        }

        var providerRequestsBefore = fixture.Provider.RequestCount;
        var markerPath = Path.Combine(fixture.Workspace, "native-human-shell-permission-marker.txt");
        var command = scenario is "ask" or "weak-consent"
            ? OperatingSystem.IsWindows()
                ? "echo NATIVE_SHELL_PERMISSION_MARKER > \"" + markerPath + "\" & exit /b 3"
                : "printf 'NATIVE_SHELL_PERMISSION_MARKER\\n' > '" + markerPath + "'; exit 3"
            : OperatingSystem.IsWindows()
                ? "echo NATIVE_SHELL_PERMISSION_MARKER > \"" + markerPath + "\""
                : "printf 'NATIVE_SHELL_PERMISSION_MARKER\\n' > '" + markerPath + "'";
        var output = new StringBuilder();
        var diagnostics = new List<string>();
        var execution = await fixture.ExecuteHumanShellCommandAsync(command, chunk => output.Append(chunk),
            diagnostics.Add, TestContext.Current.CancellationToken, allowDeferredPermission: true);
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        var events = fixture.ReadEvents(sessionId);
        var processEventsBeforeResume = events.Where(item => item.Payload is ProcessSessionStateRecorded state
            && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource).ToArray();

        if (scenario is "deny" or "observe-only")
        {
            Assert.NotEqual(0, execution);
            Assert.False(File.Exists(markerPath), "A denied or ObserveOnly shell command must not create its fixture marker.");
            Assert.Empty(processEventsBeforeResume);
            Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
            if (scenario == "deny")
            {
                var deniedShellCall = Assert.Single(events, item => item.Payload is ToolCallRequested call
                    && call.ToolName == "shell.exec");
                Assert.Contains(events, item => item.Payload is PermissionEvaluated evaluation
                    && evaluation.ToolCallId == Assert.IsType<ToolCallRequested>(deniedShellCall.Payload).ToolCallId
                    && evaluation.Decision == PermissionDecision.Deny);
            }
            else
            {
                Assert.Equal(ModelPolicyCategory.ObserveOnly,
                    fixture.QueryJson("sessionSecurity").GetProperty("category").GetString() is { } category
                        ? Enum.Parse<ModelPolicyCategory>(category) : throw new InvalidDataException("ObserveOnly category missing."));
            }
            Assert.DoesNotContain(events, item => item.Payload is InteractionRequested request
                && request.Kind is InteractionKind.Permission or InteractionKind.WeakSandboxConsent);
            return;
        }

        var expectedKind = scenario is "ask" or "weak-consent"
            ? InteractionKind.Permission : InteractionKind.WeakSandboxConsent;
        Assert.True(execution == 3, $"Expected a deferred {expectedKind} response; got code={execution}, diagnostics={string.Join(" | ", diagnostics)}, requests={string.Join(",", events.Select(item => item.Payload.GetType().Name).Where(name => name.Contains("Interaction") || name.Contains("Permission") || name.Contains("ToolCall")))}.");
        Assert.False(File.Exists(markerPath), "A process must not start before the signed response resumes its exact invocation.");
        Assert.Empty(processEventsBeforeResume);
        var requestEvent = Assert.Single(events, item => item.Payload is InteractionRequested request
            && request.Kind == expectedKind);
        var request = Assert.IsType<InteractionRequested>(requestEvent.Payload);
        var interactionId = request.InteractionId.ToString();
        var runId = requestEvent.Envelope.RunId?.ToString()
            ?? throw new InvalidDataException("Human shell interaction has no Run scope.");
        var shellRequest = Assert.Single(events, item => item.Payload is ToolCallRequested call
            && call.ToolName == "shell.exec");
        var shellInvocationId = Assert.IsType<ToolCallRequested>(shellRequest.Payload).ProviderCallId
            .Replace("native-action-", "", StringComparison.Ordinal);
        var deferredReceipt = fixture.ReadReceipt(shellInvocationId);
        Assert.Equal("deferred", deferredReceipt?.ExecutionState);
        Assert.Equal("input_required", deferredReceipt?.ExecutionCode);
        Assert.Equal(runId, deferredReceipt?.ExecutionRunId);
        var resumable = scenario is "ask" or "weak-consent" ? fixture.Runtime.IsHumanShellPermission(interactionId)
            : fixture.Runtime.IsHumanShellWeakSandboxConsent(interactionId);
        Assert.True(resumable, $"The persisted human shell interaction was not resumable: scenario={scenario}, interaction={interactionId}, eventRun={runId}, scope={fixture.QueryJson("humanActionScope").GetRawText()}, kinds={string.Join(",", events.Select(item => item.Payload.GetType().Name))}.");
        var securityRevision = fixture.QueryJson("sessionSecurity").GetProperty("revision").GetInt64();
        var responseType = scenario is "ask" or "weak-consent" ? "choice" : "weakSandboxConsent";
        var optionId = scenario is "ask" or "weak-consent" ? "allow_once" : "consent_once";
        var response = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "interaction.respond", responseType, interactionId, optionId,
            expectedRunId = runId, expectedPolicyRevision = securityRevision,
        }));
        var responseAck = fixture.Runtime.DispatchHumanShellInteractionResponse(response);
        Assert.True(responseAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Signed {scenario} response was rejected: status={responseAck.Status}, error={responseAck.Error}, reason={responseAck.Outcome?.Reason}.");
        var finalInteraction = request.InteractionId;
        int resumedCode;
        if (scenario == "weak-consent")
        {
            var firstResumeCode = await fixture.Runtime.ResumeHumanShellPermissionAsync(interactionId, optionId,
                responseAck, _ => { }, diagnostics.Add, TestContext.Current.CancellationToken);
            Assert.Equal(3, firstResumeCode);
            Assert.Equal("deferred", fixture.ReadReceipt(shellInvocationId)?.ExecutionState);
            Assert.False(File.Exists(markerPath), "Allowing Ask must still wait for the separately signed Weak consent.");
            Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
            var weakEvents = fixture.ReadEvents(sessionId);
            var weakRequestEvent = Assert.Single(weakEvents, item => item.Payload is InteractionRequested weakRequest
                && weakRequest.Kind == InteractionKind.WeakSandboxConsent);
            var weakRequest = Assert.IsType<InteractionRequested>(weakRequestEvent.Payload);
            finalInteraction = weakRequest.InteractionId;
            Assert.True(fixture.Runtime.IsHumanShellWeakSandboxConsent(finalInteraction.ToString()),
                "The Weak consent must bind to the same native shell invocation after Ask resumes.");
            var weakRecovery = fixture.QueryJson("interaction.recovery").GetProperty("weakSandbox");
            Assert.True(weakRecovery.GetProperty("state").GetString() == "pending",
                $"The current native shell Weak choice was not advertised as actionable: {weakRecovery.GetRawText()}.");
            interactionId = finalInteraction.ToString();
            responseType = "weakSandboxConsent";
            optionId = "consent_once";
            response = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType, interactionId, optionId,
                expectedRunId = runId, expectedPolicyRevision = securityRevision,
            }));
            responseAck = fixture.Runtime.DispatchHumanShellInteractionResponse(response);
            Assert.True(responseAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                $"Signed Weak response was rejected: status={responseAck.Status}, error={responseAck.Error}, reason={responseAck.Outcome?.Reason}.");
            var codecs = fixture.Server.AcquireCodecs();
            var persistedRun = fixture.Server.AcquireStore().ReadFrom(new SessionId(Guid.Parse(sessionId)), 1)
                .Where(item => item.RunId?.ToString() == runId).ToArray();
            AssertWeakNativeShellResumeProjectionRejectsUnboundHistory(persistedRun, codecs,
                new SessionId(Guid.Parse(sessionId)), new RunId(Guid.Parse(runId)));
            resumedCode = await fixture.Runtime.ResumeHumanShellWeakSandboxConsentAsync(interactionId, optionId,
                responseAck, _ => { }, diagnostics.Add, TestContext.Current.CancellationToken);
        }
        else if (scenario == "ask")
            resumedCode = await fixture.Runtime.ResumeHumanShellPermissionAsync(interactionId, optionId, responseAck,
                _ => { }, diagnostics.Add, TestContext.Current.CancellationToken);
        else
            resumedCode = await fixture.Runtime.ResumeHumanShellWeakSandboxConsentAsync(interactionId, optionId, responseAck,
                _ => { }, diagnostics.Add, TestContext.Current.CancellationToken);
        var resumeEvents = fixture.ReadEvents(sessionId);
        var permissionFailures = resumeEvents.Select(item => item.Payload).OfType<PermissionDenied>()
            .Select(item => item.Cause).ToArray();
        var permissionDecisions = resumeEvents.Select(item => item.Payload).OfType<PermissionEvaluated>()
            .Select(item => item.Decision + ":" + item.LayersJson).ToArray();
        var permissionProof = resumeEvents.Where(item => item.Envelope.ToolCallId == shellRequest.Envelope.ToolCallId)
            .Select(item => item.Envelope.Sequence + ":" + (item.Payload switch
            {
                PermissionRequested value => "PermissionRequested:" + value.InteractionId,
                InteractionRequested value => "InteractionRequested:" + value.Kind + ":" + value.OptionsJson,
                InteractionResolved value => "InteractionResolved:" + value.OptionId + ":" + value.Cause,
                PermissionGranted value => "PermissionGranted:" + value.Lifetime + ":" + value.GrantId,
                ToolCallAuthorized => "ToolCallAuthorized",
                _ => item.Payload.GetType().Name,
            })).ToArray();
        var expectedExitCode = scenario is "ask" or "weak-consent" ? 3 : 0;
        Assert.True(resumedCode == expectedExitCode,
            $"Signed {scenario} continuation did not complete the human shell action: code={resumedCode}, expected={expectedExitCode}, diagnostics={string.Join(" | ", diagnostics)}, permissionFailures={string.Join(" | ", permissionFailures)}, permissionDecisions={string.Join(" | ", permissionDecisions)}, permissionProof={string.Join(" | ", permissionProof)}.");
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        Assert.Equal("NATIVE_SHELL_PERMISSION_MARKER", File.ReadAllText(markerPath).Trim());
        var terminalReceipt = fixture.ReadReceipt(shellInvocationId);
        Assert.Equal("completed", terminalReceipt?.ExecutionState);
        Assert.Equal(runId, terminalReceipt?.ExecutionRunId);

        var completedEvents = fixture.ReadEvents(sessionId);
        if (scenario == "weak-consent")
        {
            var codecs = fixture.Server.AcquireCodecs();
            var persistedRun = fixture.Server.AcquireStore().ReadFrom(new SessionId(Guid.Parse(sessionId)), 1)
                .Where(item => item.RunId?.ToString() == runId).ToArray();
            _ = RunProjection.Replay(new SessionId(Guid.Parse(sessionId)), new RunId(Guid.Parse(runId)), codecs,
                persistedRun);
        }
        var toolCalls = completedEvents.Where(item => item.Payload is ToolCallRequested call
            && call.ToolName == "shell.exec").ToArray();
        var shellCallEvent = Assert.Single(toolCalls);
        var shellCall = Assert.IsType<ToolCallRequested>(shellCallEvent.Payload);
        Assert.Single(completedEvents, item => item.Payload is ToolCallSucceeded success
            && success.ToolCallId == shellCall.ToolCallId);
        var processState = Assert.Single(completedEvents, item => item.Payload is ProcessSessionStateRecorded state
            && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
            && state.SourceCallId == shellCall.ToolCallId.ToString() && state.State == "exited");
        Assert.Equal(runId, processState.Envelope.RunId?.ToString());
        Assert.Contains(completedEvents, item => item.Payload is InteractionResolved resolved
            && resolved.InteractionId == finalInteraction && resolved.OptionId == optionId
            && resolved.Cause == InteractionCause.User);
        if (scenario == "weak-consent")
            Assert.Contains(completedEvents, item => item.Payload is InteractionResolved resolved
                && resolved.InteractionId == request.InteractionId && resolved.OptionId == "allow_once"
                && resolved.Cause == InteractionCause.User);
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        if (scenario == "weak-consent")
        {
            var processStateCount = completedEvents.Count(item => item.Payload is ProcessSessionStateRecorded state
                && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
                && state.SourceCallId == shellCall.ToolCallId.ToString());
            await fixture.RestartHostAsync();
            Assert.Equal("completed", fixture.ReadReceipt(shellInvocationId)?.ExecutionState);
            Assert.Equal(processStateCount, fixture.ReadEvents(sessionId).Count(item =>
                item.Payload is ProcessSessionStateRecorded state
                && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
                && state.SourceCallId == shellCall.ToolCallId.ToString()));
            Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        }
    }

    [Theory]
    [InlineData("allow-exit-three")]
    [InlineData("ask-then-weak")]
    public async Task Framed_stdio_native_shell_finishes_source_and_response_receipts_without_manual_resume(
        string scenario)
    {
        if (scenario == "ask-then-weak" && !OperatingSystem.IsWindows())
        {
            Assert.Skip("Persistent Weak process sessions are supported by this cut on Windows only.");
            return;
        }

        var askThenWeak = scenario == "ask-then-weak";
        var permissions = askThenWeak
            ? "profile: autonomous\nrules:\n  shell.exec: ask\n"
            : "profile: autonomous\nrules:\n  shell.exec: allow\n";
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            permissionsYaml: permissions, isolateAgentRoots: true, includeNativeWeakSandboxCapability: true);
        var sessionId = fixture.CreateEmptySession();
        var preference = fixture.QueryJson("modePreference");
        var mode = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "mode.default.set", mode = "act", expectedRevision = preference.GetProperty("revision").GetInt64(),
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
            fixture.Server.SendUserAction(mode, TestContext.Current.CancellationToken).Outcome?.Kind);

        UnavailableStrongProcessLauncherForHumanShellTest? unavailableStrong = null;
        if (askThenWeak)
        {
            var actualWeakLauncher = OmniHost.CreateProcessSandboxLauncher(SystemProcessRuntime.Instance());
            unavailableStrong = new UnavailableStrongProcessLauncherForHumanShellTest(actualWeakLauncher);
            fixture.Runtime.HumanShellProcessLauncherFactoryForTests = () => unavailableStrong;
        }

        var catalog = fixture.QueryJson("command.catalog");
        var shell = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:shell.exec");
        Assert.True(shell.GetProperty("enabled").GetBoolean());
        var markerPath = Path.Combine(fixture.Workspace, "framed-shell-effect.txt");
        var commandText = OperatingSystem.IsWindows()
            ? "echo FRAMED_NATIVE_SHELL_EFFECT > \"" + markerPath + "\""
                + " & exit /b 3"
            : "printf 'FRAMED_NATIVE_SHELL_EFFECT\\n' > '" + markerPath + "'"
                + "; exit 3";
        var shellArgs = JsonSerializer.Serialize(new { command = commandText, cwd = fixture.Workspace, timeoutSeconds = 60 });
        var invocationId = Guid.NewGuid().ToString("D");
        var invocation = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = "core:shell.exec",
            arguments = new[] { "shell.exec", shellArgs },
            expectedSessionId = sessionId,
            expectedRunId = fixture.QueryJson("humanActionScope").GetProperty("runId").GetString(),
            expectedModeRevision = NullableJsonInt64(catalog.GetProperty("modeRevision")),
            expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
            catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = shell.GetProperty("sourceFingerprint").GetString(),
        }));
        var providerRequestsBefore = fixture.Provider.RequestCount;
        var initialAck = Ack(fixture.ExchangeTrusted(invocation, sessionId, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, initialAck.Outcome?.Kind);

        string? weakResponseId = null;
        string? weakResponsePayload = null;
        if (askThenWeak)
        {
            var initialDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < initialDeadline
                && fixture.ReadReceipt(invocationId)?.ExecutionState is null)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            var initialReceipt = fixture.ReadReceipt(invocationId);
            if (initialReceipt?.ExecutionState != "deferred")
            {
                var initialEvents = fixture.ReadEvents(sessionId);
                var decisionSummary = string.Join(";", initialEvents.Select(item => item.Payload)
                    .OfType<PermissionEvaluated>().Select(item => item.Decision + "/" + item.LayersJson));
                var interactionSummary = string.Join(",", initialEvents.Select(item => item.Payload)
                    .OfType<InteractionRequested>().Select(item => item.Kind.ToString()));
                var finishSummary = string.Join(",", initialEvents.Select(item => item.Payload)
                    .OfType<NativeCommandInvocationFinished>().Select(item => item.OutcomeCode));
                Assert.Fail($"The framed Ask did not defer: receipt={initialReceipt?.ExecutionState}/{initialReceipt?.ExecutionCode}; "
                    + $"permissionDecisions={decisionSummary}; interactions={interactionSummary}; finishes={finishSummary}; "
                    + $"policy={fixture.QueryJson("sessionSecurity").GetRawText()}.");
            }
            Assert.False(File.Exists(markerPath));
            var events = fixture.ReadEvents(sessionId);
            var permissionRequestEvent = Assert.Single(events, item => item.Payload is InteractionRequested request
                && request.Kind == InteractionKind.Permission);
            var permissionRequest = Assert.IsType<InteractionRequested>(permissionRequestEvent.Payload);
            var runId = permissionRequestEvent.Envelope.RunId?.ToString()
                ?? throw new InvalidDataException("Permission request has no Run scope.");
            var policyRevision = fixture.QueryJson("sessionSecurity").GetProperty("revision").GetInt64();
            var allow = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "choice", interactionId = permissionRequest.InteractionId.ToString(),
                optionId = "allow_once", expectedRunId = runId, expectedPolicyRevision = policyRevision,
            }));
            var allowAck = Ack(fixture.ExchangeTrusted(allow, sessionId, runId));
            Assert.True(allowAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
                $"The framed Ask response was rejected: status={allowAck.Status}, error={allowAck.Error}, reason={allowAck.Outcome?.Reason}, options={permissionRequest.OptionsJson}.");
            var allowDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < allowDeadline
                && fixture.ReadReceipt(allow.MessageId)?.ExecutionState is null)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            var allowReceipt = fixture.ReadReceipt(allow.MessageId);
            if (allowReceipt?.ExecutionState != "deferred")
            {
                var allowEvents = fixture.ReadEvents(sessionId);
                var weakKinds = string.Join(",", allowEvents.Select(item => item.Payload)
                    .OfType<InteractionRequested>().Select(item => item.Kind.ToString()));
                var decisions = string.Join(";", allowEvents.Select(item => item.Payload)
                    .OfType<PermissionEvaluated>().Select(item => item.Decision + "/" + item.LayersJson));
                Assert.Fail($"Framed Ask did not defer into Weak consent: receipt={allowReceipt?.ExecutionState}/{allowReceipt?.ExecutionCode}; "
                    + $"interactions={weakKinds}; decisions={decisions}; launcherRequested={unavailableStrong?.LastRequestedStrength}.");
            }

            var weakRequestEvent = await WaitForNativeShellInteractionAsync(fixture, sessionId,
                InteractionKind.WeakSandboxConsent);
            Assert.False(File.Exists(markerPath), "The shell must remain unlaunched until the Weak consent ACK.");
            var weakRequest = Assert.IsType<InteractionRequested>(weakRequestEvent.Payload);
            var toolCallId = weakRequestEvent.Envelope.ToolCallId?.ToString()
                ?? throw new InvalidDataException("Weak request has no ToolCall scope.");
            var taskId = weakRequestEvent.Envelope.TaskId?.ToString()
                ?? throw new InvalidDataException("Weak request has no Task scope.");
            var laneId = weakRequestEvent.Envelope.LaneId?.ToString()
                ?? throw new InvalidDataException("Weak request has no Lane scope.");
            var turnId = weakRequestEvent.Envelope.TurnId?.ToString()
                ?? throw new InvalidDataException("Weak request has no Turn scope.");
            weakResponseId = Guid.NewGuid().ToString("D");
            weakResponsePayload = JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "weakSandboxConsent",
                interactionId = weakRequest.InteractionId.ToString(), optionId = "consent_once",
                invocationId, taskId, laneId, turnId, toolCallId,
                expectedRunId = runId, expectedPolicyRevision = policyRevision,
            });
            var consent = WireEnvelope.Command(weakResponseId, weakResponsePayload);
            var consentAck = Ack(fixture.ExchangeTrusted(consent, sessionId, runId));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, consentAck.Outcome?.Kind);
            await fixture.WaitForReceiptExecutionAsync(weakResponseId, "completed");
            Assert.Equal(SandboxStrength.Weak, unavailableStrong?.LastRequestedStrength);
        }
        else
            await fixture.WaitForReceiptExecutionAsync(invocationId, "completed");

        var sourceReceipt = fixture.ReadReceipt(invocationId);
        Assert.Equal("completed", sourceReceipt?.ExecutionState);
        Assert.Equal("native_shell_exit_nonzero", sourceReceipt?.ExecutionCode);
        if (weakResponseId is not null)
        {
            var responseReceipt = fixture.ReadReceipt(weakResponseId);
            Assert.Equal("completed", responseReceipt?.ExecutionState);
            Assert.Equal("native_shell_exit_nonzero", responseReceipt?.ExecutionCode);
        }
        Assert.Equal("FRAMED_NATIVE_SHELL_EFFECT", File.ReadAllText(markerPath).Trim());
        Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        var completedEvents = fixture.ReadEvents(sessionId);
        var shellCall = Assert.Single(completedEvents, item => item.Payload is ToolCallRequested call
            && call.ToolName == "shell.exec");
        var toolCall = Assert.IsType<ToolCallRequested>(shellCall.Payload);
        Assert.Single(completedEvents, item => item.Payload is ToolCallSucceeded succeeded
            && succeeded.ToolCallId == toolCall.ToolCallId);
        Assert.Single(completedEvents, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        var nativeFinish = Assert.IsType<NativeCommandInvocationFinished>(Assert.Single(completedEvents,
            item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId).Payload);
        Assert.Equal("completed", nativeFinish.OutcomeCode);
        var processStates = completedEvents.Count(item => item.Payload is ProcessSessionStateRecorded state
            && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
            && state.SourceCallId == toolCall.ToolCallId.ToString());
        Assert.True(processStates > 0);
        var processHandles = completedEvents.Select(item => item.Payload).OfType<ProcessSessionStateRecorded>()
            .Where(state => state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
                && state.SourceCallId == toolCall.ToolCallId.ToString())
            .Select(state => state.HandleId).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(processHandles);

        var stableSequence = completedEvents.Max(item => item.Envelope.Sequence);
        var invocationReplay = Ack(fixture.ExchangeTrusted(invocation, sessionId, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, invocationReplay.Outcome?.Kind);
        Assert.Equal(stableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal("completed", fixture.ReadReceipt(invocationId)?.ExecutionState);
        if (weakResponseId is not null && weakResponsePayload is not null)
        {
            var replay = Ack(fixture.ExchangeTrusted(WireEnvelope.Command(weakResponseId, weakResponsePayload),
                sessionId, fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()));
            Assert.Equal(weakResponseId, replay.CommandId);
            Assert.Equal(stableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        }
        await fixture.RestartHostAsync();
        var reopenedSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var reopenedInvocationReplay = Ack(fixture.ExchangeTrusted(invocation, sessionId, null));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, reopenedInvocationReplay.Outcome?.Kind);
        Assert.Equal(reopenedSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal("completed", fixture.ReadReceipt(invocationId)?.ExecutionState);
        if (weakResponseId is not null && weakResponsePayload is not null)
        {
            await fixture.RestartHostAsync();
            reopenedSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
            var afterReopen = Ack(fixture.ExchangeTrusted(WireEnvelope.Command(weakResponseId, weakResponsePayload),
                sessionId, fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()));
            Assert.Equal(weakResponseId, afterReopen.CommandId);
            Assert.Equal(reopenedSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
            Assert.Equal("completed", fixture.ReadReceipt(invocationId)?.ExecutionState);
            Assert.Equal("completed", fixture.ReadReceipt(weakResponseId)?.ExecutionState);
            Assert.Equal(processStates, fixture.ReadEvents(sessionId).Count(item => item.Payload is ProcessSessionStateRecorded state
                && state.SourceToolId == HumanShellProcessTool.ProcessOwnerSource
                && state.SourceCallId == toolCall.ToolCallId.ToString()));
            Assert.Equal(providerRequestsBefore, fixture.Provider.RequestCount);
        }
    }

    private static async Task<(DomainEvent Envelope, DomainEventPayload Payload)> WaitForNativeShellInteractionAsync(
        NativeCommandHandoffFixture fixture, string sessionId, InteractionKind kind)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var found = fixture.ReadEvents(sessionId).LastOrDefault(item => item.Payload is InteractionRequested request
                && request.Kind == kind);
            if (found.Payload is not null) return found;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"The framed human shell did not reach {kind}.");
    }

    private static void AssertWeakNativeShellResumeProjectionRejectsUnboundHistory(
        DomainEvent[] events, IEventCodecRegistry codecs, SessionId session, RunId run)
    {
        Assert.Equal(RunState.Running, RunProjection.Replay(session, run, codecs, events).State);
        var nativeCallEvent = Assert.Single(events, item => codecs.Decode(item) is ToolCallRequested call
            && call.ToolName == "shell.exec");
        var nativeCall = Assert.IsType<ToolCallRequested>(codecs.Decode(nativeCallEvent));
        var invocationText = nativeCall.ProviderCallId["native-action-".Length..];
        var invocation = new CommandId(Guid.ParseExact(invocationText, "D"));
        var resumeEvent = Assert.Single(events, item => codecs.Decode(item) is RunInteractionResumed);
        var resume = Assert.IsType<RunInteractionResumed>(codecs.Decode(resumeEvent));
        var responseEvent = Assert.Single(events, item => codecs.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId == resume.InteractionId);
        var admissionEvent = Assert.Single(events, item => codecs.Decode(item) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation);
        var startedEvent = Assert.Single(events, item => codecs.Decode(item) is NativeCommandInvocationStarted started
            && started.InvocationId == invocation);

        void Reject(string label, IEnumerable<DomainEvent> candidate)
        {
            var failure = Assert.Throws<InvalidStateTransitionException>(() =>
                RunProjection.Replay(session, run, codecs, candidate.OrderBy(item => item.Sequence).ToArray()));
            Assert.Contains("interaction resume", failure.Message, StringComparison.OrdinalIgnoreCase);
        }

        Reject("native admission missing", events.Where(item => item != admissionEvent));
        Reject("native start missing", events.Where(item => item != startedEvent));
        Reject("different invocation", events.Select(item => item == nativeCallEvent
            ? ReplaceNativeEvent(item, nativeCall with { ProviderCallId = "native-action-" + Guid.NewGuid().ToString("D") }, codecs)
            : item));
        Reject("different tool call", events.Select(item => item == nativeCallEvent
            ? ReplaceNativeEvent(item, nativeCall with { ToolCallId = new ToolCallId(Guid.NewGuid()) }, codecs)
            : item));
        Reject("different lane", events.Select(item => item == resumeEvent
            ? ReplaceNativeEventEnvelope(item, laneId: new LaneId(Guid.NewGuid())) : item));
        Reject("different response command", events.Select(item => item == resumeEvent
            ? ReplaceNativeEvent(item, resume with { CommandId = Guid.NewGuid().ToString("D") }, codecs) : item));
        var response = Assert.IsType<InteractionResolved>(codecs.Decode(responseEvent));
        Reject("non-human response", events.Select(item => item == responseEvent
            ? ReplaceNativeEvent(item, response with { Cause = InteractionCause.NoClient }, codecs) : item));

        var started = new ToolCallStarted(nativeCall.ToolCallId, EffectClass.NonIdempotent, null);
        var syntheticStart = DomainEvent.Stored(EventId.New(), session, resumeEvent.Sequence,
            started.Type(), started.SchemaVersion(), DateTimeOffset.UtcNow, null, run, run,
            resumeEvent.TaskId, resumeEvent.LaneId, resumeEvent.TurnId, null, nativeCall.ToolCallId,
            resumeEvent.ArtifactRefs, codecs.CodecFor(started.Type()).Encode(started), resumeEvent.ExecutionId,
            resumeEvent.Source);
        Reject("effect started before resume", events.Where(item => item.Sequence < resumeEvent.Sequence)
            .Concat([syntheticStart])
            .Concat(events.Where(item => item.Sequence >= resumeEvent.Sequence)
                .Select(item => ResequenceNativeEvent(item, item.Sequence + 1))));
    }

    private static DomainEvent ReplaceNativeEvent(DomainEvent source, DomainEventPayload payload,
        IEventCodecRegistry codecs) => DomainEvent.Stored(source.EventId, source.SessionId, source.Sequence,
        source.Type, source.SchemaVersion, source.Timestamp, source.Causation, source.CorrelationId, source.RunId,
        source.TaskId, source.LaneId, source.TurnId, source.PlanItemId, source.ToolCallId, source.ArtifactRefs,
        codecs.CodecFor(source.Type).Encode(payload), source.ExecutionId, source.Source);

    private static DomainEvent ReplaceNativeEventEnvelope(DomainEvent source, LaneId? laneId = null) =>
        DomainEvent.Stored(source.EventId, source.SessionId, source.Sequence, source.Type, source.SchemaVersion,
            source.Timestamp, source.Causation, source.CorrelationId, source.RunId, source.TaskId,
            laneId ?? source.LaneId, source.TurnId, source.PlanItemId, source.ToolCallId, source.ArtifactRefs,
            source.PayloadJson, source.ExecutionId, source.Source);

    private static DomainEvent ResequenceNativeEvent(DomainEvent source, long sequence) =>
        DomainEvent.Stored(source.EventId, source.SessionId, sequence, source.Type, source.SchemaVersion,
            source.Timestamp, source.Causation, source.CorrelationId, source.RunId, source.TaskId,
            source.LaneId, source.TurnId, source.PlanItemId, source.ToolCallId, source.ArtifactRefs,
            source.PayloadJson, source.ExecutionId, source.Source);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task First_native_explain_admits_a_blank_session_and_stale_scopes_never_reach_the_provider()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"));
        var sessionId = fixture.CreateEmptySession();
        var before = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, before.GetProperty("sessionId").GetString());
        Assert.Null(NullableJsonString(before.GetProperty("runId")));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("modeRevision").ValueKind);
        var explain = Assert.Single(before.GetProperty("commands").EnumerateArray(),
            item => item.GetProperty("canonicalCommandId").GetString() == "core:explain");
        Assert.True(explain.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, fixture.ReadEvents(sessionId).Count(item => item.Payload is UserInputReceived));

        WireEnvelope Invoke(string commandId, string expectedSessionId, long expectedPolicyRevision,
            string sourceFingerprint)
        {
            var command = WireEnvelope.Command(commandId, JsonSerializer.Serialize(new
            {
                cmd = "command.invoke.user",
                canonicalCommandId = "core:explain",
                arguments = new[] { "README.md" },
                expectedSessionId,
                expectedRunId = (string?)null,
                expectedModeRevision = (long?)null,
                expectedPolicyRevision,
                expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
                catalogRevision = before.GetProperty("catalogRevision").GetString(),
                sourceFingerprint,
            }));
            return fixture.ExchangeTrusted(command, expectedSessionId, null);
        }

        var stalePolicy = Ack(Invoke(Guid.NewGuid().ToString("D"), sessionId,
            before.GetProperty("policyRevision").GetInt64() + 1, explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, stalePolicy.Outcome?.Kind);

        var staleFingerprint = Ack(Invoke(Guid.NewGuid().ToString("D"), sessionId,
            before.GetProperty("policyRevision").GetInt64(), new string('c', 64)));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, staleFingerprint.Outcome?.Kind);

        var otherSessionId = Guid.NewGuid().ToString("D");
        var wrongSession = Ack(Invoke(Guid.NewGuid().ToString("D"), otherSessionId,
            before.GetProperty("policyRevision").GetInt64(), explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, wrongSession.Outcome?.Kind);

        var nullRunContinue = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId = Guid.NewGuid().ToString("D"),
            expectedSessionId = sessionId,
            expectedRunId = (string?)null,
            expectedModeRevision = (long?)null,
            expectedPolicyRevision = before.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
            catalogRevision = before.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));
        var invalidContinue = Ack(fixture.ExchangeTrusted(nullRunContinue, sessionId, null));
        Assert.Equal("trusted_action_invalid", invalidContinue.Error);

        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationAdmitted);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.ReadEvents(sessionId).Count(item => item.Payload is UserInputReceived));

        var validInvoke = Ack(Invoke(invocationId, sessionId,
            before.GetProperty("policyRevision").GetInt64(), explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, validInvoke.Outcome?.Kind);
        var admissions = fixture.ReadEvents(sessionId).Where(item => item.Payload is NativeCommandInvocationAdmitted).ToArray();
        var admissionEnvelope = Assert.Single(admissions);
        var admission = Assert.IsType<NativeCommandInvocationAdmitted>(admissionEnvelope.Payload);
        Assert.Equal(invocationId, admission.InvocationId.ToString());
        Assert.Equal(sessionId, admission.SessionId.ToString());
        Assert.NotNull(admission.RunId);
        Assert.Contains(admission.ProtectedPayload, admissionEnvelope.Envelope.ArtifactRefs);
        Assert.DoesNotContain("README.md", admissionEnvelope.Envelope.PayloadJson, StringComparison.Ordinal);
        var runId = admission.RunId!.Value.ToString();

        await fixture.Provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForInvocationAndTurnAsync(sessionId, runId, invocationId, minimumCompletedTurns: 1);

        var events = fixture.ReadEvents(sessionId);
        var graph = events.Where(item => item.Envelope.RunId?.ToString() == runId).ToArray();
        Assert.Single(graph, item => item.Payload is RunCreated);
        Assert.Single(graph, item => item.Payload is TaskCreated);
        Assert.Single(graph, item => item.Payload is LaneCreated);
        Assert.Single(graph, item => item.Payload is PlanCreated);
        Assert.Single(graph, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Single(graph, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId && finished.OutcomeCode == "completed");
        Assert.Equal(1, events.Count(item => item.Payload is UserInputReceived input
            && input.RunId.ToString() == runId));
        Assert.Contains("Explain the repository content at the path README.md", fixture.ProviderBodies.Single(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_command_ack_cut_recovers_ready_invocation_only_after_explicit_continue()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(invocationId);
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        var before = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, before.GetProperty("sessionId").GetString());
        Assert.NotNull(before.GetProperty("runId").GetString());
        var explain = Assert.Single(before.GetProperty("commands").EnumerateArray(),
            item => item.GetProperty("canonicalCommandId").GetString() == "core:explain");
        Assert.Equal("prompt", explain.GetProperty("kind").GetString());
        Assert.True(explain.GetProperty("enabled").GetBoolean());
        var userInputCountBeforeInvocation = fixture.ReadEvents(sessionId)
            .Count(item => item.Payload is UserInputReceived);
        Assert.Equal(1, userInputCountBeforeInvocation);

        var invocation = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = explain.GetProperty("canonicalCommandId").GetString(),
            arguments = new[] { "README.md" },
            expectedSessionId = sessionId,
            expectedRunId = before.GetProperty("runId").GetString(),
            expectedModeRevision = before.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = before.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
            catalogRevision = before.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));

        var ackEnvelope = fixture.ExchangeTrusted(invocation, sessionId,
            before.GetProperty("runId").GetString());
        var ack = Ack(ackEnvelope);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.Equal(invocationId, ack.CommandId);
        await fixture.FailAckWriteAndStopHostAsync();
        Assert.Equal(1, fixture.Provider.RequestCount); // No provider request before the ACK writer returns.

        var admission = Assert.Single(fixture.ReadEvents(sessionId),
            item => item.Payload is NativeCommandInvocationAdmitted);
        var admissionPayload = Assert.IsType<NativeCommandInvocationAdmitted>(admission.Payload);
        Assert.Equal(invocationId, admissionPayload.InvocationId.ToString());
        Assert.Contains(admissionPayload.ProtectedPayload, admission.Envelope.ArtifactRefs);
        Assert.DoesNotContain("README.md", admission.Envelope.PayloadJson, StringComparison.Ordinal);
        var initialReceipt = fixture.ReadReceipt(invocationId);
        Assert.Equal(CommandReceiptState.Completed, initialReceipt?.State);
        Assert.Equal("Accepted", initialReceipt?.OutcomeKind);
        Assert.Null(initialReceipt?.ExecutionState);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item =>
            item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Equal(userInputCountBeforeInvocation, fixture.ReadEvents(sessionId)
            .Count(item => item.Payload is UserInputReceived));

        // The complete ACK reached the client, but the transport failed before WriteFrame
        // returned and before StdioProtocolHost called StartPendingAfterAck.
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item =>
            item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);

        await fixture.ReopenHostAsync();
        var recovery = fixture.QueryJson("command.recovery");
        var ready = Assert.Single(recovery.GetProperty("invocations").EnumerateArray(),
            item => item.GetProperty("invocationId").GetString() == invocationId);
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.DoesNotContain("README.md", recovery.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Provider.RequestCount); // Query/replay cannot wake a ready command.

        var current = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, current.GetProperty("sessionId").GetString());
        Assert.Equal(before.GetProperty("runId").GetString(), current.GetProperty("runId").GetString());
        var continueCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId,
            expectedSessionId = sessionId,
            expectedRunId = current.GetProperty("runId").GetString(),
            expectedModeRevision = current.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = current.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = current.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(current.GetProperty("routingPolicyRevision")),
            catalogRevision = current.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));
        var continueAck = Ack(fixture.ExchangeTrusted(continueCommand, sessionId,
            current.GetProperty("runId").GetString()));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        await fixture.Provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForInvocationAndTurnAsync(sessionId,
            current.GetProperty("runId").GetString()!, invocationId);

        var requestBodies = fixture.ProviderBodies.ToArray();
        Assert.Equal(2, requestBodies.Length);
        Assert.Contains("Explain the repository content at the path README.md", requestBodies[1],
            StringComparison.Ordinal);
        var invocationEvents = fixture.ReadEvents(sessionId);
        Assert.Single(invocationEvents, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Single(invocationEvents, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId
            && finished.OutcomeCode == "completed");
        Assert.Equal(userInputCountBeforeInvocation + 1,
            invocationEvents.Count(item => item.Payload is UserInputReceived));

        // Replaying the same continuation receipt is idempotent and never schedules another request.
        var duplicate = Ack(fixture.ExchangeTrusted(continueCommand, sessionId,
            current.GetProperty("runId").GetString()));
        Assert.Equal(continueAck.FirstSeq, duplicate.FirstSeq);
        Assert.Equal(continueAck.LastSeq, duplicate.LastSeq);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, duplicate.Outcome?.Kind);
        Assert.Equal(2, fixture.Provider.RequestCount);
    }

    [Fact]
    public async Task Native_workflow_ack_cut_stays_passive_until_explicit_continue_and_duplicate_is_idempotent()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(invocationId,
            includeWorkflowAgentProfile: true);
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        var beforeMode = fixture.QueryJson("humanActionScope");
        var runId = beforeMode.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The normal setup turn did not create a Run.");
        var security = fixture.QueryJson("sessionSecurity");
        Assert.Equal(sessionId, security.GetProperty("sessionId").GetString());
        Assert.True(security.GetProperty("allowOrchestration").GetBoolean());
        Assert.Equal(1, fixture.Provider.RequestCount);

        var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "orq",
            expectedRevision = beforeMode.GetProperty("revision").GetInt64(),
            effort = "ultracode",
            adaptive = true,
            allowedModes = "plan,act,orq",
            maxAgents = 2,
            maxDepth = 1,
            maxTurns = 8,
            maxToolCalls = 12,
            maxElapsedSeconds = 900,
            maxSpendUsd = 1m,
        }));
        var modeAck = Ack(fixture.ExchangeTrusted(mode, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, modeAck.Outcome?.Kind);
        var current = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, current.GetProperty("sessionId").GetString());
        Assert.Equal(runId, current.GetProperty("runId").GetString());
        Assert.Equal("orq", fixture.QueryJson("humanActionScope").GetProperty("mode").GetString());
        var workflow = Assert.Single(current.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:orq-auth");
        Assert.True(workflow.GetProperty("enabled").GetBoolean(),
            $"The real workflow command was not enabled: {workflow.GetProperty("disabledReason").GetString() ?? "no reason"}.");

        var executable = new SystemExecutableResolver().Resolve("dotnet", fixture.Workspace).ResolvedPath;
        var arguments = new[]
        {
            "Inspect the repository and report the entry point",
            "--verify-executable", executable,
            "--verify-argv-json", JsonSerializer.Serialize(new[] { "--version" }),
        };
        var invoke = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = workflow.GetProperty("canonicalCommandId").GetString(),
            arguments,
            expectedSessionId = sessionId,
            expectedRunId = runId,
            expectedModeRevision = current.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = current.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = current.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(current.GetProperty("routingPolicyRevision")),
            catalogRevision = current.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = workflow.GetProperty("sourceFingerprint").GetString(),
        }));
        var invokeAck = Ack(fixture.ExchangeTrusted(invoke, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, invokeAck.Outcome?.Kind);
        Assert.Equal(invocationId, invokeAck.CommandId);
        Assert.Equal(1, fixture.Provider.RequestCount);
        await fixture.FailAckWriteAndStopHostAsync();

        var eventsBeforeRecovery = fixture.ReadEvents(sessionId);
        var admissionEvent = Assert.Single(eventsBeforeRecovery, item => item.Payload is NativeCommandInvocationAdmitted);
        var admission = Assert.IsType<NativeCommandInvocationAdmitted>(admissionEvent.Payload);
        Assert.Equal(invocationId, admission.InvocationId.ToString());
        Assert.Equal("compiledWorkflow", admission.Kind);
        Assert.Equal(sessionId, admission.SessionId.ToString());
        Assert.Equal(runId, admission.RunId?.ToString());
        Assert.Contains(admission.ProtectedPayload, admissionEvent.Envelope.ArtifactRefs);
        Assert.DoesNotContain("Inspect the repository", admissionEvent.Envelope.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(eventsBeforeRecovery, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.DoesNotContain(eventsBeforeRecovery, item => item.Payload is PlanItemAdded);
        var invokeReceipt = fixture.ReadReceipt(invocationId);
        Assert.Equal(CommandReceiptState.Completed, invokeReceipt?.State);
        Assert.Equal("Accepted", invokeReceipt?.OutcomeKind);
        Assert.Null(invokeReceipt?.ExecutionState);
        Assert.Equal(1, fixture.Provider.RequestCount);

        await fixture.ReopenHostAsync();
        var recovered = fixture.QueryJson("command.recovery");
        var ready = Assert.Single(recovered.GetProperty("invocations").EnumerateArray(), item =>
            item.GetProperty("invocationId").GetString() == invocationId);
        Assert.Equal("compiledWorkflow", ready.GetProperty("kind").GetString());
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.DoesNotContain("Inspect the repository", recovered.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Provider.RequestCount); // Recovery reads and replay remain passive.
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);

        var refreshed = fixture.QueryJson("command.catalog");
        var continueCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId,
            expectedSessionId = sessionId,
            expectedRunId = refreshed.GetProperty("runId").GetString(),
            expectedModeRevision = refreshed.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = refreshed.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = refreshed.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(refreshed.GetProperty("routingPolicyRevision")),
            catalogRevision = refreshed.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = workflow.GetProperty("sourceFingerprint").GetString(),
        }));
        var continueAck = Ack(fixture.ExchangeTrusted(continueCommand, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        Assert.Equal(continueCommand.MessageId, continueAck.CommandId);
        await WaitForInvocationStartedAsync(fixture, sessionId, invocationId);
        await WaitForInvocationFinishedAsync(fixture, sessionId, invocationId);

        var countAfterExplicitContinue = fixture.Provider.RequestCount;
        var eventsAfterExplicitContinue = fixture.ReadEvents(sessionId);
        var completionGateCount = eventsAfterExplicitContinue.Count(item => item.Payload is PlanItemAdded added
            && added.Description.StartsWith("Workflow completion gate:", StringComparison.Ordinal));
        var duplicate = Ack(fixture.ExchangeTrusted(continueCommand, sessionId, runId));
        Assert.Equal(continueAck.FirstSeq, duplicate.FirstSeq);
        Assert.Equal(continueAck.LastSeq, duplicate.LastSeq);
        Assert.Equal(continueAck.Outcome?.Kind, duplicate.Outcome?.Kind);
        Assert.Equal(countAfterExplicitContinue, fixture.Provider.RequestCount);
        Assert.Single(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        var eventsAfterDuplicate = fixture.ReadEvents(sessionId);
        Assert.Equal(completionGateCount, eventsAfterDuplicate.Count(item => item.Payload is PlanItemAdded added
            && added.Description.StartsWith("Workflow completion gate:", StringComparison.Ordinal)));
        Assert.Single(eventsAfterDuplicate, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId);
    }

    private static async Task WaitForInvocationStartedAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (fixture.ReadEvents(sessionId).Any(item => item.Payload is NativeCommandInvocationStarted started
                && started.InvocationId.ToString() == invocationId)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Explicit workflow continuation ACK did not start the admitted invocation.");
    }

    private static async Task WaitForInvocationFinishedAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadEvents(sessionId);
            if (events.Any(item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Explicit workflow continuation did not reach a durable terminal invocation event.");
    }

    private static long? NullableJsonInt64(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();

    private sealed partial class NativeCommandHandoffFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private readonly Dictionary<string, string?> _oldExternalRoots;
        private readonly byte[] _key;
        private readonly SemaphoreSlim _providerBodyArrived = new(0);
        private OmniCliRuntime? _runtime;
        private OmniServer? _server;
        private RecoveryDuplex? _duplex;
        private Task<int>? _hostTask;
        private Task<CommandAck>? _ownedCancellationExchange;
        private string? _nonce;
        private bool _disposed;
        private bool _includeNativeChoiceCapability = true;
        private bool _includeNativeWeakSandboxCapability;
        private bool _includeRetentionQueryCapability;
        private SandboxStrength _processSandboxStrength = SandboxStrength.Strong;
        private bool _evidenceCaptureAttempted;
        private Exception? _primaryTestFailure;
        private ArtifactSnapshotLeaseSet? _postCancellationSnapshotLocks;

        private NativeCommandHandoffFixture(string root, string workspace,
            string? oldData, string? oldConfig, string oldDirectory, byte[] key,
            CliEndToEndTests.ScriptedHttpProvider provider, Dictionary<string, string?>? oldExternalRoots = null)
        {
            _root = root;
            Workspace = workspace;
            _oldData = oldData;
            _oldConfig = oldConfig;
            _oldDirectory = oldDirectory;
            _oldExternalRoots = oldExternalRoots ?? new Dictionary<string, string?>();
            _key = key;
            Provider = provider;
            ProviderBodies = new ConcurrentQueue<string>();
            Diagnostics = new ConcurrentQueue<string>();
            DetailedDiagnostics = new ConcurrentQueue<string>();
            CutStream = null!;
        }

        public string Workspace { get; }
        public OmniCliRuntime Runtime => _runtime ?? throw new InvalidOperationException("The native Host runtime is not connected.");
        public OmniServer Server => _server ?? throw new InvalidOperationException("The native Host is not connected.");
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }
        public ConcurrentQueue<string> ProviderBodies { get; }
        public ConcurrentQueue<string> ProviderResponses { get; } = new();
        public ConcurrentQueue<string> Diagnostics { get; }
        public ConcurrentQueue<string> DetailedDiagnostics { get; }
        public ConcurrentQueue<string> ProtocolAudit { get; } = new();
        public bool PreserveRootOnDispose { get; set; }
        public bool PreserveEvidenceOnDispose { get; set; }
        public string? PreservedEvidenceDirectory { get; private set; }
        public Func<CancellationToken, Task>? NativeProcessControlDeferredForTests { get; set; }
        public Action<string>? NativeProcessControlStageForTests { get; set; }
        public Func<PersistedConversationCommand, Task>? AfterCommandExecutionReceiptForTests { get; set; }
        public Func<ISandboxCapabilitiesProbe>? WorkflowSandboxCapabilitiesProbeForTests { get; set; }
        public Func<ISandboxProcessLauncher, ISandboxProcessLauncher>? WorkflowProcessLauncherDecoratorForTests { get; set; }
        public Action? ArtifactSnapshotLeaseContendedForTests { get; set; }
        public AckDeliveryCutStream CutStream { get; private set; }
        public string RootDirectory => _root;
        public string BootstrapKeyForOwnedProcess => Convert.ToHexString(_key);
        public byte[] KeyForTests => _key;
        public string JournalPath => Path.Combine(OmniHost.WorkspaceDataDirectory(
            OmniHost.CreatePlatformPaths(), Workspace), "journal.db");

        public string SaveFailureDiagnostic(string suffix, string contents)
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "OmniCore.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new DirectoryNotFoundException("OmniCore.slnx was not found above the test output directory.");
            var directory = Path.Combine(repository.FullName, "artifacts", "m9-child-process-controls");
            Directory.CreateDirectory(directory);
            var safeSuffix = string.Concat(suffix.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
            var path = Path.Combine(directory, $"diagnostic-{safeSuffix}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.log");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            PreserveRootOnDispose = true;
            return path;
        }

        public async Task WaitForProviderBodiesAsync(int count, CancellationToken cancellationToken)
        {
            while (ProviderBodies.Count < count)
                await _providerBodyArrived.WaitAsync(cancellationToken);
        }

        public string ModelPolicySummary()
        {
            using var policies = OmniHost.CreateModelPolicyService(Path.Combine(_root, "data"));
            var policy = policies.Get(ModelPolicyKey.For("scripted", "scripted-model"), CancellationToken.None);
            return policy is null ? "modelPolicy=missing" : $"modelCategory={policy.Policy.Category}; modelToolMode={policy.Policy.ToolPolicy.Mode}; modelCaps="
                + string.Join(",", policy.Policy.ToolPolicy.CapabilityCeiling.Order());
        }

        public Task<int> ExecuteDelegationAsync(string delegationId, Action<string> diagnostics,
            CancellationToken cancellationToken) => (_runtime
                ?? throw new InvalidOperationException("The native Host runtime is not connected."))
            .DelegationAsync(delegationId, diagnostics, cancellationToken);

        public Task<int> ExecuteHumanShellCommandAsync(string command, Action<string> output,
            Action<string> diagnostics, CancellationToken cancellationToken, bool allowDeferredPermission = false) =>
            (_runtime ?? throw new InvalidOperationException("The native Host runtime is not connected."))
            .ExecuteHumanShellCommandAsync(command, output, diagnostics, cancellationToken,
                allowDeferredPermission: allowDeferredPermission);

        public void ObserveChildTurnResultsForTests(Action<ExplorerTurn.TurnResult> observer) =>
            (_runtime ?? throw new InvalidOperationException("The native Host runtime is not connected."))
                .ChildTurnResultObserverForTests = observer;

        public static async Task<NativeCommandHandoffFixture> StartAsync(string cutCommandId,
            bool includeStrongExtension = false, Func<int, string, string>? providerResponse = null,
            bool includeWorkflowAgentProfile = false,
            Func<int, string, Task<string>>? providerResponseAsync = null,
            string? permissionsYaml = null, bool allowWorkflowPersistentProcess = false,
            bool includeZeroModelPricing = false,
            Func<CancellationToken, Task>? nativeProcessControlDeferredForTests = null,
            Action<string>? nativeProcessControlStageForTests = null,
            bool includeNativeChoiceCapability = true,
            bool includeRetentionQueryCapability = false,
            SandboxStrength processSandboxStrength = SandboxStrength.Strong,
            bool isolateAgentRoots = false,
            bool workflowModel = false,
            bool includeNativeWeakSandboxCapability = false,
            Func<PersistedConversationCommand, Task>? afterCommandExecutionReceiptForTests = null,
            Func<ISandboxCapabilitiesProbe>? workflowSandboxCapabilitiesProbeForTests = null,
            Func<ISandboxProcessLauncher, ISandboxProcessLauncher>? workflowProcessLauncherDecoratorForTests = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "omni-native-command-handoff-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var config = Path.Combine(root, "config");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(data);
            var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
            var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
            var oldDirectory = Environment.CurrentDirectory;
            var externalNames = new[] { "OMNICORE_AGENT_HOME", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
                "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
                "DOTNET_CLI_HOME", "NUGET_PACKAGES", "OMNICORE_TRUSTED_ACTION_BOOTSTRAP", "OPENAI_API_KEY",
                "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY", "OMNI_MODEL", "OMNI_BASE_URL" };
            var oldExternalRoots = isolateAgentRoots
                ? externalNames.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal)
                : new Dictionary<string, string?>();
            var key = RandomNumberGenerator.GetBytes(32);
            var provider = new CliEndToEndTests.ScriptedHttpProvider();
            var fixture = new NativeCommandHandoffFixture(root, workspace, oldData, oldConfig,
                oldDirectory, key, provider, oldExternalRoots);
            fixture._includeNativeChoiceCapability = includeNativeChoiceCapability;
            fixture._includeNativeWeakSandboxCapability = includeNativeWeakSandboxCapability;
            fixture._includeRetentionQueryCapability = includeRetentionQueryCapability;
            fixture._processSandboxStrength = processSandboxStrength;
            fixture.NativeProcessControlDeferredForTests = nativeProcessControlDeferredForTests;
            fixture.NativeProcessControlStageForTests = nativeProcessControlStageForTests;
            fixture.AfterCommandExecutionReceiptForTests = afterCommandExecutionReceiptForTests;
            fixture.WorkflowSandboxCapabilitiesProbeForTests = workflowSandboxCapabilitiesProbeForTests;
            fixture.WorkflowProcessLauncherDecoratorForTests = workflowProcessLauncherDecoratorForTests;
            void RecordProviderBody(string body)
            {
                fixture.ProviderBodies.Enqueue(body);
                fixture._providerBodyArrived.Release();
            }
            if (providerResponseAsync is not null)
                provider.RespondWithAsync(async (index, body) =>
                {
                    RecordProviderBody(body);
                    return await providerResponseAsync(index, body).ConfigureAwait(false);
                });
            else
                provider.RespondWith((index, body) =>
                {
                    RecordProviderBody(body);
                    return providerResponse?.Invoke(index, body)
                        ?? CliEndToEndTests.ResponsesTextResponse("The local native-command fixture completed this Turn.");
                });
            try
            {
                File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                    providers:
                      scripted:
                        family: OpenAIResponses
                        profile: api
                        baseUrl: {{provider.BaseUrl}}
                        auth: none
                        billingMode: Local
                    """, new UTF8Encoding(false));
                var modelConfiguration = workflowModel
                    ? "models:\n  scripted-model:\n    provider: scripted\n    context: 4096\n    recommendedUsableContext: 3072\n    maxOutput: 256\n    parametersBillions: 100\n    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n    reasoning:\n      supported: true\n      effortLevels: [high, adaptive]\n      replayPolicy: RequiredWithTools\n"
                    : "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    recommendedUsableContext: 24000\n    maxOutput: 512\n    parametersBillions: 100\n";
                if (includeZeroModelPricing && !workflowModel)
                    modelConfiguration += "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n";
                File.WriteAllText(Path.Combine(config, "models.yaml"), modelConfiguration, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"), permissionsYaml ?? (includeStrongExtension
                    ? "profile: balanced\nrules:\n  tool.search: allow\n  ext.fixture.native.mutate: ask\n"
                    : "profile: balanced\nrules:\n  tool.search: allow\n"), new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                if (isolateAgentRoots)
                {
                    var agentHome = Path.Combine(root, "agent-home");
                    var home = Path.Combine(root, "home");
                    foreach (var path in new[] { agentHome, home, Path.Combine(agentHome, ".codex"),
                        Path.Combine(agentHome, ".claude"), Path.Combine(agentHome, ".config"),
                        Path.Combine(agentHome, ".config", "opencode"), Path.Combine(home, ".dotnet"),
                        Path.Combine(home, "AppData", "Roaming"), Path.Combine(home, "AppData", "Local") })
                        Directory.CreateDirectory(path);
                    Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", agentHome);
                    Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(agentHome, ".codex"));
                    Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(agentHome, ".claude"));
                    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(agentHome, ".config"));
                    Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", Path.Combine(agentHome, ".config", "opencode"));
                    Environment.SetEnvironmentVariable("HOME", home);
                    Environment.SetEnvironmentVariable("USERPROFILE", home);
                    Environment.SetEnvironmentVariable("APPDATA", Path.Combine(home, "AppData", "Roaming"));
                    Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(home, "AppData", "Local"));
                    Environment.SetEnvironmentVariable("DOTNET_CLI_HOME", Path.Combine(home, ".dotnet"));
                    Environment.SetEnvironmentVariable("NUGET_PACKAGES", Path.Combine(root, "nuget-packages"));
                    Environment.SetEnvironmentVariable("OMNICORE_TRUSTED_ACTION_BOOTSTRAP", null);
                    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
                    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
                    Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
                    Environment.SetEnvironmentVariable("OMNI_MODEL", null);
                    Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
                }
                Environment.CurrentDirectory = workspace;
                if (includeStrongExtension) fixture.ConfigureStrongExtensionPackage();
                if (includeWorkflowAgentProfile) fixture.ConfigureWorkflowAgentProfile(config, allowWorkflowPersistentProcess);
                using (var policies = OmniHost.CreateModelPolicyService(data))
                    policies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                        ModelPolicyPresets.FullAgent(), CancellationToken.None);
                var defaults = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
                defaults.Set(defaults.Read().Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                fixture.StartRuntimeAndHost(cutCommandId);
                fixture.Negotiate(includeNativeChoiceCapability);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void ConfigureWorkflowAgentProfile(string config, bool allowPersistentProcess)
        {
            var profileId = Guid.NewGuid();
            var executable = new SystemExecutableResolver().Resolve("dotnet", Workspace).ResolvedPath
                .Replace("\\", "/", StringComparison.Ordinal);
            var processRules = $"        - executablePattern: '{executable}'\n"
                + "          argvPatterns: [\"--version\"]\n          decision: Allow";
            if (allowPersistentProcess)
                processRules += "\n        - executablePattern: '**/OmniCore.SandboxTestProcess.exe'\n"
                    + "          argvPatterns: [\"stdin-state\"]\n          decision: Allow";
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profileId:D}}
                agentProfiles:
                  workflow:
                    id: {{profileId:D}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process:
                {{processRules}}
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                """, new UTF8Encoding(false));
        }

        public string CreateEmptySession()
        {
            var initialScope = QueryJson("humanActionScope");
            var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
            var createAck = Ack(ExchangeTrusted(create, NullableJsonString(initialScope.GetProperty("sessionId")),
                NullableJsonString(initialScope.GetProperty("runId"))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, createAck.Outcome?.Kind);
            var selection = QueryJson("session.model.selection");
            return selection.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("The new Session ID was not returned.");
        }

        public async Task<string> CreateSessionAndQuiescentRunAsync(string mode = "plan")
        {
            var session = CreateEmptySession();
            var selection = QueryJson("session.model.selection");
            var start = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "A short setup turn before the native command.",
                mode,
                expectedRevision = (long?)null,
                expectedPolicyRevision = selection.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var startAck = Ack(ExchangeTrusted(start, session, null));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
            await Provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await WaitForTurnCompletedAsync(session);
            await WaitForReceiptExecutionAsync(start.MessageId, "completed");
            return session;
        }

        public JsonElement QueryJson(string name)
        {
            var query = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new { operation = "query", name }));
            var response = Exchange(query);
            using var payload = JsonDocument.Parse(response.PayloadJson);
            using var result = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
            return result.RootElement.Clone();
        }

        public WireEnvelope ExchangeTrusted(WireEnvelope command, string? sessionId, string? runId) =>
            Exchange(TrustedActionEnvelope(command, _key, _nonce!, Workspace, sessionId, runId));

        public WireEnvelope ExchangeTrustedWithProofScope(WireEnvelope command, string proofWorkspace,
            string? proofSessionId, string? proofRunId) =>
            Exchange(TrustedActionEnvelope(command, _key, _nonce!, proofWorkspace, proofSessionId, proofRunId));

        public void RecordProtocolAudit(string operation, string? sessionId, string? runId, string result) =>
            ProtocolAudit.Enqueue(JsonSerializer.Serialize(new
            {
                operation, sessionId, runId, result, actionProof = "<redacted>",
            }));

        public void RecordPrimaryTestFailure(Exception failure) => _primaryTestFailure ??= failure;

        public sealed record WorkflowTimeoutSnapshot(string Path, string Json);

        public sealed record WorkflowCancellationObservation(bool Quiesced, string SendStatus, string AckOutcome);

        public WorkflowTimeoutSnapshot CaptureWorkflowTimeoutSnapshot(string sessionId, string invocationId,
            ConcurrentQueue<string> capabilityObservations,
            ConcurrentQueue<WorkflowProcessBoundaryObservation> processLauncherObservations)
        {
            string json;
            try
            {
                var events = ReadEvents(sessionId);
                var receipt = ReadReceipt(invocationId);
                var snapshot = new
                {
                    invocationId,
                    receipt = new { receipt?.State, receipt?.OutcomeKind, receipt?.ExecutionState, receipt?.ExecutionCode,
                        receipt?.ExecutionRunId, receipt?.FirstSequence, receipt?.LastSequence },
                    providerRequests = Provider.RequestCount,
                    sandboxCapabilityProbeResults = capabilityObservations.TakeLast(8)
                        .Select(value => ClipDiagnostic(value, 96)).ToArray(),
                    processLauncherObservations = processLauncherObservations.TakeLast(32).ToArray(),
                    recentEvents = events.TakeLast(16).Select(item => new
                    {
                        sequence = item.Envelope.Sequence,
                        type = item.Payload.GetType().Name,
                        runId = item.Envelope.RunId?.ToString(),
                        taskId = item.Envelope.TaskId?.ToString(),
                        laneId = item.Envelope.LaneId?.ToString(),
                        turnId = item.Envelope.TurnId?.ToString(),
                        toolCallId = item.Envelope.ToolCallId?.ToString(),
                        detail = item.Payload switch
                        {
                            ToolCallRequested call => "tool=" + call.ToolName,
                            ProcessSessionStateRecorded state => "processState=" + state.State
                                + ";source=" + (state.SourceToolId ?? "none")
                                + ";exit=" + (state.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")
                                + ";outputSequence=" + state.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            InteractionRequested interaction => "interaction=" + interaction.Kind,
                            InteractionResolved interaction => "choice=" + interaction.OptionId + ";cause=" + interaction.Cause,
                            PermissionEvaluated permission => "permission=" + permission.Decision,
                            NativeCommandInvocationStarted started => "invocationKind=" + started.Kind,
                            NativeCommandInvocationFinished finished => "invocationOutcome=" + finished.OutcomeCode,
                            _ => string.Empty,
                        },
                    }).ToArray(),
                    diagnostics = Diagnostics.TakeLast(6).Select(value => ClipDiagnostic(value, 96)).ToArray(),
                };
                json = JsonSerializer.Serialize(snapshot);
                if (Encoding.UTF8.GetByteCount(json) > 12 * 1024)
                {
                    var compact = JsonSerializer.Serialize(new
                    {
                        invocationId,
                        receipt = new { receipt?.State, receipt?.OutcomeKind, receipt?.ExecutionState,
                            receipt?.ExecutionCode, receipt?.ExecutionRunId },
                        providerRequests = Provider.RequestCount,
                        sandboxCapabilityProbeResults = capabilityObservations.TakeLast(4)
                            .Select(value => ClipDiagnostic(value, 96)).ToArray(),
                        processLauncherObservations = processLauncherObservations.TakeLast(16).ToArray(),
                        recentEvents = events.TakeLast(8).Select(item => new
                        {
                            sequence = item.Envelope.Sequence,
                            type = item.Payload.GetType().Name,
                            toolCallId = item.Envelope.ToolCallId?.ToString(),
                        }).ToArray(),
                        diagnostics = Diagnostics.TakeLast(4).Select(value => ClipDiagnostic(value, 96)).ToArray(),
                        snapshotTruncated = true,
                    });
                    json = Encoding.UTF8.GetByteCount(compact) <= 12 * 1024 ? compact : JsonSerializer.Serialize(new
                    {
                        invocationId,
                        providerRequests = Provider.RequestCount,
                        processLauncherObservations = processLauncherObservations.TakeLast(8).ToArray(),
                        snapshotTruncated = true,
                    });
                }
            }
            catch (Exception exception)
            {
                json = JsonSerializer.Serialize(new
                {
                    invocationId,
                    providerRequests = Provider.RequestCount,
                    snapshotReadErrorType = exception.GetType().Name,
                    snapshotTruncated = true,
                });
            }
            var path = Path.Combine(_root, "workflow-timeout-snapshot.json");
            try { File.WriteAllText(path, json, new UTF8Encoding(false)); }
            catch (Exception exception)
            {
                json = JsonSerializer.Serialize(new
                {
                    invocationId,
                    providerRequests = Provider.RequestCount,
                    snapshotWriteErrorType = exception.GetType().Name,
                    snapshotTruncated = true,
                });
            }
            return new WorkflowTimeoutSnapshot(path, json);
        }

        public async Task<WorkflowCancellationObservation> CancelOwnedWorkflowAndWaitAsync(string sessionId, string runId,
            string invocationId, TaskCompletionSource releaseInvocationReceipt)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(35);
            releaseInvocationReceipt.TrySetResult();
            var cancel = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new { cmd = "run.cancel", runId }));
            var signedCancel = TrustedActionEnvelope(cancel, _key, _nonce!, Workspace, sessionId, runId);
            string sendStatus;
            string ackOutcome;
            var cancelTask = Task.Run(() => Ack(Exchange(signedCancel)));
            _ownedCancellationExchange = cancelTask;
            try
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var acknowledgement = await cancelTask.WaitAsync(remaining, CancellationToken.None).ConfigureAwait(false);
                _ownedCancellationExchange = null;
                sendStatus = "acknowledged";
                ackOutcome = acknowledgement.Outcome?.Kind.ToString() ?? "none";
                RecordProtocolAudit("run.cancel after workflow timeout", sessionId, runId,
                    "signed cancellation sent only to the fixture Run; outcome=" + ackOutcome);
            }
            catch (Exception exception)
            {
                sendStatus = "failed_" + exception.GetType().Name;
                ackOutcome = "none";
                AbortOwnedStdioTransport();
                try { await cancelTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
                catch { /* transport is closed below; preserve the primary timeout and owned root */ }
                if (cancelTask.IsCompleted) _ownedCancellationExchange = null;
                PreserveEvidenceOnDispose = false;
                PreserveRootOnDispose = true;
                return new WorkflowCancellationObservation(false, sendStatus, ackOutcome);
            }

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var receipt = ReadReceipt(invocationId);
                    var terminal = receipt?.ExecutionState is "completed" or "failed" or "cancelled" or "unknown";
                    var events = ReadEvents(sessionId);
                    var invocationFinished = events.Any(item => item.Payload is NativeCommandInvocationFinished finished
                        && finished.InvocationId.ToString() == invocationId);
                    var runTerminal = events.Any(item => item.Payload switch
                    {
                        RunCancelled cancelled => cancelled.RunId.ToString() == runId,
                        RunFailed failed => failed.RunId.ToString() == runId,
                        RunCompleted completed => completed.RunId.ToString() == runId,
                        _ => false,
                    });
                    if ((terminal || invocationFinished) && runTerminal)
                    {
                        var remaining = deadline - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                        try
                        {
                            _postCancellationSnapshotLocks = AcquireArtifactSnapshotLocks(remaining);
                            return new WorkflowCancellationObservation(true, sendStatus, ackOutcome);
                        }
                        catch (Exception exception)
                        {
                            sendStatus += "_lease_" + exception.GetType().Name;
                            break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    sendStatus += "_receipt_" + exception.GetType().Name;
                    break;
                }
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
            AbortOwnedStdioTransport();
            try
            {
                await cancelTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                _ownedCancellationExchange = null;
            }
            catch (Exception exception)
            {
                sendStatus += "_transport_" + exception.GetType().Name;
            }
            PreserveEvidenceOnDispose = false;
            PreserveRootOnDispose = true;
            return new WorkflowCancellationObservation(false, sendStatus, ackOutcome);
        }

        private void AbortOwnedStdioTransport()
        {
            try { _duplex?.CompleteClientInput(); } catch { }
            try { CutStream?.Dispose(); } catch { }
            try { _duplex?.Dispose(); } catch { }
        }

        private static string ClipDiagnostic(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        public void RecordProviderResponse(int requestIndex, string response) =>
            ProviderResponses.Enqueue(JsonSerializer.Serialize(new { requestIndex, response }));

        public async Task StopHostAsync()
        {
            _duplex?.CompleteClientInput();
            if (_hostTask is not null)
            {
                var exitCode = await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
                if (exitCode != 0) throw new InvalidOperationException("Owned stdio Host exited with code " + exitCode + ".");
                _hostTask = null;
            }
            CutStream?.Dispose();
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
            ClearOwnedSqlitePools();
        }

        internal void ClearOwnedSqlitePools()
        {
            ClearPool(Path.Combine(_root, "data", "user.db"));
            ClearPool(Path.Combine(_root, "data", "spend-reservations.db"));
            ClearPool(JournalPath);
            var mailboxPath = Path.Combine(_root, "data", "companion", "coordinator.db");
            if (File.Exists(mailboxPath))
            {
                // The shared mailbox uses a separate pool with a one-second timeout.
                // Release only this fixture's idle connections after its Host has stopped.
                using var mailbox = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = mailboxPath, DefaultTimeout = 1 }.ToString());
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(mailbox);
            }

            static void ClearPool(string path)
            {
                if (!File.Exists(path)) return;
                // Match SqliteModelPolicyStore/SqliteEventStore's literal connection string so
                // Microsoft.Data.Sqlite clears the pool that actually owns these fixture files.
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + path);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
        }

        public string PreserveEvidence(string name)
        {
            _evidenceCaptureAttempted = true;
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "OmniCore.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new DirectoryNotFoundException("OmniCore.slnx was not found above the test output directory.");
            var safeName = string.Concat(name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
            var destination = Path.Combine(repository.FullName, "artifacts", "m9-worktree-workflow-provision",
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N"), "fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var heldSnapshotLocks = _postCancellationSnapshotLocks;
            _postCancellationSnapshotLocks = null;
            using var artifactSnapshotLocks = heldSnapshotLocks ?? AcquireArtifactSnapshotLocks();
            CopyFixtureDirectory(_root, destination, artifactSnapshotLocks.Paths);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "stdio-actions-redacted.jsonl"),
                ProtocolAudit, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "provider-requests-fixture.jsonl"),
                ProviderBodies.Select(body => JsonSerializer.Serialize(body)), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "provider-responses-fixture.jsonl"),
                ProviderResponses, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "diagnostics-safe.txt"),
                Diagnostics, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(destination)!, "capture.txt"),
                "Owned fixture copy after stdio Host/runtime shutdown. Config uses auth:none and local scripted HTTP provider. "
                + "All agent HOME roots are temporary fixture paths. Trusted action proof values and API credentials are not captured. "
                + $"Held {artifactSnapshotLocks.Paths.Count} exact zero-length .artifact-gc.lease locks exclusively through capture; "
                + "those ephemeral lock files alone were excluded: "
                + string.Join(",", artifactSnapshotLocks.Paths.Select(path => Path.GetRelativePath(_root, path)))
                + "; durable fixture files were copied.\n",
                new UTF8Encoding(false));
            var binaryDirectory = Path.Combine(Path.GetDirectoryName(destination)!, "bin");
            Directory.CreateDirectory(binaryDirectory);
            var binaryFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".json" or ".pdb")
                .Order(StringComparer.Ordinal).ToArray();
            foreach (var file in binaryFiles)
                File.Copy(file, Path.Combine(binaryDirectory, Path.GetFileName(file)), overwrite: false);
            var manifest = binaryFiles.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                + "  " + Path.GetFileName(file));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "test-output-binaries.sha256"),
                manifest, new UTF8Encoding(false));
            PreservedEvidenceDirectory = Path.GetDirectoryName(destination)!;
            return PreservedEvidenceDirectory;
        }

        private sealed class ArtifactSnapshotLeaseSet(List<FileStream> handles, HashSet<string> paths) : IDisposable
        {
            public IReadOnlySet<string> Paths { get; } = paths;
            public void Dispose()
            {
                List<Exception>? failures = null;
                foreach (var handle in handles)
                {
                    try { handle.Dispose(); }
                    catch (Exception failure) { (failures ??= []).Add(failure); }
                }
                if (failures is not null)
                    throw new AggregateException("Fixture artifact snapshot locks could not all be released.", failures);
            }
        }

        private ArtifactSnapshotLeaseSet AcquireArtifactSnapshotLocks(TimeSpan? timeout = null)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var fixtureRoot = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var expectedWorkspaceData = Path.GetFullPath(OmniHost.WorkspaceDataDirectory(
                OmniHost.CreatePlatformPaths(), Workspace));
            var workspacesRoot = Path.GetFullPath(Path.Combine(_root, "data", "workspaces"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!expectedWorkspaceData.StartsWith(workspacesRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("The workspace CAS path escaped the owned fixture data/workspaces root.");
            var leasePath = Path.GetFullPath(Path.Combine(expectedWorkspaceData, ".artifact-gc.lease"));
            if (!leasePath.StartsWith(fixtureRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("The workspace artifact lease escaped the owned fixture root.");
            var handles = new List<FileStream>();
            var waitBudget = timeout ?? TimeSpan.FromSeconds(20);
            if (waitBudget <= TimeSpan.Zero)
                throw new TimeoutException("No time remained to acquire the fixture workspace CAS lease.");
            var deadline = DateTimeOffset.UtcNow + waitBudget;
            IOException? lastBusyFailure = null;
            try
            {
                while (true)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                        throw new IOException("The workspace artifact publication lease did not become quiescent before the evidence snapshot: "
                            + Path.GetRelativePath(_root, leasePath), lastBusyFailure);
                    var attributes = File.Exists(leasePath) ? File.GetAttributes(leasePath) : FileAttributes.Normal;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Fixture artifact lease path is a reparse point: "
                            + Path.GetRelativePath(_root, leasePath));

                    FileStream handle;
                    try { handle = new FileStream(leasePath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException failure)
                    {
                        lastBusyFailure = failure;
                        ArtifactSnapshotLeaseContendedForTests?.Invoke();
                        if (DateTimeOffset.UtcNow >= deadline)
                            throw new IOException("The workspace artifact publication lease did not become quiescent before the evidence snapshot: "
                                + Path.GetRelativePath(_root, leasePath), failure);
                        Task.Delay(25).GetAwaiter().GetResult();
                        continue;
                    }
                    if (handle.Length != 0)
                    {
                        handle.Dispose();
                        throw new InvalidDataException("Fixture artifact lease file is not empty: "
                            + Path.GetRelativePath(_root, leasePath));
                    }
                    handles.Add(handle);
                    var excluded = new HashSet<string>(comparer) { leasePath };
                    return new ArtifactSnapshotLeaseSet(handles, excluded);
                }
            }
            catch
            {
                foreach (var handle in handles)
                {
                    try { handle.Dispose(); }
                    catch { /* Preserve the snapshot acquisition failure; the root remains owned for diagnosis. */ }
                }
                throw;
            }
        }

        private static void CopyFixtureDirectory(string source, string destination, IReadOnlySet<string> excludedFiles)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                if (excludedFiles.Contains(Path.GetFullPath(file))) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyFixtureDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), excludedFiles);
        }

        public WireEnvelope Exchange(WireEnvelope request)
        {
            var duplex = _duplex ?? throw new InvalidOperationException("The native Host stream is not open.");
            WireFrame.Write(duplex.ClientInput, request.ToJson());
            var response = WireFrame.Read(duplex.ClientOutput)
                ?? throw new EndOfStreamException("The native Host closed before returning a response frame.");
            return WireEnvelope.FromJson(response);
        }

        public IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> ReadEvents(string sessionId)
        {
            using var store = new SqliteEventStore(JournalPath, readOnly: true);
            var codecs = EventCodecs.Create();
            return store.ReadFrom(SessionId.Parse(sessionId), 1)
                .Select(envelope => (envelope, codecs.Decode(envelope))).ToArray();
        }

        public CommandReceipt? ReadReceipt(string commandId)
        {
            using var store = new SqliteEventStore(JournalPath, readOnly: true);
            return store.ReadCommandReceipt(commandId);
        }

        public async Task WaitForInvocationAndTurnAsync(string sessionId, string runId, string invocationId,
            int minimumCompletedTurns = 2, string expectedOutcomeCode = "completed")
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents(sessionId);
                var started = events.Any(item => item.Payload is NativeCommandInvocationStarted command
                    && command.InvocationId.ToString() == invocationId);
                var finished = events.Any(item => item.Payload is NativeCommandInvocationFinished command
                    && command.InvocationId.ToString() == invocationId && command.OutcomeCode == expectedOutcomeCode);
                var expectedRun = RunId.Parse(runId);
                var turns = events.Where(item => item.Envelope.RunId == expectedRun && item.Payload is TurnStarted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                var completedTurns = events.Where(item => item.Envelope.RunId == expectedRun
                        && item.Payload is TurnCompleted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                if (started && finished && turns.Intersect(completedTurns).Count() >= minimumCompletedTurns) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var finalEvents = ReadEvents(sessionId);
            var eventSummary = string.Join(",", finalEvents.Select(item =>
                $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}:run={item.Envelope.RunId?.ToString() ?? "-"}:turn={item.Envelope.TurnId?.ToString() ?? "-"}"));
            var finalReceipt = ReadReceipt(invocationId);
            throw new TimeoutException($"The native command did not complete its durable Run Turn. providerRequests={Provider.RequestCount}; "
                + $"started={finalEvents.Any(item => item.Payload is NativeCommandInvocationStarted command && command.InvocationId.ToString() == invocationId)}; "
                + $"finished={finalEvents.Any(item => item.Payload is NativeCommandInvocationFinished command && command.InvocationId.ToString() == invocationId)}; "
                + $"receipt={finalReceipt?.State}/{finalReceipt?.OutcomeKind}/{finalReceipt?.ExecutionState}; "
                + $"diagnostics={string.Join(",", Diagnostics)}; events={eventSummary}");
        }

        private static string SafeDiagnostic(string value) => value.Length <= 160
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')
                ? value : "non_code_diagnostic";

        public async Task FailAckWriteAndStopHostAsync()
        {
            var hostResult = await (_hostTask ?? throw new InvalidOperationException("Host task missing."))
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(2, hostResult);
            _hostTask = null;
            CutStream.Dispose();
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
        }

        public Task ReopenHostAsync()
        {
            StartRuntimeAndHost(null);
            Negotiate(_includeNativeChoiceCapability);
            return Task.CompletedTask;
        }

        public async Task RestartHostAsync()
        {
            _duplex?.CompleteClientInput();
            if (_hostTask is not null)
            {
                var hostResult = await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(0, hostResult);
            }
            _hostTask = null;
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
            StartRuntimeAndHost(null);
            Negotiate(_includeNativeChoiceCapability);
        }

        public async Task WaitForTurnCompletedAsync(string sessionId, long afterSequence = 0)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadEvents(sessionId).Any(item => item.Envelope.Sequence > afterSequence
                    && item.Payload is TurnCompleted)) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The setup conversation Turn did not complete.");
        }

        public async Task WaitForReceiptExecutionAsync(string commandId, string expectedState)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadReceipt(commandId)?.ExecutionState == expectedState) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var receipt = ReadReceipt(commandId);
            throw new TimeoutException($"The setup receipt did not reach {expectedState}: "
                + $"state={receipt?.State}; outcome={receipt?.OutcomeKind}; execution={receipt?.ExecutionState}/{receipt?.ExecutionCode}.");
        }

        private void StartRuntimeAndHost(string? cutCommandId)
        {
            var runtime = OmniCliRuntime.Create(Workspace);
            runtime.ProcessSandboxStrengthForTests = _processSandboxStrength;
            runtime.WorkflowSandboxCapabilitiesProbeForTests = WorkflowSandboxCapabilitiesProbeForTests;
            runtime.WorkflowProcessLauncherDecoratorForTests = WorkflowProcessLauncherDecoratorForTests;
            runtime.UseConsoleInput = false;
            runtime.QuestionnaireInput = null;
            runtime.PrepareConversationSessionPolicy();
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            _server = Assert.IsType<OmniServer>(client);
            var duplex = new RecoveryDuplex();
            _runtime = runtime;
            _duplex = duplex;
            Stream output = duplex.HostOutput;
            if (cutCommandId is not null)
            {
                CutStream = new AckDeliveryCutStream(output, cutCommandId);
                output = CutStream;
            }
            _hostTask = Task.Run(() => StdioProtocolHost.RunWithTrustedActions(client,
                duplex.HostInput, output, TextWriter.Null, _key,
                runPersistedConversation: async (admitted, token) =>
                    await runtime.ExecutePersistedConversationAsync(admitted,
                        diagnostic =>
                        {
                            DetailedDiagnostics.Enqueue(diagnostic);
                            Diagnostics.Enqueue(SafeDiagnostic(diagnostic));
                        }, token),
                nativeProcessControlDeferredForTests: NativeProcessControlDeferredForTests,
                nativeProcessControlStageForTests: NativeProcessControlStageForTests,
                afterCommandExecutionReceiptForTests: AfterCommandExecutionReceiptForTests));
        }

        private void Negotiate(bool includeNativeChoiceCapability = true)
        {
            var capabilities = new List<string>
            {
                "query.commandCatalog", "query.commandRecovery", "query.processSessions",
                "command.process.control.user", "command.invoke.user", "command.invoke.continue.user",
                "command.run.mode.select.user",
            };
            if (_includeRetentionQueryCapability) capabilities.Add("query.worktreeRetention");
            if (includeNativeChoiceCapability)
                capabilities.Add("command.conversation.interaction.choice.respond.user");
            if (_includeNativeWeakSandboxCapability)
                capabilities.Add("command.conversation.weak-sandbox.respond.user");
            var hello = Exchange(WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":"
                + JsonSerializer.Serialize("native-command-hello-" + Guid.NewGuid().ToString("N"))
                + ",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":"
                + JsonSerializer.Serialize(capabilities) + "}}"));
            using var json = JsonDocument.Parse(hello.PayloadJson);
            if (!json.RootElement.TryGetProperty("trustedActionNonce", out var nonce))
            {
                var errorCode = json.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
                var keys = string.Join(",", json.RootElement.EnumerateObject().Select(property => property.Name));
                throw new InvalidOperationException($"Trusted action nonce was not negotiated (messageType={hello.MessageType}, errorCode={errorCode ?? "none"}, payloadKeys={keys}).");
            }
            _nonce = nonce.GetString()
                ?? throw new InvalidOperationException("Trusted action nonce was null.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            var cleanupFailures = new List<Exception>();
            var hostStopped = _hostTask is null;
            void Capture(Action action)
            {
                try { action(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try
            {
                if (_ownedCancellationExchange is { } cancellationExchange)
                {
                    AbortOwnedStdioTransport();
                    try
                    {
                        await cancellationExchange.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                        _ownedCancellationExchange = null;
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            "Owned timeout-cancellation exchange did not quiesce after closing its fixture transport.", exception));
                        PreserveRootOnDispose = true;
                        PreserveEvidenceOnDispose = false;
                    }
                }
                Capture(() => _duplex?.CompleteClientInput());
                if (_hostTask is not null)
                {
                    try
                    {
                        var exitCode = await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
                        hostStopped = true;
                        _hostTask = null;
                        if (exitCode != 0) cleanupFailures.Add(new InvalidOperationException(
                            "Owned stdio Host exited with code " + exitCode + "."));
                    }
                    catch (Exception exception) { cleanupFailures.Add(exception); }
                }
                Capture(() => CutStream?.Dispose());
                Capture(() => _duplex?.Dispose());
                var runtime = _runtime;
                _runtime = null;
                Capture(() => runtime?.Dispose());
                Capture(ClearOwnedSqlitePools);
                if (PreserveEvidenceOnDispose && PreservedEvidenceDirectory is null
                    && !_evidenceCaptureAttempted && hostStopped)
                {
                    try
                    {
                        PreserveEvidence("workflow-provision-cleanup");
                        PreserveRootOnDispose = false;
                    }
                    catch (Exception exception) { cleanupFailures.Add(exception); }
                }
                try { await Provider.DisposeAsync(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            finally
            {
                Capture(_providerBodyArrived.Dispose);
                var heldSnapshotLocks = _postCancellationSnapshotLocks;
                _postCancellationSnapshotLocks = null;
                if (heldSnapshotLocks is not null) Capture(heldSnapshotLocks.Dispose);
                CryptographicOperations.ZeroMemory(_key);
                Capture(() => Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData));
                Capture(() => Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig));
                foreach (var value in _oldExternalRoots)
                    Capture(() => Environment.SetEnvironmentVariable(value.Key, value.Value));
                Capture(() => Environment.CurrentDirectory = _oldDirectory);
                if (!PreserveRootOnDispose && cleanupFailures.Count == 0)
                {
                    Capture(DeleteOwnedRoot);
                    if (cleanupFailures.Count > 0) PreserveRootOnDispose = true;
                }
                else if (cleanupFailures.Count > 0)
                    PreserveRootOnDispose = true;
            }
            if (_primaryTestFailure is not null && cleanupFailures.Count > 0)
                throw new AggregateException(
                    "The native command test body and fixture cleanup both failed; the first inner exception is the test-body failure.",
                    new[] { _primaryTestFailure }.Concat(cleanupFailures));
            if (cleanupFailures.Count == 1)
                throw new InvalidOperationException(
                    "Native command fixture cleanup failed after attempting all owned-resource restorations.",
                    cleanupFailures[0]);
            if (cleanupFailures.Count > 1)
                throw new AggregateException(
                    "Native command fixture cleanup had multiple failures after attempting all owned-resource restorations.",
                    cleanupFailures);

            void DeleteOwnedRoot()
            {
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var fixtureRoot = Path.GetFullPath(_root);
                if (!fixtureRoot.StartsWith(tempRoot, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !Path.GetFileName(fixtureRoot).StartsWith("omni-native-command-handoff-",
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to delete a path outside the owned native-command fixture root.");

                var directories = new Stack<string>();
                directories.Push(fixtureRoot);
                while (directories.TryPop(out var directory))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Push(entry);
                            continue;
                        }
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }
}
