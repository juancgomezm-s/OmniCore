using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task Native_worktree_preview_is_passive_and_apply_uses_ask_receipt_and_deduplicated_tool_pipeline()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            permissionsYaml: "profile: balanced\nrules:\n  tool.search: allow\n  worktree.integrate: ask\n");
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
        var scope = fixture.QueryJson("humanActionScope");
        var runId = scope.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup Run is not active after its Plan turn.");
        var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "act",
            expectedRevision = scope.GetProperty("revision").GetInt64(),
        }));
        var modeAck = Ack(fixture.ExchangeTrusted(mode, sessionId, runId));
        Assert.True(modeAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Act mode selection failed: {modeAck.Error}/{modeAck.Outcome?.Reason}");
        var binding = await CreateNativeWorktreeBindingAsync(fixture, sessionId, runId);
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var changedPath = Path.Combine(binding.Identity.WorktreePath, "new-native-file.txt");
        File.WriteAllText(changedPath, "native worktree marker " + binding.Marker + "\n", new UTF8Encoding(false));
        var proposal = Assert.IsType<WorktreeIntegrationPreview>((await store.PreviewIntegrationAsync(
            new WorktreeIntegrationPreviewRequest(binding.Identity), CancellationToken.None)).Preview);
        var paths = proposal.Changes.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray();
        Assert.True(paths.SequenceEqual(["new-native-file.txt"], StringComparer.Ordinal),
            "Unexpected integration paths: " + string.Join(",", proposal.Changes.Select(item =>
                item.RelativePath + ":" + item.ExpectedPreSha256 + ":" + item.ExpectedPostSha256)));

        var sequenceBeforePreview = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        var statusBefore = Git(binding.Identity.RepositoryRoot, "status", "--porcelain=v1", "--untracked-files=all");
        var worktreesBefore = Git(binding.Identity.RepositoryRoot, "worktree", "list", "--porcelain");
        var publicLane = Assert.Single(fixture.QueryJson("agents").GetProperty("lanes").EnumerateArray(), item =>
            item.GetProperty("objective").GetString() == "Fixture child worktree");
        var laneId = publicLane.GetProperty("laneId").GetString()
            ?? throw new InvalidOperationException("The public agents query omitted its Lane ID.");
        Assert.Equal(binding.Identity.LaneId.ToString(), laneId);
        var previewJson = fixture.QueryJson("worktree.preview:" + laneId);
        var publicOwnershipId = previewJson.GetProperty("ownershipId").GetString()
            ?? throw new InvalidOperationException("The public worktree preview omitted its apply ownership token.");
        Assert.Equal(binding.Identity.OwnershipId, publicOwnershipId);
        Assert.Equal(proposal.ProposalId, previewJson.GetProperty("proposalId").GetString());
        Assert.True(previewJson.GetProperty("applicable").GetBoolean());
        Assert.Equal(paths, previewJson.GetProperty("changes").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()).ToArray());
        Assert.Equal(sequenceBeforePreview, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal(statusBefore, Git(binding.Identity.RepositoryRoot, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(worktreesBefore, Git(binding.Identity.RepositoryRoot, "worktree", "list", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));

        var catalog = fixture.QueryJson("command.catalog");
        var descriptor = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:worktree.integrate");
        Assert.True(descriptor.GetProperty("enabled").GetBoolean());
        Assert.Equal("nativeTool", descriptor.GetProperty("kind").GetString());
        var invocationId = Guid.NewGuid().ToString("D");
        var integrationArguments = JsonSerializer.Serialize(new
        {
            ownershipId = publicOwnershipId,
            proposalId = proposal.ProposalId,
            paths,
        });
        Assert.True(fixture.Server.NativeWorktreeIntegrationStillAllowed(SessionId.Parse(sessionId),
            RunId.Parse(runId), integrationArguments), "The live worktree authorization preflight rejected the exact queried proposal.");
        Assert.False(fixture.Server.NativeWorktreeIntegrationStillAllowed(SessionId.New(), RunId.Parse(runId),
            integrationArguments), "A proposal was executable from another Session.");
        Assert.False(fixture.Server.NativeWorktreeIntegrationStillAllowed(SessionId.Parse(sessionId), RunId.New(),
            integrationArguments), "A proposal was executable from another Run.");
        var foreignOwnershipArguments = JsonSerializer.Serialize(new
        {
            ownershipId = Guid.NewGuid().ToString("N"),
            proposalId = proposal.ProposalId,
            paths,
        });
        Assert.False(fixture.Server.NativeWorktreeIntegrationStillAllowed(SessionId.Parse(sessionId),
            RunId.Parse(runId), foreignOwnershipArguments), "A proposal was executable under foreign ownership.");
        var request = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = "core:worktree.integrate",
            arguments = new[] { "worktree.integrate", integrationArguments },
            expectedSessionId = sessionId,
            expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(catalog.GetProperty("modeRevision")),
            expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
            catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
        }));
        var ack = Ack(fixture.ExchangeTrusted(request, sessionId, runId));
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Native worktree action was not admitted: {ack.Error}/{ack.Outcome?.Reason}");
        await WaitForPermissionRequestAsync(fixture, sessionId, runId);
        await fixture.WaitForReceiptExecutionAsync(invocationId, "deferred");
        var eventsBeforeApproval = fixture.ReadEvents(sessionId);
        Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(SessionId.Parse(sessionId), RunId.Parse(runId),
            fixture.Server.AcquireCodecs(), eventsBeforeApproval.Select(item => item.Envelope).ToArray()).State);
        Assert.Contains(eventsBeforeApproval, item => item.Envelope.RunId?.ToString() == runId
            && item.Envelope.TaskId == eventsBeforeApproval.Single(value => value.Payload is PermissionRequested).Envelope.TaskId
            && item.Envelope.LaneId == eventsBeforeApproval.Single(value => value.Payload is PermissionRequested).Envelope.LaneId
            && item.Payload is RunAwaitingInput transition && transition.RunId == RunId.Parse(runId));
        var permission = Assert.IsType<PermissionRequested>(Assert.Single(eventsBeforeApproval,
            item => item.Payload is PermissionRequested).Payload);
        Assert.DoesNotContain(eventsBeforeApproval, item => item.Envelope.ToolCallId == permission.ToolCallId
            && item.Payload is ToolCallStarted or WorktreeIntegrationStateRecorded);
        Assert.False(File.Exists(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));

        var interaction = Assert.IsType<InteractionRequested>(Assert.Single(eventsBeforeApproval,
            item => item.Payload is InteractionRequested).Payload);
        var interactionEnvelope = Assert.Single(eventsBeforeApproval,
            item => item.Payload is InteractionRequested requested && requested.InteractionId == interaction.InteractionId);
        var deferredReceipt = fixture.ReadReceipt(invocationId);
        var nativeCall = Assert.Single(eventsBeforeApproval, item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + invocationId);
        var nativeCallPayload = Assert.IsType<ToolCallRequested>(nativeCall.Payload);
        Assert.True(fixture.Server.NativeToolInvocationMatches(SessionId.Parse(sessionId), RunId.Parse(runId),
            nativeCallPayload.ProviderCallId, nativeCallPayload.ToolName, nativeCallPayload.ArgumentsJson),
            $"Native invocation matcher failed: receipt={deferredReceipt?.State}/{deferredReceipt?.OutcomeKind}/"
            + $"{deferredReceipt?.ExecutionState}/{deferredReceipt?.ExecutionCode}; tool={nativeCallPayload.ToolName}; "
            + $"call={nativeCall.Envelope.TaskId}/{nativeCall.Envelope.LaneId}/{nativeCall.Envelope.TurnId}; "
            + $"interaction={interactionEnvelope.Envelope.TaskId}/{interactionEnvelope.Envelope.LaneId}/{interactionEnvelope.Envelope.TurnId}; "
            + $"taskStart={fixture.ReadEvents(sessionId).Single(item => item.Payload is TurnStarted started
                && started.TurnId == nativeCall.Envelope.TurnId).Envelope.TaskId}.");
        var security = fixture.QueryJson("sessionSecurity");
        var approval = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "interaction.respond",
            responseType = "choice",
            interactionId = interaction.InteractionId.ToString(),
            optionId = "allow_once",
            expectedRunId = runId,
            expectedPolicyRevision = security.GetProperty("revision").GetInt64(),
        }));
        var approved = Ack(fixture.ExchangeTrusted(approval, sessionId, runId));
        Assert.True(approved.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Permission choice was rejected: {approved.Error}/{approved.Outcome?.Reason}; options={interaction.OptionsJson}; "
            + $"receipt={deferredReceipt?.State}/{deferredReceipt?.OutcomeKind}/{deferredReceipt?.ExecutionState}/"
            + $"{deferredReceipt?.ExecutionCode}; call={nativeCall.Envelope.TaskId}/{nativeCall.Envelope.LaneId}/"
            + $"{nativeCall.Envelope.TurnId}; interaction={interactionEnvelope.Envelope.TaskId}/{interactionEnvelope.Envelope.LaneId}/"
            + $"{interactionEnvelope.Envelope.TurnId}; turnStart={fixture.ReadEvents(sessionId).Single(item => item.Payload is TurnStarted started
                && started.TurnId == nativeCall.Envelope.TurnId).Envelope.TaskId}.");
        var effectDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        (DomainEvent Envelope, DomainEventPayload Payload)[] afterApproval = [];
        while (DateTimeOffset.UtcNow < effectDeadline)
        {
            afterApproval = fixture.ReadEvents(sessionId).ToArray();
            if (afterApproval.Any(item => item.Envelope.ToolCallId == nativeCallPayload.ToolCallId
                    && item.Payload is ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown)) break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.True(afterApproval.Any(item => item.Envelope.TaskId == nativeCall.Envelope.TaskId
            && item.Envelope.LaneId == nativeCall.Envelope.LaneId && item.Envelope.TurnId == nativeCall.Envelope.TurnId
            && item.Payload is RunInteractionResumed resumed && resumed.InteractionId == interaction.InteractionId
            && resumed.CommandId == approval.MessageId),
            "resume correlation mismatch: expected=" + nativeCall.Envelope.TaskId + "/" + nativeCall.Envelope.LaneId
            + "/" + nativeCall.Envelope.TurnId + "/" + interaction.InteractionId + "/" + approval.MessageId
            + "; observed=" + string.Join(" | ", afterApproval.Where(item => item.Payload is RunInteractionResumed)
                .Select(item => item.Envelope.Sequence + ":" + item.Envelope.TaskId + "/" + item.Envelope.LaneId
                    + "/" + item.Envelope.TurnId + "/" + ((RunInteractionResumed)item.Payload).InteractionId
                    + "/" + ((RunInteractionResumed)item.Payload).CommandId)));
        Assert.DoesNotContain(afterApproval, item => item.Envelope.Sequence > interactionEnvelope.Envelope.Sequence
            && item.Envelope.RunId?.ToString() == runId && item.Payload is UserInputReceived);
        Assert.True(afterApproval.Any(item => item.Envelope.ToolCallId == nativeCallPayload.ToolCallId
            && item.Payload is ToolCallSucceeded),
            "native worktree terminal mismatch: providerRequests=" + fixture.Provider.RequestCount + "; receipt="
            + fixture.ReadReceipt(invocationId)?.ExecutionState + "/" + fixture.ReadReceipt(invocationId)?.ExecutionCode
            + "; outcomes=" + string.Join(" | ", afterApproval.Where(item =>
                item.Envelope.ToolCallId == nativeCallPayload.ToolCallId
                && item.Payload is ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown or ToolCallReconciled)
                .Select(item => item.Envelope.Sequence + ":" + item.Payload.Type() + ":" + item.Payload)));
        Assert.DoesNotContain(afterApproval, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId);
        Assert.Equal("native worktree marker " + binding.Marker + "\n",
            File.ReadAllText(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));
        var durableSequence = fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence);
        await fixture.RestartHostAsync();
        var reopenedPreview = fixture.QueryJson("worktree.preview:" + laneId);
        Assert.False(reopenedPreview.GetProperty("applicable").GetBoolean());
        Assert.Empty(reopenedPreview.GetProperty("changes").EnumerateArray());
        Assert.Equal(durableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        var afterRestartRetry = Ack(fixture.ExchangeTrusted(request, sessionId, runId));
        Assert.Equal(ack.FirstSeq, afterRestartRetry.FirstSeq);
        Assert.Equal(ack.LastSeq, afterRestartRetry.LastSeq);
        Assert.Equal(durableSequence, fixture.ReadEvents(sessionId).Max(item => item.Envelope.Sequence));
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.Single(fixture.ReadEvents(sessionId), item => item.Payload is WorktreeIntegrationStateRecorded state
            && state.ProposalId == proposal.ProposalId && state.State == WorktreeIntegrationState.Completed);
        Assert.Equal("native worktree marker " + binding.Marker + "\n", File.ReadAllText(changedPath));
        Assert.Equal("native worktree marker " + binding.Marker + "\n",
            File.ReadAllText(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));
        Assert.Equal(1, fixture.Provider.RequestCount);
        await fixture.WaitForReceiptExecutionAsync(invocationId, "completed");
        var receipt = fixture.ReadReceipt(invocationId);
        Assert.True(receipt is { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
            ExecutionState: "completed", ExecutionCode: "native_tool_completed" },
            $"Worktree receipt was not terminal: {receipt?.State}/{receipt?.OutcomeKind}/{receipt?.ExecutionState}/{receipt?.ExecutionCode}.");
        var terminalReceipt = receipt ?? throw new InvalidOperationException("Native worktree receipt disappeared.");
        fixture.Server.ReceiptStore?.CompleteDeferredCommandExecution(invocationId, terminalReceipt.RequestHash,
            "completed", "native_tool_completed", sessionId, runId);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.ReceiptStore?.CompleteDeferredCommandExecution(
            invocationId, "wrong-request-hash", "completed", "native_tool_completed", sessionId, runId));
        Assert.Throws<InvalidOperationException>(() => fixture.Server.ReceiptStore?.CompleteDeferredCommandExecution(
            invocationId, terminalReceipt.RequestHash, "failed", "native_tool_failed", sessionId, runId));
        Assert.Equal("native worktree marker " + binding.Marker + "\n", File.ReadAllText(changedPath));
        Assert.Equal("native worktree marker " + binding.Marker + "\n",
            File.ReadAllText(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));
        var completedEvents = fixture.ReadEvents(sessionId);
        var resumeEvent = Assert.Single(completedEvents, item => item.Payload is RunInteractionResumed resumed
            && resumed.InteractionId == interaction.InteractionId);
        var forgedResume = completedEvents.Select(item => item.Envelope.EventId == resumeEvent.Envelope.EventId
            ? DomainEvent.Stored(item.Envelope.EventId, item.Envelope.SessionId, item.Envelope.Sequence,
                item.Envelope.Type, item.Envelope.SchemaVersion, item.Envelope.Timestamp, item.Envelope.Causation,
                item.Envelope.CorrelationId, item.Envelope.RunId, TaskId.New(), item.Envelope.LaneId,
                item.Envelope.TurnId, item.Envelope.PlanItemId, item.Envelope.ToolCallId,
                item.Envelope.ArtifactRefs, item.Envelope.PayloadJson, item.Envelope.ExecutionId, item.Envelope.Source)
            : item.Envelope).ToArray();
        Assert.Throws<InvalidStateTransitionException>((Action)(() => _ = RunProjection.Replay(
            SessionId.Parse(sessionId), RunId.Parse(runId), fixture.Server.AcquireCodecs(), forgedResume)));
        Assert.Throws<InvalidStateTransitionException>((Action)(() => _ = CanonicalStateTracker.Replay(
            fixture.Server.AcquireCodecs(), forgedResume)));
        Assert.Single(completedEvents, item => item.Payload is WorktreeIntegrationStateRecorded state
            && state.ProposalId == proposal.ProposalId && state.State == WorktreeIntegrationState.Completed
            && state.CompleteLaneResult);
        var admitted = Assert.IsType<NativeCommandInvocationAdmitted>(Assert.Single(completedEvents,
            item => item.Payload is NativeCommandInvocationAdmitted).Payload);
        Assert.Equal("nativeTool", admitted.Kind);
        var toolRequest = Assert.Single(completedEvents, item => item.Payload is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + invocationId);
        var toolCallId = Assert.IsType<ToolCallRequested>(toolRequest.Payload).ToolCallId;
        Assert.Contains(completedEvents, item => item.Envelope.Sequence > toolRequest.Envelope.Sequence
            && item.Envelope.ToolCallId == toolCallId && item.Payload is ToolCallSucceeded);

        var retry = Ack(fixture.ExchangeTrusted(request, sessionId, runId));
        Assert.Equal(ack.FirstSeq, retry.FirstSeq);
        Assert.Equal(ack.LastSeq, retry.LastSeq);
        Assert.Single(fixture.ReadEvents(sessionId), item => item.Payload is WorktreeIntegrationStateRecorded state
            && state.ProposalId == proposal.ProposalId && state.State == WorktreeIntegrationState.Completed);
        Assert.Equal("native worktree marker " + binding.Marker + "\n",
            File.ReadAllText(Path.Combine(binding.Identity.RepositoryRoot, "new-native-file.txt")));

        var descriptorContract = StdioProtocol.NativeCommands.Single(item => item.Name == "worktree.integrate");
        Assert.Equal("command.invoke.user", descriptorContract.WireName);
        Assert.Equal("command.invoke.user", descriptorContract.Capability);
        Assert.Contains("canonicalCommandId=core:worktree.integrate", descriptorContract.Variant);
    }

    [Fact]
    public async Task Native_worktree_deny_resumes_without_user_input_or_git_effect()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"),
            permissionsYaml: "profile: balanced\nrules:\n  tool.search: allow\n  worktree.integrate: ask\n");
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
        var scope = fixture.QueryJson("humanActionScope");
        var runId = scope.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup Run is not active after its Plan turn.");
        var modeAck = Ack(fixture.ExchangeTrusted(WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select", runId, mode = "act",
            expectedRevision = scope.GetProperty("revision").GetInt64(),
        })), sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, modeAck.Outcome?.Kind);
        var binding = await CreateNativeWorktreeBindingAsync(fixture, sessionId, runId);
        var worktreePath = Path.Combine(binding.Identity.WorktreePath, "deny-native-file.txt");
        File.WriteAllText(worktreePath, "must remain in the worktree\n", new UTF8Encoding(false));
        var preview = Assert.IsType<WorktreeIntegrationPreview>((await new GitWorktreeStore(SystemProcessRuntime.Instance())
            .PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(binding.Identity), CancellationToken.None)).Preview);
        var lane = Assert.Single(fixture.QueryJson("agents").GetProperty("lanes").EnumerateArray(), item =>
            item.GetProperty("objective").GetString() == "Fixture child worktree");
        var laneId = lane.GetProperty("laneId").GetString()
            ?? throw new InvalidOperationException("The public agents query omitted its Lane ID.");
        var publicPreview = fixture.QueryJson("worktree.preview:" + laneId);
        Assert.Equal(binding.Identity.OwnershipId, publicPreview.GetProperty("ownershipId").GetString());
        Assert.Equal(preview.ProposalId, publicPreview.GetProperty("proposalId").GetString());
        var paths = publicPreview.GetProperty("changes").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();
        var args = JsonSerializer.Serialize(new { ownershipId = publicPreview.GetProperty("ownershipId").GetString(),
            proposalId = publicPreview.GetProperty("proposalId").GetString(), paths });
        var catalog = fixture.QueryJson("command.catalog");
        var descriptor = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:worktree.integrate");
        var invocationId = Guid.NewGuid().ToString("D");
        var request = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user", canonicalCommandId = "core:worktree.integrate",
            arguments = new[] { "worktree.integrate", args }, expectedSessionId = sessionId, expectedRunId = runId,
            expectedModeRevision = NullableJsonInt64(catalog.GetProperty("modeRevision")),
            expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
            catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString(),
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, Ack(fixture.ExchangeTrusted(request, sessionId, runId)).Outcome?.Kind);
        await WaitForPermissionRequestAsync(fixture, sessionId, runId);
        await fixture.WaitForReceiptExecutionAsync(invocationId, "deferred");
        var pending = fixture.ReadEvents(sessionId);
        var permission = Assert.IsType<PermissionRequested>(Assert.Single(pending, item => item.Payload is PermissionRequested).Payload);
        var interaction = Assert.IsType<InteractionRequested>(Assert.Single(pending, item => item.Payload is InteractionRequested).Payload);
        Assert.DoesNotContain(pending, item => item.Envelope.ToolCallId == permission.ToolCallId
            && item.Payload is ToolCallStarted or WorktreeIntegrationStateRecorded);
        Assert.False(File.Exists(Path.Combine(binding.Identity.RepositoryRoot, "deny-native-file.txt")));

        var security = fixture.QueryJson("sessionSecurity");
        var deniedCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "interaction.respond", responseType = "choice", interactionId = interaction.InteractionId.ToString(),
            optionId = "deny", expectedRunId = runId,
            expectedPolicyRevision = security.GetProperty("revision").GetInt64(),
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted,
            Ack(fixture.ExchangeTrusted(deniedCommand, sessionId, runId)).Outcome?.Kind);
        await fixture.WaitForReceiptExecutionAsync(invocationId, "failed");
        var afterDeny = fixture.ReadEvents(sessionId);
        Assert.Contains(afterDeny, item => item.Envelope.ToolCallId == permission.ToolCallId
            && item.Payload is PermissionDenied);
        Assert.Contains(afterDeny, item => item.Envelope.TaskId == pending.Single(value => value.Payload is PermissionRequested).Envelope.TaskId
            && item.Payload is RunInteractionResumed resumed && resumed.InteractionId == interaction.InteractionId
            && resumed.CommandId == deniedCommand.MessageId);
        Assert.DoesNotContain(afterDeny, item => item.Envelope.ToolCallId == permission.ToolCallId
            && item.Payload is ToolCallStarted or WorktreeIntegrationStateRecorded or ToolCallSucceeded);
        Assert.DoesNotContain(afterDeny, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId);
        Assert.Equal("must remain in the worktree\n", File.ReadAllText(worktreePath));
        Assert.False(File.Exists(Path.Combine(binding.Identity.RepositoryRoot, "deny-native-file.txt")));
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.Equal("native_tool_failed", fixture.ReadReceipt(invocationId)?.ExecutionCode);
    }

    private static async Task<(WorktreeIdentity Identity, string Marker)> CreateNativeWorktreeBindingAsync(
        NativeCommandHandoffFixture fixture, string sessionId, string runId)
    {
        Git(fixture.Workspace, "init", "--quiet", "--initial-branch=main");
        Git(fixture.Workspace, "config", "core.autocrlf", "false");
        Git(fixture.Workspace, "config", "commit.gpgSign", "false");
        File.WriteAllText(Path.Combine(fixture.Workspace, ".gitignore"), ".omnicore/\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Workspace, "integration-target.txt"), "base\n", new UTF8Encoding(false));
        Git(fixture.Workspace, "add", "-A");
        Git(fixture.Workspace, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "base fixture");

        var journal = fixture.ReadEvents(sessionId);
        var run = RunId.Parse(runId);
        var parentTaskEvent = Assert.Single(journal, item => item.Envelope.RunId == run && item.Payload is TaskCreated);
        var parentTask = Assert.IsType<TaskCreated>(parentTaskEvent.Payload);
        var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(fixture.Workspace);
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalRoot));
        var dataRoot = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), physicalRoot);
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var taskId = TaskId.New();
        var laneId = LaneId.New();
        var executionId = ExecutionId.New();
        var profileId = ProfileId.New();
        var created = await store.CreateAsync(new WorktreeCreateRequest(workspaceId, laneId,
            physicalRoot, dataRoot, WorktreeBase.Head, IgnoredFilesPolicy.None), CancellationToken.None);
        Assert.True(created.Succeeded, "test_worktree_create_failed:" + created.Error);
        var identity = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var packetRef = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 1,
            "application/vnd.omnicore.task-packet+json", ArtifactKind.Other, Sensitivity.Sensitive);
        var projectionRef = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('b', 64)), 1,
            "application/vnd.omnicore.selected-context+json", ArtifactKind.Other, Sensitivity.Sensitive);
        var binding = new LaneWorktreeCreated(taskId, laneId, executionId,
            identity.OwnershipId, identity.RepositoryRoot, identity.GitCommonDirectory, identity.WorktreePath,
            identity.SnapshotCommit, identity.SnapshotTree, packetRef, projectionRef);
        var codecs = EventCodecs.Create();
        var session = SessionId.Parse(sessionId);
        var journalStore = fixture.Server.AcquireStore();
        void Append(DomainEventPayload payload, TaskId? eventTask = null, LaneId? eventLane = null,
            ExecutionId? eventExecution = null)
        {
            var codec = codecs.CodecFor(payload.Type());
            var envelope = DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, run, run,
                eventTask, eventLane, null, null, null, [], codec.Encode(payload), eventExecution);
            journalStore.Append(session, envelope, DurabilityClass.Barrier, CancellationToken.None);
        }
        var budget = new TaskBudget(1m, 8_000, 2, 8);
        Append(new TaskCreated(taskId, run, "Fixture child worktree", Array.Empty<TaskDependency>(), budget,
            parentTask.TaskId), taskId);
        Append(new TaskReady(taskId), taskId);
        Append(new LaneCreated(laneId, taskId, profileId), taskId, laneId);
        Append(new TaskStarted(taskId, laneId), taskId, laneId);
        Append(new LaneProvisioning(laneId), taskId, laneId);
        Append(binding, taskId, laneId, executionId);
        Append(new LaneStarted(laneId), taskId, laneId);
        Append(new AgentExecutionStarted(executionId, laneId, profileId, null,
            ExecutionRelation.Detached, ExecutionSupervision.Unmanaged), taskId, laneId, executionId);

        var marker = "NATIVE-WORKTREE-" + Guid.NewGuid().ToString("N");
        return (identity, marker);
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("test_git_start_failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("test_git_failed:" + stderr);
        return stdout;
    }

    private static async Task WaitForPermissionRequestAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string runId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (fixture.ReadEvents(sessionId).Any(item => item.Envelope.RunId?.ToString() == runId
                && item.Payload is PermissionRequested)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Native worktree action did not reach the Ask decision.");
    }
}
