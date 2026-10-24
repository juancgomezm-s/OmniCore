using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    [Fact]
    public async Task Native_stdio_orq_workflow_publishes_real_owned_implementation_worktree_and_passive_queries_resolve_it()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        var program = "public static class Program\n{\n    public static int Main()\n    {\n        /* FEATURE_MODULE_HOOK */\n        return 0;\n    }\n}\n";
        const string implementation = "public static class FeatureModule { public static bool IsInitialized { get; private set; } public static void Initialize() => IsInitialized = true; }\n";
        var programVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(program))).ToLowerInvariant();
        var fixtureWorkspace = string.Empty;
        NativeCommandHandoffFixture? responseAuditFixture = null;
        string WorkflowResponse(int index, string _)
        {
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
            permissionsYaml: "profile: autonomous\nrules:\n  dyn.core.verify_integration: allow\n",
            includeZeroModelPricing: true, isolateAgentRoots: true, workflowModel: true,
            includeNativeWeakSandboxCapability: true);
        fixtureWorkspace = fixture.Workspace;
        responseAuditFixture = fixture;
        fixture.PreserveRootOnDispose = true;
        fixture.PreserveEvidenceOnDispose = true;
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
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, modeAck.Outcome?.Kind);
        fixture.RecordProtocolAudit("run.mode.select", sessionId, runId, "accepted; exact revision; HMAC redacted");
        var catalog = fixture.QueryJson("command.catalog");
        var workflow = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:orq-auth");
        Assert.True(workflow.GetProperty("enabled").GetBoolean(),
            "The compiled workflow must be enabled by the signed Orchestrate authorization.");

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
        await WaitForWorkflowInvocationAsync(fixture, sessionId, invocationId);

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
        var weakConsentRequest = Assert.Single(eventsAfterWorkflow, item => item.Payload is InteractionRequested request
            && request.Kind == InteractionKind.WeakSandboxConsent);
        var weakConsent = Assert.IsType<InteractionRequested>(weakConsentRequest.Payload);
        var weakConsentResolved = Assert.Single(eventsAfterWorkflow, item => item.Payload is InteractionResolved response
            && response.InteractionId == weakConsent.InteractionId);
        Assert.Equal("consent_once", Assert.IsType<InteractionResolved>(weakConsentResolved.Payload).OptionId);
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

        fixture.RecordProtocolAudit("worktree.preview", sessionId, runId, "passive; no journal delta");
        await fixture.StopHostAsync();
        var artifactDirectory = fixture.PreserveEvidence("workflow-provision");
        fixture.PreserveRootOnDispose = false;
        Assert.True(Directory.Exists(Path.Combine(artifactDirectory, "fixture", "workspace", ".git")));
    }

    private static async Task WaitForWorkflowInvocationAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        var weakSandboxConsentResponded = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadEvents(sessionId);
            if (!weakSandboxConsentResponded)
            {
                var requestEvent = events.LastOrDefault(item => item.Payload is InteractionRequested request
                    && request.Kind == InteractionKind.WeakSandboxConsent
                    && !events.Any(resolved => resolved.Payload is InteractionResolved response
                        && response.InteractionId == request.InteractionId));
                if (requestEvent.Payload is InteractionRequested weakConsent)
                {
                    var envelope = requestEvent.Envelope;
                    var runId = envelope.RunId?.ToString()
                        ?? throw new InvalidDataException("Weak sandbox consent has no Run scope.");
                    var taskId = envelope.TaskId?.ToString()
                        ?? throw new InvalidDataException("Weak sandbox consent has no Task scope.");
                    var laneId = envelope.LaneId?.ToString()
                        ?? throw new InvalidDataException("Weak sandbox consent has no Lane scope.");
                    var turnId = envelope.TurnId?.ToString()
                        ?? throw new InvalidDataException("Weak sandbox consent has no Turn scope.");
                    var toolCallId = envelope.ToolCallId?.ToString()
                        ?? throw new InvalidDataException("Weak sandbox consent has no ToolCall scope.");
                    var policyRevision = fixture.QueryJson("sessionSecurity").GetProperty("revision").GetInt64();
                    var response = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
                    {
                        cmd = "interaction.respond", responseType = "weakSandboxConsent",
                        expectedRunId = runId, taskId, laneId, turnId, toolCallId,
                        invocationId, interactionId = weakConsent.InteractionId.ToString(),
                        optionId = "consent_once", expectedPolicyRevision = policyRevision,
                    }));
                    var consentAck = Ack(fixture.ExchangeTrusted(response, sessionId, runId));
                    Assert.Equal(RuntimeCommandOutcomeKind.Accepted, consentAck.Outcome?.Kind);
                    fixture.RecordProtocolAudit("interaction.respond weakSandboxConsent", sessionId, runId,
                        "human-signed consent_once for the exact verifier ToolCall; HMAC redacted");
                    weakSandboxConsentResponded = true;
                }
            }
            if (events.Any(item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId))
            {
                var finish = events.Last(item => item.Payload is NativeCommandInvocationFinished value
                    && value.InvocationId.ToString() == invocationId).Payload as NativeCommandInvocationFinished;
                Assert.Equal("completed", finish?.OutcomeCode);
                return;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The stdio Orchestrate workflow did not reach a durable terminal invocation event. "
            + $"providerRequests={fixture.Provider.RequestCount}; diagnostics={string.Join(",", fixture.Diagnostics)}; "
            + $"events={string.Join(",", fixture.ReadEvents(sessionId).TakeLast(20).Select(item => item.Payload.GetType().Name))}.");
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
        private string? _nonce;
        private bool _disposed;
        private bool _includeNativeChoiceCapability = true;
        private bool _includeNativeWeakSandboxCapability;
        private bool _includeRetentionQueryCapability;
        private SandboxStrength _processSandboxStrength = SandboxStrength.Strong;

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
        public AckDeliveryCutStream CutStream { get; private set; }
        public string RootDirectory => _root;
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
            bool includeNativeWeakSandboxCapability = false)
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

        private void ClearOwnedSqlitePools()
        {
            ClearPool(Path.Combine(_root, "data", "user.db"));
            ClearPool(JournalPath);

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
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "OmniCore.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new DirectoryNotFoundException("OmniCore.slnx was not found above the test output directory.");
            var safeName = string.Concat(name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
            var destination = Path.Combine(repository.FullName, "artifacts", "m9-worktree-workflow-provision",
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N"), "fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFixtureDirectory(_root, destination);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "stdio-actions-redacted.jsonl"),
                ProtocolAudit, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "provider-requests-fixture.jsonl"),
                ProviderBodies.Select(body => JsonSerializer.Serialize(body)), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "provider-responses-fixture.jsonl"),
                ProviderResponses, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "diagnostics-safe.txt"),
                Diagnostics, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(destination)!, "capture.txt"),
                "Owned fixture copy after stdio Host shutdown. Config uses auth:none and local scripted HTTP provider. "
                + "All agent HOME roots are temporary fixture paths. Trusted action proof values and API credentials are not captured.\n",
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

        private static void CopyFixtureDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyFixtureDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
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

        private async Task WaitForTurnCompletedAsync(string sessionId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadEvents(sessionId).Any(item => item.Payload is TurnCompleted)) return;
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
                nativeProcessControlStageForTests: NativeProcessControlStageForTests));
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
            Exception? cleanupFailure = null;
            var hostStopped = _hostTask is null;
            void Capture(Action action)
            {
                try { action(); }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            try
            {
                Capture(() => _duplex?.CompleteClientInput());
                if (_hostTask is not null)
                {
                    try
                    {
                        var exitCode = await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
                        hostStopped = true;
                        _hostTask = null;
                        if (exitCode != 0) cleanupFailure ??= new InvalidOperationException(
                            "Owned stdio Host exited with code " + exitCode + ".");
                    }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }
                Capture(() => CutStream?.Dispose());
                Capture(() => _duplex?.Dispose());
                var runtime = _runtime;
                _runtime = null;
                Capture(() => runtime?.Dispose());
                Capture(ClearOwnedSqlitePools);
                if (PreserveEvidenceOnDispose && PreservedEvidenceDirectory is null && hostStopped)
                {
                    try
                    {
                        PreserveEvidence("workflow-provision-cleanup");
                        PreserveRootOnDispose = false;
                    }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }
                try { await Provider.DisposeAsync(); }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            finally
            {
                Capture(_providerBodyArrived.Dispose);
                CryptographicOperations.ZeroMemory(_key);
                Capture(() => Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData));
                Capture(() => Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig));
                foreach (var value in _oldExternalRoots)
                    Capture(() => Environment.SetEnvironmentVariable(value.Key, value.Value));
                Capture(() => Environment.CurrentDirectory = _oldDirectory);
                if (!PreserveRootOnDispose)
                    Capture(DeleteOwnedRoot);
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Native command fixture cleanup failed after attempting all owned-resource restorations.", cleanupFailure);

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
