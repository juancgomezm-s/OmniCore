using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Accepted_generic_process_profile_child_is_controlled_inside_its_open_turn_without_an_extra_provider_request()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Strong child process fixture requires Windows AppContainer.");
            return;
        }

        const string objective = "Run the M9 generic child process control fixture.";
        var afterFirstProcess = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterSecondProcess = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var providerWaitCancellation = new CancellationTokenSource();
        var childControlReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childStopReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeControlDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNativeControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childDiagnostics = new ConcurrentQueue<string>();
        var childRequestCount = 0;
        var childTurnResults = new ConcurrentQueue<string>();
        var choiceAcknowledgments = new ConcurrentQueue<string>();
        var processArguments = JsonSerializer.Serialize(new
        {
            executable = "./OmniCore.SandboxTestProcess.exe",
            argv = new[] { "stdin-state" },
            cwd = ".",
            timeoutSeconds = 90,
            terminal = false,
            readPaths = new[]
            {
                "OmniCore.SandboxTestProcess.exe", "OmniCore.SandboxTestProcess.dll",
                "OmniCore.SandboxTestProcess.deps.json", "OmniCore.SandboxTestProcess.runtimeconfig.json",
                "OmniCore.Infrastructure.dll", "OmniCore.Abstractions.dll", "OmniCore.Domain.dll",
            },
            writePaths = Array.Empty<string>(),
        });
        var profileDeniedArguments = JsonSerializer.Serialize(new
        {
            executable = "./OmniCore.SandboxTestProcess.exe",
            argv = new[] { "not-allowed-by-the-child-profile" },
            cwd = ".",
            timeoutSeconds = 90,
            terminal = false,
            readPaths = new[] { "OmniCore.SandboxTestProcess.exe" },
            writePaths = Array.Empty<string>(),
        });
        async Task<string> Respond(int index, string body)
        {
            if (body.Contains(objective, StringComparison.Ordinal))
            {
                switch (Interlocked.Increment(ref childRequestCount))
                {
                    case 1:
                        return CliEndToEndTests.ResponsesToolCallResponse("child-profile-denied-process", "process.start", profileDeniedArguments);
                    case 2:
                        return CliEndToEndTests.ResponsesToolCallResponse("child-persistent-process-a", "process.start", processArguments);
                    case 3:
                        childControlReady.TrySetResult();
                        return await afterFirstProcess.Task.WaitAsync(providerWaitCancellation.Token);
                    case 4:
                        childStopReady.TrySetResult();
                        return await afterSecondProcess.Task.WaitAsync(providerWaitCancellation.Token);
                }
            }
            return CliEndToEndTests.ResponsesTextResponse("The generic delegated child fixture completed.");
        }

        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            includeWorkflowAgentProfile: true, providerResponseAsync: Respond,
            permissionsYaml: "profile: autonomous\nrules:\n  tool.search: allow\n  process.start: allow\n  process.read: allow\n  process.send_input: ask\n  process.stop: allow\n",
            allowWorkflowPersistentProcess: true, includeZeroModelPricing: true,
            nativeProcessControlDeferredForTests: async _ =>
            {
                nativeControlDeferred.TrySetResult();
                await releaseNativeControl.Task.ConfigureAwait(false);
            }, nativeProcessControlStageForTests: stage => childDiagnostics.Enqueue("stdio:" + stage));
        var sessionId = "unassigned";
        var runId = "unassigned";
        var delegationId = "unassigned";
        var controlIdForDiagnostics = "unassigned";
        var latestChoiceIdForDiagnostics = "unassigned";
        Task<int>? childExecution = null;
        string SaveFailureEvidence(string reason)
        {
            var evidence = new StringBuilder().AppendLine("reason=" + reason)
                .AppendLine("session=" + sessionId).AppendLine("run=" + runId)
                .AppendLine("delegation=" + delegationId).AppendLine("providerRequests=" + fixture.Provider.RequestCount)
                .AppendLine("childRequestCount=" + Volatile.Read(ref childRequestCount))
                .AppendLine("childControlReady=" + childControlReady.Task.Status)
                .AppendLine("childStopReady=" + childStopReady.Task.Status)
                .AppendLine("nativeControlDeferred=" + nativeControlDeferred.Task.Status)
                .AppendLine("releaseNativeControl=" + releaseNativeControl.Task.Status)
                .AppendLine("childDiagnostics=" + string.Join(" | ", childDiagnostics))
                .AppendLine("childTurnResults=" + string.Join(" | ", childTurnResults))
                .AppendLine("childExecutionStatus=" + (childExecution?.Status.ToString() ?? "unassigned"))
                .AppendLine("childExecutionFaulted=" + (childExecution?.IsFaulted.ToString() ?? "unassigned"))
                .AppendLine("childExecutionException=" + (childExecution?.Exception?.GetBaseException().ToString() ?? "none"))
                .AppendLine("childExecutionResult=" + (childExecution?.Status == TaskStatus.RanToCompletion
                    ? childExecution.Result.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable"))
                .AppendLine("choiceAcknowledgments=" + string.Join(" | ", choiceAcknowledgments))
                .AppendLine("controlId=" + controlIdForDiagnostics)
                .AppendLine("controlReceipt=" + (fixture.ReadReceipt(controlIdForDiagnostics) is { } controlReceipt
                    ? $"{controlReceipt.State}/{controlReceipt.OutcomeKind}/{controlReceipt.ExecutionState}/{controlReceipt.ExecutionCode}/seq={controlReceipt.FirstSequence}-{controlReceipt.LastSequence}"
                    : "unavailable"))
                .AppendLine("latestChoiceId=" + latestChoiceIdForDiagnostics)
                .AppendLine("latestChoiceReceipt=" + (fixture.ReadReceipt(latestChoiceIdForDiagnostics) is { } choiceReceipt
                    ? $"{choiceReceipt.State}/{choiceReceipt.OutcomeKind}/{choiceReceipt.ExecutionState}/{choiceReceipt.ExecutionCode}/seq={choiceReceipt.FirstSequence}-{choiceReceipt.LastSequence}"
                    : "unavailable"))
                .AppendLine("root=" + fixture.RootDirectory).AppendLine("journal=" + fixture.JournalPath);
            try { evidence.AppendLine("profile=" + File.ReadAllText(Path.Combine(fixture.RootDirectory, "config", "agent-profiles.yaml"))); }
            catch (Exception profileError) { evidence.AppendLine("profileReadError=" + profileError.Message); }
            try { evidence.AppendLine("sessionSecurity=" + fixture.QueryJson("sessionSecurity").GetRawText()); }
            catch (Exception securityError) { evidence.AppendLine("securityReadError=" + securityError.Message); }
            foreach (var diagnostic in fixture.DetailedDiagnostics) evidence.AppendLine("diagnostic=" + diagnostic);
            var bodyIndex = 0;
            foreach (var body in fixture.ProviderBodies)
                evidence.AppendLine($"--- provider {++bodyIndex} ({body.Length} chars) ---\n{body[..Math.Min(body.Length, 5000)]}");
            if (Guid.TryParse(sessionId, out _))
                foreach (var item in fixture.ReadEvents(sessionId).TakeLast(200))
                    evidence.Append(item.Envelope.Sequence).Append('|').Append(item.Envelope.EventId).Append('|')
                        .Append(item.Payload.GetType().Name).Append('|').Append(item.Envelope.RunId).Append('|')
                        .Append(item.Envelope.TaskId).Append('|').Append(item.Envelope.LaneId).Append('|')
                        .Append(item.Envelope.ExecutionId).Append('|').Append(item.Envelope.TurnId).Append('|')
                        .Append(item.Envelope.ToolCallId).Append('|').AppendLine(item.Payload.ToString());
            return fixture.SaveFailureDiagnostic("generic-child", evidence.ToString());
        }
        try
        {
        fixture.ObserveChildTurnResultsForTests(result =>
        {
            var finalText = result.FinalText ?? "";
            var guard = finalText switch
            {
                "Selected reasoning is incompatible with declared route capability." => "SelectedReasoningRouteCapability",
                "Cannot execute Turn: applied AgentProfile ceiling differs from the Lane configuration." => "AppliedAgentProfileLaneMismatch",
                "Cannot execute Turn: durable AgentProfile revision or content differs from its applied configuration." => "DurableAgentProfileRevisionOrContentMismatch",
                "Cannot resume Turn: reasoning resolution differs from its original configuration." => "ResumeReasoningResolutionMismatch",
                _ => "OtherFinalTextSha256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finalText))),
            };
            childTurnResults.Enqueue("stop=" + result.StopReason + "; turn=" + result.TurnId
                + "; steps=" + result.Steps + "; failure=" + (result.FailureDiagnostic ?? "none")
                + "; finalGuard=" + guard + "; toolCalls=" + result.ToolCalls.Count);
        });
        CopyProcessHelperInto(fixture.Workspace);
        InitializeProcessWorkspace(fixture.Workspace);
        sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        runId = fixture.QueryJson("humanActionScope").GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup turn did not create a Run.");
        var scope = fixture.QueryJson("humanActionScope");
        var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select", runId, mode = "orq",
            expectedRevision = scope.GetProperty("revision").GetInt64(), effort = "ultracode",
            adaptive = true, allowedModes = "plan,act,orq", maxAgents = 2, maxDepth = 1,
            maxTurns = 8, maxToolCalls = 12, maxElapsedSeconds = 900, maxSpendUsd = 1m,
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
            Ack(fixture.ExchangeTrusted(mode, sessionId, runId)).Outcome?.Kind);
        var agentFile = File.ReadAllLines(Path.Combine(fixture.RootDirectory, "config", "agent-profiles.yaml"));
        var profileId = agentFile.Select(line => line.Trim()).First(line => line.StartsWith("defaultProfile:", StringComparison.Ordinal))
            .Split(':', 2)[1].Trim();
        var rootLane = Assert.Single(AgentsJson.Decode(fixture.Server.Query("agents",
            TestContext.Current.CancellationToken)!.Json)!.Lanes);
        Assert.NotNull(rootLane.LastContextEventId);
        Assert.NotNull(rootLane.SelectableContextItemIds);
        var request = new DelegationCreateRequest(profileId, rootLane.LastContextEventId!, objective,
            rootLane.SelectableContextItemIds!, 8192, 2, 8, 120_000, 1m);
        var delegationIdCommand = DelegationCommands.Create(request, Ids.NewV7());
        var admitted = fixture.Server.SendUserAction(delegationIdCommand,
            TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, admitted.Outcome?.Kind);
        var childLane = Assert.Single(AgentsJson.Decode(fixture.Server.Query("agents",
            TestContext.Current.CancellationToken)!.Json)!.Lanes, lane => lane.DelegationId is not null);
        delegationId = childLane.DelegationId!;
        Assert.NotNull(childLane.DelegationState);
        childExecution = Task.Run(() => fixture.ExecuteDelegationAsync(delegationId, childDiagnostics.Enqueue,
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        async Task<(PermissionRequested Permission, InteractionRequested Interaction)> WaitForPermissionAsync(
            string toolName, int occurrence)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = fixture.ReadEvents(sessionId);
                var calls = snapshot.Where(item => item.Payload is ToolCallRequested call
                    && call.ToolName == toolName).ToArray();
                if (calls.Length >= occurrence)
                {
                    var callId = calls[occurrence - 1].Envelope.ToolCallId;
                    var permission = snapshot.Select(item => item.Payload).OfType<PermissionRequested>()
                        .SingleOrDefault(item => item.ToolCallId == callId);
                    if (permission is not null && snapshot.Select(item => item.Payload).OfType<InteractionRequested>()
                        .SingleOrDefault(item => item.InteractionId == permission.InteractionId) is { } interaction)
                        return (permission, interaction);
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var path = SaveFailureEvidence("Permission interaction missing for " + toolName + " occurrence " + occurrence);
            throw new TimeoutException("Expected Permission/Interaction not observed: tool=" + toolName
                + "; occurrence=" + occurrence + "; evidence=" + path);
        }

        async Task RespondAllowOnceAsync(InteractionRequested interaction)
        {
            var securitySnapshot = fixture.QueryJson("sessionSecurity");
            var commandId = Ids.NewV7();
            latestChoiceIdForDiagnostics = commandId;
            var allow = WireEnvelope.Command(commandId, JsonSerializer.Serialize(new
            {
                cmd = "interaction.respond", responseType = "choice",
                interactionId = interaction.InteractionId.ToString(), optionId = "allow_once",
                expectedRunId = runId, expectedPolicyRevision = securitySnapshot.GetProperty("revision").GetInt64(),
            }));
            var ack = Ack(fixture.ExchangeTrusted(allow, sessionId, runId));
            choiceAcknowledgments.Enqueue("command=" + commandId + "; interaction="
                + interaction.InteractionId + "; status=" + ack.Status + "; error=" + ack.Error
                + "; outcome=" + ack.Outcome?.Kind + "; reason=" + ack.Outcome?.Reason);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        }

        try
        {
            await childControlReady.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        catch (Exception waitFailure)
        {
            var path = SaveFailureEvidence("child did not reach its next provider boundary after process.start: " + waitFailure);
            throw new InvalidOperationException("Child process start did not complete; evidence=" + path, waitFailure);
        }
        var pendingEvents = fixture.ReadEvents(sessionId);
        var processStarts = pendingEvents.Where(item => item.Payload is ToolCallRequested call
            && call.ToolName == "process.start").ToArray();
        var profileDeniedCall = processStarts[0];
        Assert.Equal(2, processStarts.Length);
        Assert.Contains(pendingEvents, item => item.Envelope.ToolCallId == profileDeniedCall.Envelope.ToolCallId
            && item.Payload is PermissionDenied);
        Assert.DoesNotContain(pendingEvents, item => item.Envelope.ToolCallId == profileDeniedCall.Envelope.ToolCallId
            && item.Payload is PermissionRequested or ToolCallStarted or ProcessSessionStateRecorded);
        Assert.DoesNotContain(pendingEvents, item => item.Payload is PermissionRequested);
        Assert.Contains(pendingEvents, item => item.Envelope.ToolCallId == processStarts[1].Envelope.ToolCallId
            && item.Payload is ToolCallStarted);
        Assert.False(childExecution.IsCompleted, "The child must still own its open Turn while the provider is at the next boundary.");
        var providerRequestsBeforeControl = fixture.Provider.RequestCount;

        var events = fixture.ReadEvents(sessionId);
        var startRequest = events.Last(item => item.Payload is ToolCallRequested call && call.ToolName == "process.start");
        var startEnvelope = startRequest.Envelope;
        Assert.Equal(runId, startEnvelope.RunId?.ToString());
        Assert.NotNull(startEnvelope.TaskId);
        Assert.NotNull(startEnvelope.LaneId);
        Assert.NotNull(startEnvelope.ExecutionId);
        var task = new TaskId(startEnvelope.TaskId!.Value);
        var lane = new LaneId(startEnvelope.LaneId!.Value);
        var execution = new ExecutionId(startEnvelope.ExecutionId!.Value);
        Assert.Contains(events, item => item.Payload is AgentExecutionStarted started
            && started.ExecutionId == execution && started.LaneId == lane && started.ParentExecutionId is not null);
        Assert.Contains(events, item => item.Payload is DelegationAccepted accepted
            && accepted.ChildExecutionId == execution);
        var handle = FindLatestProcessHandle(fixture, sessionId);
        var running = fixture.QueryJson("process.sessions:" + runId);
        var childSummary = Assert.Single(running.GetProperty("processes").EnumerateArray(), item =>
            item.GetProperty("handle").GetString() == handle);
        Assert.Equal(lane.ToString(), childSummary.GetProperty("laneId").GetString());
        Assert.True(childSummary.GetProperty("canInput").GetBoolean());

        var currentCatalog = fixture.QueryJson("command.catalog");
        var wrongScopeId = Guid.NewGuid().ToString("D");
        var wrongScope = WireEnvelope.Command(wrongScopeId, JsonSerializer.Serialize(new
        {
            cmd = "process.control", canonicalCommandId = "core:process.control",
            arguments = new[] { "process.send_input", JsonSerializer.Serialize(new
                { handle, input = "wrong-run-must-not-run", eof = true }) },
            expectedSessionId = sessionId, expectedRunId = Guid.NewGuid().ToString("D"),
            expectedModeRevision = NullableJsonInt64(currentCatalog.GetProperty("modeRevision")),
            expectedPolicyRevision = currentCatalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = currentCatalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(currentCatalog.GetProperty("routingPolicyRevision")),
            catalogRevision = currentCatalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
        }));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted,
            Ack(fixture.ExchangeTrusted(wrongScope, sessionId, runId)).Outcome?.Kind);
        Assert.Equal(providerRequestsBeforeControl, fixture.Provider.RequestCount);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + wrongScopeId);

        var wrongHandleId = Guid.NewGuid().ToString("D");
        var wrongHandle = WireEnvelope.Command(wrongHandleId, JsonSerializer.Serialize(new
        {
            cmd = "process.control", canonicalCommandId = "core:process.control",
            arguments = new[] { "process.send_input", JsonSerializer.Serialize(new
                { handle = Guid.NewGuid().ToString("N"), input = "must-not-run", eof = true }) },
            expectedSessionId = sessionId, expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(currentCatalog.GetProperty("modeRevision")),
            expectedPolicyRevision = currentCatalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = currentCatalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(currentCatalog.GetProperty("routingPolicyRevision")),
            catalogRevision = currentCatalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
        }));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted,
            Ack(fixture.ExchangeTrusted(wrongHandle, sessionId, runId)).Outcome?.Kind);
        Assert.Equal(providerRequestsBeforeControl, fixture.Provider.RequestCount);
        Assert.Equal(2, fixture.ReadEvents(sessionId).Count(item => item.Payload is ToolCallRequested call
            && call.ToolName == "process.start"));

        var controlId = Guid.NewGuid().ToString("D");
        controlIdForDiagnostics = controlId;
        var control = WireEnvelope.Command(controlId, JsonSerializer.Serialize(new
        {
            cmd = "process.control", canonicalCommandId = "core:process.control",
            arguments = new[] { "process.send_input", JsonSerializer.Serialize(new { handle, input = "ñ\n", eof = true }) },
            expectedSessionId = sessionId, expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(currentCatalog.GetProperty("modeRevision")),
            expectedPolicyRevision = currentCatalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = currentCatalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(currentCatalog.GetProperty("routingPolicyRevision")),
            catalogRevision = currentCatalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
        }));
        var controlAck = Ack(fixture.ExchangeTrusted(control, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, controlAck.Outcome?.Kind);
        await fixture.WaitForReceiptExecutionAsync(controlId, "deferred");
        try { await nativeControlDeferred.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        catch (Exception waitFailure)
        {
            var path = SaveFailureEvidence("native control did not pause after its deferred receipt: " + waitFailure);
            throw new InvalidOperationException("Expected source control continuation pause; evidence=" + path, waitFailure);
        }
        var controlPermission = await WaitForPermissionAsync("process.send_input", 1);
        Assert.Equal(controlPermission.Permission.ToolCallId, Assert.Single(fixture.ReadEvents(sessionId),
            item => item.Payload is ToolCallRequested call && call.ProviderCallId == "native-action-" + controlId).Envelope.ToolCallId);
        await RespondAllowOnceAsync(controlPermission.Interaction);
        Assert.Equal("deferred", fixture.ReadReceipt(controlId)?.ExecutionState);
        var pausedControlCall = fixture.ReadEvents(sessionId).Single(item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + controlId).Envelope.ToolCallId;
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Envelope.ToolCallId == pausedControlCall
            && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown);
        releaseNativeControl.TrySetResult();
        await fixture.WaitForReceiptExecutionAsync(controlId, "completed");
        await WaitForNativeToolSucceededAsync(fixture, sessionId, controlId, "process.send_input");
        Assert.Equal(providerRequestsBeforeControl, fixture.Provider.RequestCount);
        var controlledEvents = fixture.ReadEvents(sessionId);
        var nativeControlEvent = controlledEvents.Last(item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + controlId);
        Assert.Equal(RunState.Running, RunProjection.Replay(SessionId.Parse(sessionId), RunId.Parse(runId),
            fixture.Server.AcquireCodecs(), controlledEvents.Select(item => item.Envelope).ToArray()).State);
        _ = CanonicalStateTracker.Replay(fixture.Server.AcquireCodecs(),
            controlledEvents.Select(item => item.Envelope).ToArray());
        var resumedControl = Assert.Single(controlledEvents, item => item.Payload is RunInteractionResumed resumed
            && resumed.InteractionId == controlPermission.Interaction.InteractionId).Envelope;
        var withoutAdmission = controlledEvents.Where(item => item.Payload is not NativeCommandInvocationAdmitted admitted
            || admitted.InvocationId.ToString() != controlId).Select(item => item.Envelope).ToArray();
        var wrongExecution = controlledEvents.Select(item => item.Envelope.EventId == resumedControl.EventId
            ? DomainEvent.Stored(item.Envelope.EventId, item.Envelope.SessionId, item.Envelope.Sequence,
                item.Envelope.Type, item.Envelope.SchemaVersion, item.Envelope.Timestamp, item.Envelope.Causation,
                item.Envelope.CorrelationId, item.Envelope.RunId, item.Envelope.TaskId, item.Envelope.LaneId,
                item.Envelope.TurnId, item.Envelope.PlanItemId, item.Envelope.ToolCallId, item.Envelope.ArtifactRefs,
                item.Envelope.PayloadJson, new ExecutionId(Guid.NewGuid()), item.Envelope.Source)
            : item.Envelope).ToArray();
        var wrongToolCall = controlledEvents.Select(item => item.Envelope.EventId == resumedControl.EventId
            ? DomainEvent.Stored(item.Envelope.EventId, item.Envelope.SessionId, item.Envelope.Sequence,
                item.Envelope.Type, item.Envelope.SchemaVersion, item.Envelope.Timestamp, item.Envelope.Causation,
                item.Envelope.CorrelationId, item.Envelope.RunId, item.Envelope.TaskId, item.Envelope.LaneId,
                item.Envelope.TurnId, item.Envelope.PlanItemId, ToolCallId.New(), item.Envelope.ArtifactRefs,
                item.Envelope.PayloadJson, item.Envelope.ExecutionId, item.Envelope.Source)
            : item.Envelope).ToArray();
        var codecs = fixture.Server.AcquireCodecs();
        var targetInteractionId = controlPermission.Interaction.InteractionId;
        var targetRequestEvent = Assert.Single(controlledEvents, item => item.Payload is InteractionRequested requested
            && requested.InteractionId == targetInteractionId).Envelope;
        var targetRequest = Assert.IsType<InteractionRequested>(codecs.Decode(targetRequestEvent));
        var targetResolutionEvent = Assert.Single(controlledEvents, item => item.Payload is InteractionResolved resolved
            && resolved.InteractionId == targetInteractionId).Envelope;
        var targetResolution = Assert.IsType<InteractionResolved>(codecs.Decode(targetResolutionEvent));
        DomainEvent Rebuild(DomainEvent original, long sequence, DomainEventPayload? replacement = null,
            bool newEvent = false)
        {
            var payload = replacement ?? codecs.Decode(original);
            return DomainEvent.Stored(newEvent ? EventId.New() : original.EventId, original.SessionId, sequence, payload.Type(),
                payload.SchemaVersion(), original.Timestamp, original.Causation, original.CorrelationId,
                original.RunId, original.TaskId, original.LaneId, original.TurnId, original.PlanItemId,
                original.ToolCallId, original.ArtifactRefs, codecs.CodecFor(payload.Type()).Encode(payload),
                original.ExecutionId, original.Source);
        }

        DomainEvent[] CrossInteractionResolution(bool expireTarget)
        {
            var otherInteractionId = InteractionId.New();
            var insertedEvents = expireTarget ? 2 : 1;
            var malformed = new List<DomainEvent>(controlledEvents.Count + insertedEvents);
            foreach (var item in controlledEvents)
            {
                var original = item.Envelope;
                var sequence = original.Sequence > targetRequestEvent.Sequence
                    ? original.Sequence + insertedEvents : original.Sequence;
                var replacement = original.EventId == targetResolutionEvent.EventId
                    ? targetResolution with { InteractionId = otherInteractionId }
                    : null;
                malformed.Add(Rebuild(original, sequence, replacement));
                if (original.EventId != targetRequestEvent.EventId) continue;

                var insertedSequence = original.Sequence + 1;
                if (expireTarget)
                    malformed.Add(Rebuild(original, insertedSequence++, new InteractionExpired(targetInteractionId),
                        newEvent: true));
                malformed.Add(Rebuild(original, insertedSequence,
                    targetRequest with { InteractionId = otherInteractionId }, newEvent: true));
            }
            return malformed.ToArray();
        }

        var crossInteractionResolution = CrossInteractionResolution(expireTarget: false);
        var expiredTargetOtherResolution = CrossInteractionResolution(expireTarget: true);
        Assert.Contains(expiredTargetOtherResolution, item => codecs.Decode(item) is InteractionExpired expired
            && expired.InteractionId == targetInteractionId);
        Assert.Contains(expiredTargetOtherResolution, item => codecs.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId != targetInteractionId && item.TaskId == targetRequestEvent.TaskId
            && item.LaneId == targetRequestEvent.LaneId && item.ExecutionId == targetRequestEvent.ExecutionId);
        foreach (var invalidResume in new[] { withoutAdmission, wrongExecution, wrongToolCall,
                     crossInteractionResolution, expiredTargetOtherResolution })
        {
            Assert.Throws<InvalidStateTransitionException>(() => RunProjection.Replay(SessionId.Parse(sessionId),
                RunId.Parse(runId), fixture.Server.AcquireCodecs(), invalidResume));
            Assert.Throws<InvalidStateTransitionException>(() => CanonicalStateTracker.Replay(
                fixture.Server.AcquireCodecs(), invalidResume));
        }
        Assert.Equal(task.ToString(), nativeControlEvent.Envelope.TaskId?.ToString());
        Assert.Equal(lane.ToString(), nativeControlEvent.Envelope.LaneId?.ToString());
        Assert.Equal(execution.ToString(), nativeControlEvent.Envelope.ExecutionId?.ToString());
        Assert.Equal(3, fixture.ReadEvents(sessionId).Count(item => item.Envelope.TaskId == task
            && item.Envelope.LaneId == lane && item.Envelope.TurnId == startEnvelope.TurnId
            && item.Payload is ToolCallRequested));
        Assert.Equal(startEnvelope.TurnId, nativeControlEvent.Envelope.TurnId);
        var successCount = controlledEvents.Count(item => item.Envelope.ToolCallId == nativeControlEvent.Envelope.ToolCallId
            && item.Payload is ToolCallSucceeded);
        var duplicateAck = Ack(fixture.ExchangeTrusted(control, sessionId, runId));
        Assert.Equal(controlAck.FirstSeq, duplicateAck.FirstSeq);
        Assert.Equal(controlAck.LastSeq, duplicateAck.LastSeq);
        Assert.Equal(successCount, fixture.ReadEvents(sessionId).Count(item => item.Envelope.ToolCallId == nativeControlEvent.Envelope.ToolCallId
            && item.Payload is ToolCallSucceeded));
        Assert.Equal(providerRequestsBeforeControl, fixture.Provider.RequestCount);

        var terminalDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < terminalDeadline
            && !fixture.ReadEvents(sessionId).Any(item => item.Payload is ProcessSessionStateRecorded process
                && process.HandleId == handle && process.State == "exited"))
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ReadEvents(sessionId), item => item.Payload is ProcessSessionStateRecorded process
            && process.HandleId == handle && process.State == "exited");
        var eventsBeforeQueries = fixture.ReadEvents(sessionId);
        var passive = fixture.QueryJson($"process.output:{runId}:{handle}:0");
        Assert.Equal("exited", passive.GetProperty("state").GetString());
        var output = string.Join("", passive.GetProperty("chunks").EnumerateArray()
            .Select(item => item.GetProperty("text").GetString()));
        Assert.Contains("PIPE:ñ", output, StringComparison.Ordinal);
        Assert.Equal(eventsBeforeQueries.Count, fixture.ReadEvents(sessionId).Count);
        afterFirstProcess.TrySetResult(CliEndToEndTests.ResponsesToolCallResponse("child-persistent-process-b",
            "process.start", processArguments));
        await childStopReady.Task.WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);
        var secondHandle = Assert.Single(fixture.QueryJson("process.sessions:" + runId).GetProperty("processes")
            .EnumerateArray(), item => item.GetProperty("laneId").GetString() == lane.ToString()
                && item.GetProperty("state").GetString() == "running").GetProperty("handle").GetString()!;
        Assert.NotEqual(handle, secondHandle);
        var requestsBeforeStop = fixture.Provider.RequestCount;
        var stopCatalog = fixture.QueryJson("command.catalog");
        var stopId = Guid.NewGuid().ToString("D");
        var stop = WireEnvelope.Command(stopId, JsonSerializer.Serialize(new
        {
            cmd = "process.control", canonicalCommandId = "core:process.control",
            arguments = new[] { "process.stop", JsonSerializer.Serialize(new { handle = secondHandle }) },
            expectedSessionId = sessionId, expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(stopCatalog.GetProperty("modeRevision")),
            expectedPolicyRevision = stopCatalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = stopCatalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(stopCatalog.GetProperty("routingPolicyRevision")),
            catalogRevision = stopCatalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
        }));
        var originalStopAck = Ack(fixture.ExchangeTrusted(stop, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, originalStopAck.Outcome?.Kind);
        await fixture.WaitForReceiptExecutionAsync(stopId, "completed");
        await WaitForNativeToolSucceededAsync(fixture, sessionId, stopId, "process.stop");
        Assert.Equal(requestsBeforeStop, fixture.Provider.RequestCount);
        var duplicateStopAck = Ack(fixture.ExchangeTrusted(stop, sessionId, runId));
        Assert.Equal(originalStopAck.FirstSeq, duplicateStopAck.FirstSeq);
        Assert.Equal(originalStopAck.LastSeq, duplicateStopAck.LastSeq);
        var stopEventSnapshot = fixture.ReadEvents(sessionId);
        var nativeStopCall = Assert.Single(stopEventSnapshot, evt => evt.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + stopId).Envelope.ToolCallId;
        Assert.Single(stopEventSnapshot, evt => evt.Payload is ToolCallSucceeded
            && evt.Envelope.ToolCallId == nativeStopCall);
        var stopEvents = fixture.ReadEvents(sessionId);
        Assert.Contains(stopEvents, item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + stopId
            && item.Envelope.TaskId?.ToString() == task.ToString()
            && item.Envelope.LaneId?.ToString() == lane.ToString()
            && item.Envelope.ExecutionId?.ToString() == execution.ToString()
            && item.Envelope.TurnId == startEnvelope.TurnId);
        Assert.Contains(fixture.QueryJson("process.sessions:" + runId).GetProperty("processes").EnumerateArray(),
            item => item.GetProperty("handle").GetString() == secondHandle
                && item.GetProperty("state").GetString() == "stopped");

        afterSecondProcess.TrySetResult(CliEndToEndTests.ResponsesTextResponse("The child process was controlled and verified."));
        var executionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < executionDeadline
            && !fixture.ReadEvents(sessionId).Any(item => item.Payload is AgentExecutionCompleted completed
                && completed.ExecutionId == execution)
            && !fixture.ReadEvents(sessionId).Any(item => item.Payload is AgentExecutionFailed failed
                && failed.ExecutionId == execution))
            await Task.Delay(25, TestContext.Current.CancellationToken);
        var terminalEvents = fixture.ReadEvents(sessionId);
        Assert.Contains(terminalEvents, item => item.Payload is AgentExecutionCompleted completed
            && completed.ExecutionId == execution);
        Assert.DoesNotContain(terminalEvents, item => item.Payload is AgentExecutionFailed failed
            && failed.ExecutionId == execution);
        Assert.Contains(childTurnResults, observation => observation.StartsWith("stop=EndTurn; turn="
            + startEnvelope.TurnId, StringComparison.Ordinal));
        var terminalCatalog = fixture.QueryJson("command.catalog");
        var terminalControlId = Guid.NewGuid().ToString("D");
        var terminalControl = WireEnvelope.Command(terminalControlId, JsonSerializer.Serialize(new
        {
            cmd = "process.control", canonicalCommandId = "core:process.control",
            arguments = new[] { "process.send_input", JsonSerializer.Serialize(new
                { handle = secondHandle, input = "after-child-terminal", eof = true }) },
            expectedSessionId = sessionId, expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(terminalCatalog.GetProperty("modeRevision")),
            expectedPolicyRevision = terminalCatalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = terminalCatalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(terminalCatalog.GetProperty("routingPolicyRevision")),
            catalogRevision = terminalCatalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1"))),
        }));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted,
            Ack(fixture.ExchangeTrusted(terminalControl, sessionId, runId)).Outcome?.Kind);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + terminalControlId);
        }
        catch (Exception exception)
        {
            var path = SaveFailureEvidence(exception.ToString());
            throw new InvalidOperationException("M9 child control diagnostic: " + path, exception);
        }
        finally
        {
            releaseNativeControl.TrySetResult();
            var release = CliEndToEndTests.ResponsesTextResponse("Child control fixture cleanup.");
            afterFirstProcess.TrySetResult(release);
            afterSecondProcess.TrySetResult(release);
            providerWaitCancellation.Cancel();
            if (childExecution is { IsCompleted: false })
                await Task.WhenAny(childExecution, Task.Delay(TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
        }
    }
}
