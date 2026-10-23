namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;
using OmniCore.Infrastructure;
using OmniCore.Execution;
using System.Text.Json;

public sealed partial class OmniServer
{
    private const string NativeProcessControlCommandId = "core:process.control";
    private static readonly string NativeProcessControlFingerprint = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-process-control-v1")));
    private const string NativeWorktreeIntegrationCommandId = "core:worktree.integrate";
    private static readonly string NativeWorktreeIntegrationFingerprint = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("omnicore-native-worktree-integration-v1")));

    private string EncodeProcessSessions(SessionId session, RunId run)
    {
        if (_lastSessionId != session || _lastRunId != run) return "null";
        const int processJournalWindow = 4096;
        var highWater = _store.CurrentSequence(session);
        var fromSequence = Math.Max(1, highWater - processJournalWindow + 1);
        var truncated = fromSequence > 1;
        var events = _store.ReadFrom(session, fromSequence).Where(evt => evt.RunId == run).ToArray();
        var projection = RunProjection.Replay(session, run, _codecs, events);
        var activeRun = new RunControlService(_store, _codecs).ActiveRun(session);
        var active = activeRun == run && projection.State is (RunState.Running or RunState.AwaitingInput
            or RunState.Validating);
        var terminal = activeRun is null && projection.State is (RunState.Completed or RunState.CompletedWithIssues
            or RunState.Failed or RunState.Cancelled);
        // This endpoint can inspect only the server's exact current Run, while it is active
        // or after a durable terminal event. It never resumes that Run or acquires a manager.
        if (!active && !terminal) return "null";
        var rootLane = projection.RootTask is { } rootTask
            ? LaneProjection.Replay(_codecs, events).ForTask(rootTask).FirstOrDefault()?.Id : null;
        var summaries = _processSessions.TryGetValue(session, out var manager)
            ? manager.Snapshot(run.ToString())
            : events.Select(evt => (evt.Sequence, Payload: _codecs.Decode(evt)))
                .Where(item => item.Payload is ProcessSessionStateRecorded)
                .GroupBy(item => ((ProcessSessionStateRecorded)item.Payload).HandleId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Sequence).Last().Payload)
                .OfType<ProcessSessionStateRecorded>()
                .Where(item => item.RunId == run.ToString())
                .Select(item => new ProcessSessionSummary(item.HandleId,
                    item.State is "starting" or "running" or "stopping" ? "unknown" : item.State,
                    item.RunId, item.LaneId, item.LastOutputSequence, item.ExitCode, item.ProposalId,
                    item.SourceToolId ?? "process.start", item.LastOutputSequence > 0,
                    false, false, false)).ToArray();
        var rows = summaries.Take(32).Select(item => "{" + string.Join(",", new[]
        {
            JsonObj.Field("handle", item.Handle), JsonObj.Field("state", item.State),
            JsonObj.Field("runId", item.RunId), JsonObj.Field("laneId", item.LaneId),
            JsonObj.Field("sourceTool", item.SourceToolId),
            "\"lastOutputSequence\":" + item.LastOutputSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.ExitCode is { } exit ? "\"exitCode\":" + exit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "\"exitCode\":null",
            item.ProposalId is { } proposal ? JsonObj.Field("proposalId", proposal) : "\"proposalId\":null",
            JsonObj.FieldBool("outputGap", item.OutputLost),
            JsonObj.FieldBool("rootLane", rootLane is not null && item.LaneId == rootLane.Value.ToString()),
            JsonObj.FieldBool("canRead", active && item.CanRead && rootLane is not null && item.LaneId == rootLane.Value.ToString()),
            JsonObj.FieldBool("canInput", active && item.CanInput && rootLane is not null && item.LaneId == rootLane.Value.ToString()),
            JsonObj.FieldBool("canStop", active && item.CanStop && rootLane is not null && item.LaneId == rootLane.Value.ToString()),
        }) + "}");
        return "{" + JsonObj.Field("sessionId", session.ToString()) + "," + JsonObj.Field("runId", run.ToString())
            + ",\"sequence\":" + highWater.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"truncated\":" + (truncated ? "true" : "false")
            + ",\"processes\":[" + string.Join(",", rows) + "]}";
    }

    private string EncodeProcessOutput(SessionId session, RunId run, string handle, long after)
    {
        if (_lastSessionId != session || _lastRunId != run || after < 0
            || new RunControlService(_store, _codecs).ActiveRun(session) is { } active && active != run)
            return "null";
        const int processJournalWindow = 4096;
        var highWaterSequence = _store.CurrentSequence(session);
        var events = _store.ReadFrom(session, Math.Max(1, highWaterSequence - processJournalWindow + 1))
            .Where(evt => evt.RunId == run).ToArray();
        var projection = RunProjection.Replay(session, run, _codecs, events);
        if (new RunControlService(_store, _codecs).ActiveRun(session) is null
            && projection.State is not (RunState.Completed or RunState.CompletedWithIssues or RunState.Failed or RunState.Cancelled))
            return "null";
        var rootTask = projection.RootTask;
        var rootLane = rootTask is { } task
            ? LaneProjection.Replay(_codecs, events).ForTask(task).FirstOrDefault()?.Id.ToString() : null;
        var lane = events.Select(evt => _codecs.Decode(evt)).OfType<ProcessSessionStateRecorded>()
            .LastOrDefault(item => item.HandleId == handle && item.RunId == run.ToString())?.LaneId;
        if (lane is null || rootLane is null || lane != rootLane) return "null";
        IReadOnlyList<ProcessOutputChunk> chunks = Array.Empty<ProcessOutputChunk>();
        var last = after;
        var highWater = 0L;
        var gap = true;
        var state = "unknown";
        int? exitCode = null;
        if (_processSessions.TryGetValue(session, out var manager))
        {
            try
            {
                chunks = manager.Read(handle, run.ToString(), lane, after, 32,
                    out last, out highWater, out gap, out state, out exitCode);
            }
            catch (KeyNotFoundException) { }
        }
        if (highWater == 0)
        {
            var latest = events.Select(evt => _codecs.Decode(evt)).OfType<ProcessSessionStateRecorded>()
                .LastOrDefault(item => item.HandleId == handle && item.RunId == run.ToString());
            if (latest is null || after > latest.LastOutputSequence) return "null";
            highWater = latest.LastOutputSequence;
            last = after;
            if (state == "unknown")
                state = latest.State is "starting" or "running" or "stopping" ? "unknown" : latest.State;
            exitCode = latest.ExitCode;
            gap = gap || after < highWater || latest.State is ("starting" or "running" or "stopping" or "unknown");
        }
        var encoded = string.Join(",", chunks.Select(chunk => "{" + JsonObj.Field("stream", chunk.Stream)
            + "," + JsonObj.Field("text", chunk.Text) + ",\"sequence\":"
            + chunk.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
        return "{" + JsonObj.Field("sessionId", session.ToString()) + "," + JsonObj.Field("runId", run.ToString())
            + "," + JsonObj.Field("handle", handle) + "," + JsonObj.Field("state", state)
            + ",\"chunks\":[" + encoded + "],\"lastDelivered\":" + last.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"highWater\":" + highWater.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"gap\":" + (gap ? "true" : "false")
            + ",\"exitCode\":" + (exitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + "}";
    }

    private string EncodeWorktreePreview(string laneIdText, CancellationToken cancellationToken)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || _workspaceRoot is not { Length: > 0 } workspace
            || !Guid.TryParseExact(laneIdText, "D", out var laneGuid)
            || laneIdText != new LaneId(laneGuid).ToString()) return "null";
        var laneId = new LaneId(laneGuid);
        var bindings = _store.ReadFrom(session, 1).Where(item => item.RunId == run
                && item.TaskId is not null && item.LaneId == laneId && item.ExecutionId is not null
                && _codecs.Decode(item) is LaneWorktreeCreated created && created.LaneId == laneId
                && item.TaskId == created.TaskId && item.ExecutionId == created.ExecutionId
                && PathEqualsForWorktree(created.RepositoryRoot, workspace))
            .Select(item => (item, (LaneWorktreeCreated)_codecs.Decode(item)!)).ToArray();
        if (bindings.Length != 1
            || !TryResolveOwnedLaneWorktree(session, bindings[0].Item2.OwnershipId,
                out var identity, out _, cancellationToken, expectedRun: run)) return "null";
        var preview = new GitWorktreeStore(SystemProcessRuntime.Instance())
            .PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity!), cancellationToken)
            .GetAwaiter().GetResult();
        if (!preview.Succeeded || preview.Preview is not { } value)
            return "{\"state\":\"unavailable\",\"error\":" + NativeJsonString(preview.Error?.ToString() ?? "GitCommandFailed") + "}";
        // Keep both CPU/memory use and the JSON-lines response bounded before formatting paths.
        // Integration accepts at most 64 files, so a larger proposal cannot be applied here.
        if (value.Changes.Count > 64 || value.Conflicts.Count > 64)
            return "{\"state\":\"unavailable\",\"error\":\"result_too_large\"}";
        var changes = string.Join(",", value.Changes.Select(change => "{" + JsonObj.Field("path", change.RelativePath)
            + "," + (change.ExpectedPreSha256 is null ? "\"expectedPreSha256\":null" : JsonObj.Field("expectedPreSha256", change.ExpectedPreSha256))
            + "," + (change.ExpectedPostSha256 is null ? "\"expectedPostSha256\":null" : JsonObj.Field("expectedPostSha256", change.ExpectedPostSha256))
            + "," + (change.ExpectedPreMode is null ? "\"expectedPreMode\":null" : JsonObj.Field("expectedPreMode", change.ExpectedPreMode))
            + "," + (change.ExpectedPostMode is null ? "\"expectedPostMode\":null" : JsonObj.Field("expectedPostMode", change.ExpectedPostMode)) + "}"));
        var conflicts = string.Join(",", value.Conflicts.Select(conflict => NativeJsonString(conflict.RelativePath)));
        var result = "{" + JsonObj.Field("sessionId", session.ToString()) + "," + JsonObj.Field("laneId", laneId.ToString())
            + "," + JsonObj.Field("ownershipId", identity!.OwnershipId)
            + "," + JsonObj.Field("proposalId", value.ProposalId) + ",\"workspaceHeadChanged\":"
            + (value.WorkspaceHeadChanged ? "true" : "false") + ",\"workspaceBranchChanged\":"
            + (value.WorkspaceBranchChanged ? "true" : "false") + ",\"conflicts\":[" + conflicts
            + "],\"changes\":[" + changes + "],\"applicable\":"
            + (value.Conflicts.Count == 0 && value.Changes.Count is > 0 and <= 64 ? "true" : "false") + "}";
        return Encoding.UTF8.GetByteCount(result) <= 256 * 1024 ? result
            : "{\"state\":\"unavailable\",\"error\":\"result_too_large\"}";
    }

    private bool TryResolveOwnedLaneWorktree(SessionId session, string ownershipId,
        out WorktreeIdentity? identity, out LaneWorktreeCreated? binding,
        CancellationToken cancellationToken = default, RunId? expectedRun = null)
    {
        identity = null;
        binding = null;
        if (!Guid.TryParseExact(ownershipId, "N", out _) || _workspaceRoot is not { Length: > 0 } workspace)
            return false;
        string root;
        try { root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspace); }
        catch (Exception) { return false; }
        var events = _store.ReadFrom(session, 1);
        var roots = events.Where(item => _codecs.Decode(item) is WorkspaceRootEstablished established
                && established.SessionId == session)
            .Select(item => (item, (WorkspaceRootEstablished)_codecs.Decode(item)!)).ToArray();
        if (roots.Length != 1 || !PathEqualsForWorktree(roots[0].Item2.CanonicalRoot, root)
            || !WorkspaceRootIdentity.Verify(root, roots[0].Item2.DurableIdentity)) return false;
        var candidates = events.Select(envelope => (Envelope: envelope, Payload: _codecs.Decode(envelope)))
            .Where(item => item.Payload is LaneWorktreeCreated created && created.OwnershipId == ownershipId
                && item.Envelope.SessionId == session && item.Envelope.RunId is not null
                && item.Envelope.TaskId == created.TaskId && item.Envelope.LaneId == created.LaneId
                && item.Envelope.ExecutionId == created.ExecutionId
                && PathEqualsForWorktree(created.RepositoryRoot, root))
            .ToArray();
        if (candidates.Length != 1 || candidates[0].Payload is not LaneWorktreeCreated match
            || candidates[0].Envelope.RunId is not { } run || expectedRun is { } requiredRun && run != requiredRun) return false;
        var runEvents = events.Where(item => item.RunId == run).ToArray();
        var task = runEvents.Where(item => item.TaskId == match.TaskId && _codecs.Decode(item) is TaskCreated created
                && created.TaskId == match.TaskId && created.RunId == run)
            .Select(item => (TaskCreated)_codecs.Decode(item)!).ToArray();
        var lane = runEvents.Where(item => item.TaskId == match.TaskId && item.LaneId == match.LaneId
                && _codecs.Decode(item) is LaneCreated)
            .Select(item => (LaneCreated)_codecs.Decode(item)!).ToArray();
        var execution = runEvents.Where(item => item.TaskId == match.TaskId && item.LaneId == match.LaneId
                && item.ExecutionId == match.ExecutionId && _codecs.Decode(item) is AgentExecutionStarted started
                && started.ExecutionId == match.ExecutionId && started.LaneId == match.LaneId)
            .ToArray();
        if (task.Length != 1 || lane.Length != 1 || execution.Length != 1
            || lane[0].TaskId != match.TaskId || lane[0].LaneId != match.LaneId) return false;
        var platformPaths = _platformPathsFactory();
        var dataRoot = Path.Combine(platformPaths.DataDirectory, "workspaces", WorkspaceId.Of(
            ProjectIdentity.CanonicalWorkspacePath(root)).ToString());
        var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
            .LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken).GetAwaiter().GetResult();
        if (!loaded.Succeeded || loaded.Worktree is not { } owned
            || owned.OwnershipId != match.OwnershipId || owned.LaneId != match.LaneId
            || !PathEqualsForWorktree(owned.RepositoryRoot, match.RepositoryRoot)
            || !PathEqualsForWorktree(owned.GitCommonDirectory, match.GitCommonDirectory)
            || !PathEqualsForWorktree(owned.WorktreePath, match.WorktreePath)
            || !string.Equals(owned.SnapshotCommit, match.SnapshotCommit, StringComparison.OrdinalIgnoreCase)) return false;
        identity = owned;
        binding = match;
        return true;
    }

    private bool CanExecuteNativeWorktreeIntegration(SessionId session, RunId run, string argumentsJson,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3
                || !value.TryGetProperty("ownershipId", out var owner) || owner.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("proposalId", out var proposal) || proposal.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array
                || paths.GetArrayLength() is < 1 or > 64) return false;
            var ownershipId = owner.GetString()!;
            var proposalId = proposal.GetString()!;
            var claimed = paths.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? "" : "\0").ToArray();
            if (!IsSha256(proposalId) || claimed.Any(string.IsNullOrWhiteSpace)
                || claimed.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != claimed.Length
                || !TryResolveOwnedLaneWorktree(session, ownershipId, out var identity, out _, cancellationToken,
                    expectedRun: run)) return false;
            var preview = new GitWorktreeStore(SystemProcessRuntime.Instance())
                .PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(identity!), cancellationToken)
                .GetAwaiter().GetResult();
            if (!preview.Succeeded || preview.Preview is not { } current || current.HasConflicts
                || !string.Equals(current.ProposalId, proposalId, StringComparison.Ordinal)) return false;
            var expected = current.Changes.Select(item => item.RelativePath).Order(StringComparer.Ordinal).ToArray();
            Array.Sort(claimed, StringComparer.Ordinal);
            return expected.Length > 0 && expected.Length == claimed.Length
                && expected.SequenceEqual(claimed, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        { return false; }
    }

    internal bool NativeWorktreeIntegrationStillAllowed(SessionId session, RunId run, string argumentsJson) =>
        _lastSessionId == session && _lastRunId == run
        && CanExecuteNativeWorktreeIntegration(session, run, argumentsJson, CancellationToken.None);

    private static bool PathEqualsForWorktree(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception) { return false; }
    }

    private static string NativeJsonString(string value) => "\"" + JsonObj.Escape(value) + "\"";

    internal sealed record NativeCommandPayload(string CanonicalCommandId, string Kind, string[] Arguments,
        string? Prompt, string? Origin, string? ForcedToolName, string? WorkflowId, string? WorkflowVersion);

    private static string NativeCommandPurpose(SessionId session, CommandId invocation) =>
        "native-command:" + session + ":" + invocation;

    private CommandAck HandleNativeCommandAction(WireEnvelope command, string commandName,
        CancellationToken cancellationToken)
    {
        if (_artifacts is not IProtectedArtifactStore protectedStore)
            return new CommandAck(command.MessageId, "error", "command.protected_store_unavailable",
                RuntimeCommandOutcome.Rejected());
        using var request = JsonDocument.Parse(command.PayloadJson);
        var root = request.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !WireString(root, "expectedSessionId", out var expectedSession)
            || !Guid.TryParseExact(expectedSession, "D", out var sessionGuid)
            || expectedSession != sessionGuid.ToString("D")
            || !TryNativeNullableId(root, "expectedRunId", out var expectedRun)
            || !TryNativeNullableRevision(root, "expectedModeRevision", out var expectedMode)
            || !root.TryGetProperty("expectedPolicyRevision", out var policyNode)
            || !policyNode.TryGetInt64(out var expectedPolicy) || expectedPolicy < 1
            || !root.TryGetProperty("expectedSelectionRevision", out var selectionNode)
            || !selectionNode.TryGetInt32(out var expectedSelection) || expectedSelection < 0
            || !TryNativeNullableRevision(root, "expectedRoutingPolicyRevision", out var expectedRouting)
            || !WireString(root, "catalogRevision", out var expectedCatalog)
            || !WireString(root, "sourceFingerprint", out var expectedSource)
            || !NativeCommandIsSha256(expectedCatalog) || !NativeCommandIsSha256(expectedSource))
            return NativeCommandRejected(command.MessageId, "command.invalid_request");

        if (_lastSessionId != new SessionId(sessionGuid))
            return NativeCommandRejected(command.MessageId, "command.session_changed");
        if (commandName is "command.invoke.user" or "process.control")
            return AdmitNativeCommand(command, root, expectedRun, expectedMode, expectedPolicy, expectedSelection,
                expectedRouting, expectedCatalog, expectedSource, protectedStore, cancellationToken,
                nativeProcessControl: commandName == "process.control");

        if (!WireString(root, "invocationId", out var invocationText)
            || !Guid.TryParseExact(invocationText, "D", out var invocationGuid)
            || invocationText != invocationGuid.ToString("D"))
            return NativeCommandRejected(command.MessageId, "command.invalid_request");
        if (commandName == "workflow.continue")
            return AcceptNativeWorkflowContinuation(command, new CommandId(invocationGuid), expectedRun,
                expectedMode, expectedPolicy, expectedSelection, expectedRouting, expectedCatalog, expectedSource);
        return AcceptNativeCommandContinuation(command, new CommandId(invocationGuid), expectedRun, expectedMode,
            expectedPolicy, expectedSelection, expectedRouting, expectedCatalog, expectedSource);
    }

    private CommandAck AcceptNativeWorkflowContinuation(WireEnvelope command, CommandId invocation,
        RunId? expectedRun, long? expectedMode, long expectedPolicy, int expectedSelection,
        long? expectedRouting, string expectedCatalog, string expectedSource)
    {
        var before = _lastSessionId;
        if (before is not { } session || expectedRun is not { } run || !EnsureWriterOwnershipForCommand())
            return NativeCommandRejected(command.MessageId, "workflow.session_unavailable");
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            var admissionEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
                && admitted.InvocationId == invocation && admitted.SessionId == session
                && admitted.RunId == run && admitted.Kind == "compiledWorkflow");
            if (admissionEvent is null || _codecs.Decode(admissionEvent) is not NativeCommandInvocationAdmitted admission
                || !history.Any(evt => evt.Sequence > admissionEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
                || history.Any(evt => evt.Sequence > admissionEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation))
                return NativeCommandRejected(command.MessageId, "workflow.recovery_unavailable");
            if (!TryReadNativeCommandPayload(invocation, out var readable, out var payloadJson)
                || readable != admission || !TryDecodeNativeCommandPayload(payloadJson, out var payload)
                || payload.Kind != "compiledWorkflow"
                || !FixedHashEquals(expectedSource, admission.SourceFingerprint))
                return NativeCommandRejected(command.MessageId, "workflow.admission_invalid");
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" })
                return NativeCommandRejected(command.MessageId, "workflow.execution_uncertain");
            if (admission.RunId != run || admission.ExpectedModeRevision != expectedMode
                || admission.ExpectedPolicyRevision != expectedPolicy
                || admission.ExpectedSelectionRevision != expectedSelection
                || admission.ExpectedRoutingPolicyRevision != expectedRouting
                || !FixedHashEquals(admission.CatalogRevision, expectedCatalog)
                || !NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy,
                    expectedSelection, expectedRouting, expectedCatalog))
                return NativeCommandRejected(command.MessageId, "workflow.scope_changed");

            WorkflowRuntimeAuthorization authorization;
            try
            {
                authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload);
                _ = ReadWorkflowContext(authorization);
                if (!NativeWorkflowInvocationIsReady(authorization, out _, out _))
                    return NativeCommandRejected(command.MessageId, "workflow.continuation_unavailable");
            }
            catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
                or ArgumentException or KeyNotFoundException)
            {
                return NativeCommandRejected(command.MessageId, "workflow.authorization_unavailable");
            }
            _lastWorkflowRequested = authorization.Request;
            _lastWorkflowInvocation = new WorkflowInvocation(invocation.ToString(), authorization.Request,
                session, run);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), session,
                _store.CurrentSequence(session), new CommandId(Guid.Parse(command.MessageId)));
        }
    }

    internal bool NativeWorkflowContinuationIsAllowed(CommandId invocation, out string reasonCode,
        out string? workflowId, out string? stage)
    {
        reasonCode = "workflow.recovery_unavailable";
        workflowId = null;
        stage = null;
        if (_lastSessionId is not { } session || _lastRunId is not { } run) return false;
        var history = _store.ReadFrom(session, 1);
        var admissionEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation && admitted.SessionId == session
            && admitted.RunId == run && admitted.Kind == "compiledWorkflow");
        if (admissionEvent is null || _codecs.Decode(admissionEvent) is not NativeCommandInvocationAdmitted admission
            || !history.Any(evt => evt.Sequence > admissionEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
            || history.Any(evt => evt.Sequence > admissionEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation)) return false;
        var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
        if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" })
        { reasonCode = "workflow.execution_uncertain"; return false; }
        if (!NativeCommandRunIsQuiescent(session, run))
        { reasonCode = "workflow.run_not_quiescent"; return false; }
        if (!TryReadNativeCommandPayload(invocation, out var readable, out var json) || readable != admission
            || !TryDecodeNativeCommandPayload(json, out var payload) || payload.Kind != "compiledWorkflow"
            || payload.WorkflowId is not { Length: > 0 } id || payload.WorkflowVersion is not { Length: > 0 })
        { reasonCode = "workflow.admission_invalid"; return false; }
        workflowId = id;
        try
        {
            var authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload);
            _ = ReadWorkflowContext(authorization);
            if (!NativeWorkflowInvocationIsReady(authorization, out reasonCode, out stage)) return false;
            return true;
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException)
        {
            reasonCode = failure is InvalidDataException ? "workflow.journal_invalid" : "workflow.authorization_unavailable";
            return false;
        }
    }

    internal void RespondNativeWeakSandboxConsent(SessionId session, RunId run, TaskId task, LaneId lane,
        TurnId turn, ToolCallId toolCall, InteractionId interaction, CommandId invocation, string optionId,
        long expectedPolicyRevision, string responseCommandId)
    {
        if (_lastSessionId != session || _lastRunId != run || !EnsureWriterOwnershipForCommand())
            throw new SessionSecurityRevisionConflictException();
        lock (_modeAuthorityMutationGate)
        {
            if (ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedPolicyRevision
                || new RunControlService(_store, _codecs).ActiveRun(session) != run)
                throw new SessionSecurityRevisionConflictException();
            var history = _store.ReadFrom(session, 1);
            var admittedEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted item
                && item.InvocationId == invocation && item.SessionId == session && item.RunId == run
                && item.Kind == "compiledWorkflow");
            if (admittedEvent is null || _codecs.Decode(admittedEvent) is not NativeCommandInvocationAdmitted admission
                || !history.Any(evt => evt.Sequence > admittedEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
                || history.Any(evt => evt.Sequence > admittedEvent.Sequence
                    && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation)
                || ReceiptStore?.ReadCommandReceipt(invocation.ToString()) is not
                    { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
                        ExecutionState: "deferred", ExecutionCode: "input_required", ExecutionRunId: var executionRun }
                || !string.Equals(executionRun, run.ToString(), StringComparison.Ordinal)
                || !TryReadNativeCommandPayload(invocation, out var readable, out var json)
                || readable != admission || !TryDecodeNativeCommandPayload(json, out var payload)
                || payload.Kind != "compiledWorkflow" || payload.WorkflowId is not { Length: > 0 }
                || payload.WorkflowVersion is not { Length: > 0 })
                throw new InteractionNotPendingException(interaction);

            var authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload,
                allowAwaitingWeakSandboxConsent: true);
            if (!WeakSandboxWorkflowAuthorizationIsCurrent(authorization))
                throw new SessionSecurityRevisionConflictException();
            var verify = FindWorkflowStage(ForStage(authorization, WorkflowStage.Verify, null), WorkflowStage.Verify,
                allowAwaitingWeakSandboxConsent: true);
            if (verify is not { } stage || stage.Phase != PreM6RecordPhase.Accepted
                || stage.Item.State is PlanItemState.Failed or PlanItemState.Completed
                || stage.Delegation.ChildTaskId != task || stage.Delegation.ChildLaneId != lane)
                throw new InteractionNotPendingException(interaction);
            if (!NativeWeakConsentScopeMatches(session, run, admission, stage.Delegation,
                task, lane, turn, toolCall, interaction))
                throw new SessionSecurityRevisionConflictException();

            var interactionEvent = history.SingleOrDefault(evt => evt.RunId == run && evt.TaskId == task
                && evt.LaneId == lane && evt.TurnId == turn && evt.ToolCallId == toolCall
                && _codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interaction && request.Kind == InteractionKind.WeakSandboxConsent);
            if (interactionEvent is null) throw new InteractionNotPendingException(interaction);
            var control = new RunControlService(_store, _codecs);
            control.RespondNativeWeakSandboxConsent(session, run, task, lane, turn, toolCall, interaction,
                optionId, responseCommandId);
            // Rehydrate only from this authenticated, exact durable invocation after the response
            // append succeeds. This supplies ExecuteWorkflowAsync's existing current-invocation
            // guard after a Host restart; passive queries never populate this field.
            _lastWorkflowRequested = authorization.Request;
            _lastWorkflowInvocation = new WorkflowInvocation(invocation.ToString(), authorization.Request,
                session, run);
        }
    }

    internal bool CanResolveNativeWeakSandboxConsent(SessionId session, RunId run, TaskId task, LaneId lane,
        TurnId turn, ToolCallId toolCall, InteractionId interaction, CommandId invocation, string optionId,
        long expectedPolicyRevision)
    {
        if (optionId is not ("consent_once" or "deny") || _lastSessionId != session || _lastRunId != run
            || ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedPolicyRevision
            || new RunControlService(_store, _codecs).ActiveRun(session) != run) return false;
        var history = _store.ReadFrom(session, 1);
        var admittedEvent = history.LastOrDefault(evt => _codecs.Decode(evt) is NativeCommandInvocationAdmitted item
            && item.InvocationId == invocation && item.SessionId == session && item.RunId == run
            && item.Kind == "compiledWorkflow");
        if (admittedEvent is null || _codecs.Decode(admittedEvent) is not NativeCommandInvocationAdmitted admission
            || !history.Any(evt => evt.Sequence > admittedEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation && started.Kind == "compiledWorkflow")
            || history.Any(evt => evt.Sequence > admittedEvent.Sequence
                && _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation)
            || ReceiptStore?.ReadCommandReceipt(invocation.ToString()) is not
                { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
                    ExecutionState: "deferred", ExecutionCode: "input_required", ExecutionRunId: var executionRun }
            || !string.Equals(executionRun, run.ToString(), StringComparison.Ordinal)
            || !TryReadNativeCommandPayload(invocation, out var readable, out var payloadJson)
            || readable != admission || !TryDecodeNativeCommandPayload(payloadJson, out var payload)
            || payload.Kind != "compiledWorkflow" || payload.WorkflowId is not { Length: > 0 }
            || payload.WorkflowVersion is not { Length: > 0 }) return false;
        try
        {
            var authorization = AuthorizeNativeWorkflowContinuation(invocation, admission, payload,
                allowAwaitingWeakSandboxConsent: true);
            if (!WeakSandboxWorkflowAuthorizationIsCurrent(authorization)) return false;
            var verify = FindWorkflowStage(ForStage(authorization, WorkflowStage.Verify, null), WorkflowStage.Verify,
                allowAwaitingWeakSandboxConsent: true);
            if (verify is not { } stage || stage.Phase != PreM6RecordPhase.Accepted
                || stage.Item.State is PlanItemState.Failed or PlanItemState.Completed
                || stage.Delegation.ChildTaskId != task || stage.Delegation.ChildLaneId != lane) return false;
            if (!NativeWeakConsentScopeMatches(session, run, admission, stage.Delegation,
                task, lane, turn, toolCall, interaction)) return false;
            if (RunProjection.Replay(session, run, _codecs, history).State != RunState.AwaitingInput) return false;
            var requestEvent = history.SingleOrDefault(evt => evt.RunId == run && evt.TaskId == task
                && evt.LaneId == lane && evt.TurnId == turn && evt.ToolCallId == toolCall
                && _codecs.Decode(evt) is InteractionRequested request
                && request.InteractionId == interaction && request.Kind == InteractionKind.WeakSandboxConsent);
            if (requestEvent is null || _codecs.Decode(requestEvent) is not InteractionRequested weakRequest
                || !ProtocolMapper.OptionIds(weakRequest.OptionsJson).Contains(optionId, StringComparer.Ordinal)) return false;
            var call = history.Where(evt => evt.RunId == run && evt.TaskId == task && evt.LaneId == lane
                    && evt.TurnId == turn && evt.ToolCallId == toolCall)
                .Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
            var requested = call.SingleOrDefault(item => item.Payload is ToolCallRequested);
            var prepared = call.SingleOrDefault(item => item.Payload is ToolCallPrepared);
            var authorized = call.LastOrDefault(item => item.Payload is ToolCallAuthorized);
            return requested.Event is not null && prepared.Event is not null && authorized.Event is not null
                && requested.Event.Sequence < prepared.Event.Sequence && prepared.Event.Sequence < requestEvent.Sequence
                && authorized.Event.Sequence < requestEvent.Sequence
                && !call.Any(item => item.Event.Sequence > requestEvent.Sequence
                    && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallFailed
                        or ToolCallEffectUnknown or ToolCallReconciled)
                && !history.Any(evt => evt.RunId == run && _codecs.Decode(evt) is InteractionResolved resolved
                    && resolved.InteractionId == interaction)
                && !history.Any(evt => evt.RunId == run && _codecs.Decode(evt) is InteractionExpired expired
                    && expired.InteractionId == interaction);
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException
            or ArgumentException or KeyNotFoundException or InvalidInteractionOptionException)
        {
            return false;
        }
    }

    private bool NativeWeakConsentScopeMatches(SessionId session, RunId run,
        NativeCommandInvocationAdmitted admission, Delegation verifyDelegation, TaskId task, LaneId lane,
        TurnId turn, ToolCallId toolCall, InteractionId interaction)
    {
        if (_lastSessionId != session || _lastRunId != run || !EnsureWriterOwnershipForCommand()
            || new RunControlService(_store, _codecs).ActiveRun(session) != run
            || ReadSessionSecurityPolicy(session)?.PolicyRevision != admission.ExpectedPolicyRevision
            || CurrentModeAuthority()?.Revision != admission.ExpectedModeRevision)
            return false;
        var history = _store.ReadFrom(session, 1);
        if (!HasOnlyPendingWeakVerifyChildInFlight(session, run, history, verifyDelegation,
                task, lane, turn, toolCall, interaction)) return false;
        var selection = SessionModelSelectionProjection.Read(history, _codecs, session);
        var routing = SessionRoutingAuthorization.Read(history, _codecs, session);
        if ((selection?.Revision ?? 0) != admission.ExpectedSelectionRevision
            || routing?.Revision != admission.ExpectedRoutingPolicyRevision
            || !FixedHashEquals(CurrentNativeCommandCatalog().CatalogRevision, admission.CatalogRevision))
            return false;
        var state = RunProjection.Replay(session, run, _codecs, EventsForRun(history, run)).State;
        return state is RunState.AwaitingInput or RunState.Running;
    }

    private bool HasOnlyPendingWeakVerifyChildInFlight(SessionId session, RunId run,
        IReadOnlyList<DomainEvent> history, Delegation verifyDelegation, TaskId task, LaneId lane,
        TurnId turn, ToolCallId toolCall, InteractionId interaction)
    {
        if (verifyDelegation.ChildTaskId != task || verifyDelegation.ChildLaneId != lane) return false;
        var accepted = history.Where(evt => evt.RunId == run)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .SingleOrDefault(item => item.Payload is DelegationAccepted acceptedEvent
                && acceptedEvent.DelegationId == verifyDelegation.DelegationId
                && acceptedEvent.ExecutionId == verifyDelegation.ParentExecutionId);
        if (accepted.Event is null || accepted.Payload is not DelegationAccepted delegationAccepted
            || delegationAccepted.ChildExecutionId == default) return false;

        var childExecution = delegationAccepted.ChildExecutionId;
        var childStart = history.Where(evt => evt.RunId == run && evt.TaskId == task && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .SingleOrDefault(item => item.Payload is AgentExecutionStarted started
                && started.ExecutionId == childExecution && started.ParentExecutionId == verifyDelegation.ParentExecutionId);
        if (childStart.Event is null) return false;

        var weakRequest = history.Where(evt => evt.RunId == run && evt.TaskId == task && evt.LaneId == lane
                && evt.TurnId == turn && evt.ToolCallId == toolCall && evt.ExecutionId == childExecution)
            .Select(evt => (Event: evt, Payload: _codecs.Decode(evt)))
            .SingleOrDefault(item => item.Payload is InteractionRequested request
                && request.InteractionId == interaction && request.Kind == InteractionKind.WeakSandboxConsent);
        if (weakRequest.Event is null || weakRequest.Event.Sequence <= childStart.Event.Sequence
            || weakRequest.Event.Sequence <= accepted.Event.Sequence) return false;

        var startedTools = new HashSet<ToolCallId>();
        var activeChildren = new HashSet<ExecutionId>();
        foreach (var envelope in history)
        {
            switch (_codecs.Decode(envelope))
            {
                case ToolCallStarted started:
                    startedTools.Add(started.ToolCallId);
                    break;
                case ToolCallSucceeded succeeded:
                    startedTools.Remove(succeeded.ToolCallId);
                    break;
                case ToolCallFailed failed when failed.EffectOutcome != EffectOutcome.Unknown:
                    startedTools.Remove(failed.ToolCallId);
                    break;
                case ToolCallReconciled reconciled when reconciled.Outcome is ReconciliationOutcome.Applied
                    or ReconciliationOutcome.NotApplied:
                    startedTools.Remove(reconciled.ToolCallId);
                    break;
                case AgentExecutionStarted started when started.ParentExecutionId is not null:
                    activeChildren.Add(started.ExecutionId);
                    break;
                case AgentExecutionCompleted completed:
                    activeChildren.Remove(completed.ExecutionId);
                    break;
                case AgentExecutionFailed failed:
                    activeChildren.Remove(failed.ExecutionId);
                    break;
            }
        }
        return startedTools.Count == 0 && activeChildren.Count == 1 && activeChildren.Contains(childExecution);
    }

    private bool NativeWorkflowInvocationIsReady(WorkflowRuntimeAuthorization authorization,
        out string reasonCode, out string? stageName)
    {
        reasonCode = "workflow.stage_state_uncertain";
        stageName = null;
        if (!WorkflowAuthorizationIsCurrent(authorization))
        { reasonCode = "workflow.authorization_unavailable"; return false; }
        foreach (var stage in Enum.GetValues<WorkflowStage>())
        {
            var found = FindWorkflowStage(ForStage(authorization, stage, null), stage);
            if (found is null)
            {
                stageName = stage.ToString();
                reasonCode = "";
                return true;
            }
            var (item, _, phase) = found.Value;
            stageName = stage.ToString();
            if (phase == PreM6RecordPhase.Failed || item.State == PlanItemState.Failed)
            { reasonCode = "workflow.stage_failed"; return false; }
            if (item.State == PlanItemState.Completed && phase is PreM6RecordPhase.Resolved or PreM6RecordPhase.Recorded)
                continue;
            if (phase is PreM6RecordPhase.Created or PreM6RecordPhase.Returned)
            { reasonCode = ""; return true; }
            if (phase == PreM6RecordPhase.Accepted)
            { reasonCode = "workflow.child_pending"; return false; }
            reasonCode = "workflow.stage_state_uncertain";
            return false;
        }
        stageName = "completion";
        reasonCode = "";
        return true;
    }

    private CommandAck AdmitNativeCommand(WireEnvelope command, JsonElement root, RunId? expectedRun,
        long? expectedMode, long expectedPolicy, int expectedSelection, long? expectedRouting,
        string expectedCatalog, string expectedSource, IProtectedArtifactStore protectedStore,
        CancellationToken cancellationToken, bool nativeProcessControl = false)
    {
        if (!Guid.TryParse(command.MessageId, out var commandGuid)
            || !WireString(root, "canonicalCommandId", out var canonicalId)
            || string.IsNullOrWhiteSpace(canonicalId) || canonicalId.Length > 256
            || !root.TryGetProperty("arguments", out var argsNode) || argsNode.ValueKind != JsonValueKind.Array
            || argsNode.GetArrayLength() > 32)
            return NativeCommandRejected(command.MessageId, "command.invalid_request");
        var arguments = new List<string>();
        var argumentChars = 0;
        foreach (var item in argsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || value.Length > 16_384)
                return NativeCommandRejected(command.MessageId, "command.invalid_request");
            argumentChars += value.Length;
            if (argumentChars > 65_536) return NativeCommandRejected(command.MessageId, "command.payload_too_large");
            arguments.Add(value);
        }
        var nativeWorktreeIntegration = canonicalId == NativeWorktreeIntegrationCommandId;
        if (nativeProcessControl && (canonicalId != NativeProcessControlCommandId || expectedRun is null
            || expectedSource != NativeProcessControlFingerprint || arguments.Count != 2
            || arguments[0] is not ("process.read" or "process.send_input" or "process.stop")
            || !IsJsonObject(arguments[1])))
            return NativeCommandRejected(command.MessageId, "process.control.invalid_request");
        if (nativeWorktreeIntegration && (expectedRun is null
            || expectedSource != NativeWorktreeIntegrationFingerprint || arguments.Count != 2
            || arguments[0] != "worktree.integrate" || !IsJsonObject(arguments[1])))
            return NativeCommandRejected(command.MessageId, "worktree.integrate.invalid_request");
        var session = _lastSessionId;
        if (session is null || session.Value.ToString() != WireStringValue(root, "expectedSessionId"))
            return NativeCommandRejected(command.MessageId, "command.session_changed");
        var invocation = new CommandId(commandGuid);
        var before = _store.CurrentSequence(session);
        try
        {
            lock (_modeAuthorityMutationGate)
            {
                if (_lastSessionId != session || !EnsureWriterOwnershipForCommand()
                    || !NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy,
                        expectedSelection, expectedRouting, expectedCatalog,
                        allowActiveProcessControl: nativeProcessControl,
                        processControlToolName: nativeProcessControl ? arguments[0] : null,
                        processControlArgumentsJson: nativeProcessControl ? arguments[1] : null))
                    return NativeCommandRejected(command.MessageId, "command.scope_changed");
                var context = expectedRun is null
                    ? new CommandContext(session, null, ReadModePreference().Mode, _workspaceRoot, false,
                        "A Run is required for workflow authorization.")
                    : CommandContextForCurrentRun();
                var nativeSnapshot = CurrentNativeCommandCatalog();
                if (!FixedHashEquals(expectedCatalog, nativeSnapshot.CatalogRevision))
                    return NativeCommandRejected(command.MessageId, "command.catalog_changed");
                NativeCommandPayload payload;
                string fingerprint;
                if (nativeProcessControl)
                {
                    if (canonicalId != NativeProcessControlCommandId || expectedRun is null
                        || expectedSource != NativeProcessControlFingerprint || arguments.Count != 2
                        || arguments[0] is not ("process.read" or "process.send_input" or "process.stop")
                        || arguments[1].Length > 16_384 || !IsJsonObject(arguments[1]))
                        return NativeCommandRejected(command.MessageId, "process.control.invalid_request");
                    fingerprint = NativeProcessControlFingerprint;
                    payload = new NativeCommandPayload(canonicalId, "nativeTool", arguments.ToArray(),
                        null, null, null, null, null);
                    if (!CanExecuteNativeProcessControl(session, expectedRun,
                            arguments[0], arguments[1]))
                        return NativeCommandRejected(command.MessageId, "process.control.scope_changed");
                }
                else if (nativeWorktreeIntegration)
                {
                    var nativeEntry = nativeSnapshot.Commands.SingleOrDefault(entry =>
                        entry.CanonicalCommandId == NativeWorktreeIntegrationCommandId);
                    if (nativeEntry is not { Enabled: true }
                        || !FixedHashEquals(expectedSource, nativeEntry.SourceFingerprint))
                        return NativeCommandRejected(command.MessageId, nativeEntry?.DisabledReason ?? "command.source_changed");
                    fingerprint = NativeWorktreeIntegrationFingerprint;
                    payload = new NativeCommandPayload(canonicalId, "nativeTool", arguments.ToArray(),
                        null, null, null, null, null);
                    if (!CanExecuteNativeWorktreeIntegration(session, expectedRun!,
                            arguments[1], cancellationToken))
                        return NativeCommandRejected(command.MessageId, "worktree.integrate.scope_changed");
                }
                else
                {
                    var nativeEntry = nativeSnapshot.Commands.SingleOrDefault(entry =>
                        entry.CanonicalCommandId == canonicalId);
                    if (nativeEntry is not { Enabled: true }
                        || !FixedHashEquals(expectedSource, nativeEntry.SourceFingerprint))
                        return NativeCommandRejected(command.MessageId, nativeEntry?.DisabledReason ?? "command.source_changed");
                    var snapshot = _commandService.Catalog(context);
                    var catalogEntry = snapshot.Commands.SingleOrDefault(entry => entry.Id == canonicalId);
                    var handler = _commandService.ResolveCanonical(canonicalId);
                    fingerprint = CommandService.Fingerprint(handler.Descriptor);
                    if (catalogEntry is null || !catalogEntry.Enabled || !FixedHashEquals(expectedSource, fingerprint))
                        return NativeCommandRejected(command.MessageId, "command.source_changed");

                    var outcome = _commandService.InvokeAsync(new CommandInvocation(canonicalId, arguments, "NativeCommand"),
                        context, cancellationToken).AsTask().GetAwaiter().GetResult();
                    payload = outcome switch
                    {
                        PromptCommandRequested prompt when prompt.Text.Length <= 65_536
                            => new NativeCommandPayload(canonicalId,
                                prompt.ForcedToolName is null ? "prompt" : "forcedExtension", arguments.ToArray(),
                                prompt.Text, prompt.Origin, prompt.ForcedToolName, null, null),
                        WorkflowRequested workflow when workflow.Arguments.Sum(value => (long)value.Length) <= 65_536
                            => new NativeCommandPayload(canonicalId, "compiledWorkflow", arguments.ToArray(), null, null,
                                null, workflow.Workflow.Id, workflow.Workflow.Version),
                        _ => throw new InvalidOperationException("command.unsupported_outcome"),
                    };
                }
                var serialized = EncodeNativeCommandPayload(payload);
                if (Encoding.UTF8.GetByteCount(serialized) > 92_000)
                    return NativeCommandRejected(command.MessageId, "command.payload_too_large");
                var purpose = NativeCommandPurpose(session, invocation);
                using var publication = (_artifacts as IArtifactPublicationLease)?.AcquirePublicationLease(cancellationToken);
                var reference = protectedStore.PutProtectedText(serialized, purpose, "application/json", ArtifactKind.Other);
                if (reference.Size is <= 0 or > 131_072)
                    return NativeCommandRejected(command.MessageId, "command.payload_too_large");
                var security = ReadSessionSecurityPolicy(session);
                if (security?.PolicyRevision != expectedPolicy)
                    return NativeCommandRejected(command.MessageId, "command.policy_changed");
                if (expectedRun is null)
                {
                    // A blank Session may start only the built-in explanatory prompt. Create the
                    // canonical Run graph and its admission in one journal transaction; no synthetic
                    // user-input event, new Session, or mode/capability elevation is introduced.
                    if (canonicalId != "core:explain" || payload.Kind != "prompt")
                        return NativeCommandRejected(command.MessageId, "command.run_required");
                    var modePreference = ReadModePreference();
                    if (!FixedHashEquals(expectedCatalog, CurrentNativeCommandCatalog(modePreference).CatalogRevision))
                        return NativeCommandRejected(command.MessageId, "command.catalog_changed");
                    var safeObjective = new PiiRedactor().Redact(payload.Prompt ?? "");
                    if (string.IsNullOrWhiteSpace(safeObjective)) safeObjective = "Native prompt command: core:explain";
                    var profile = _agentProfiles.DefaultProfile;
                    var runControl = new RunControlService(_store, _codecs,
                        rootProfile: profile?.Id, rootProfileRevision: profile?.Revision,
                        rootProfileHash: profile is null ? null : AgentProfileFingerprint.Hash(profile),
                        initialReasoningSelection: runId =>
                        {
                            var preference = ReadReasoningPreference();
                            return preference.HasSelection
                                ? new RunReasoningPreferenceSelected(runId, 1, preference.Request,
                                    "UserDefault", "RunCreated", preference.Revision)
                                : null;
                        });
                    using var causation = CausationScope.Begin(new CommandCausation(invocation));
                    var run = runControl.StartRun(session, safeObjective, modePreference.Mode,
                        runId => new DomainEventPayload[]
                        {
                            new NativeCommandInvocationAdmitted(invocation, session, runId, canonicalId,
                                payload.Kind, expectedCatalog, fingerprint, expectedPolicy, 1, expectedSelection,
                                expectedRouting, reference),
                        }, DurabilityClass.Barrier);
                    _lastRunId = run;
                    _lastSnapshot = null;
                    _lastWorkingStateText = "";
                    SaveLastSession();
                    DispatchRunStartLifecycle(session, run);
                }
                else
                {
                    using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: expectedRun));
                    new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationAdmitted(
                        invocation, session, expectedRun, canonicalId, payload.Kind, expectedCatalog, fingerprint,
                        expectedPolicy, expectedMode, expectedSelection, expectedRouting, reference), DurabilityClass.Barrier);
                }
                return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    session, before, invocation);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return before < _store.CurrentSequence(session)
                ? FailedDurableCommandAck(command, session, before, "command.admission_unavailable")
                : NativeCommandRejected(command.MessageId, "command.admission_rejected_" + exception.GetType().Name + "_" + exception.Message);
        }
    }

    private CommandAck AcceptNativeCommandContinuation(WireEnvelope command, CommandId invocation,
        RunId? expectedRun, long? expectedMode, long expectedPolicy, int expectedSelection,
        long? expectedRouting, string expectedCatalog, string expectedSource)
    {
        if (_lastSessionId is not { } session || !EnsureWriterOwnershipForCommand())
            return NativeCommandRejected(command.MessageId, "command.session_unavailable");
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            var admittedEvent = history.LastOrDefault(item => _codecs.Decode(item) is NativeCommandInvocationAdmitted admitted
                && admitted.InvocationId == invocation && admitted.SessionId == session);
            if (admittedEvent is null || _codecs.Decode(admittedEvent) is not NativeCommandInvocationAdmitted admittedPayload)
                return NativeCommandRejected(command.MessageId, "command.recovery_unavailable");
            if (history.Any(item => item.Sequence > admittedEvent.Sequence
                && _codecs.Decode(item) is NativeCommandInvocationStarted started && started.InvocationId == invocation))
                return NativeCommandRejected(command.MessageId, "command.recovery_blocked");
            if (!NativeCommandScopeMatches(session, expectedRun, expectedMode, expectedPolicy, expectedSelection,
                    expectedRouting, expectedCatalog)
                || admittedPayload.ExpectedPolicyRevision != expectedPolicy
                || admittedPayload.ExpectedModeRevision != expectedMode
                || admittedPayload.ExpectedSelectionRevision != expectedSelection
                || admittedPayload.ExpectedRoutingPolicyRevision != expectedRouting
                || !FixedHashEquals(admittedPayload.CatalogRevision, expectedCatalog)
                || !FixedHashEquals(admittedPayload.SourceFingerprint, expectedSource))
                return NativeCommandRejected(command.MessageId, "command.scope_changed");
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: null or "cancelled" })
                return NativeCommandRejected(command.MessageId, "command.recovery_blocked");
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
    }

    private bool NativeCommandScopeMatches(SessionId session, RunId? expectedRun, long? expectedMode,
        long expectedPolicy, int expectedSelection, long? expectedRouting, string expectedCatalog,
        bool allowActiveProcessControl = false, string? processControlToolName = null,
        string? processControlArgumentsJson = null)
    {
        if (_lastSessionId != session || !EnsureWriterOwnershipForCommand()
            || new RunControlService(_store, _codecs).ActiveRun(session) != expectedRun
            || ReadSessionSecurityPolicy(session)?.PolicyRevision != expectedPolicy)
            return false;
        if (expectedRun is { } activeRun)
        {
            if (allowActiveProcessControl
                ? processControlToolName is null || processControlArgumentsJson is null
                    || !CanExecuteNativeProcessControlDuringTurn(session, activeRun,
                        processControlToolName, processControlArgumentsJson)
                : !NativeCommandRunIsQuiescent(session, activeRun)) return false;
        }
        else
        {
            if (expectedMode is not null || HasSessionSecurityInFlightAuthority(session)
                || new RunControlService(_store, _codecs).UnreconciledEffects(session).Count != 0)
                return false;
        }
        var history = _store.ReadFrom(session, 1);
        var selection = SessionModelSelectionProjection.Read(history, _codecs, session);
        var routing = SessionRoutingAuthorization.Read(history, _codecs, session);
        if ((selection?.Revision ?? 0) != expectedSelection || routing?.Revision != expectedRouting)
            return false;
        var mode = expectedRun is null ? null : CurrentModeAuthority()?.Revision;
        if (mode != expectedMode) return false;
        return FixedHashEquals(expectedCatalog, CurrentNativeCommandCatalog().CatalogRevision);
    }

    private static CommandAck NativeCommandRejected(string commandId, string reason) =>
        new(commandId, "error", reason, RuntimeCommandOutcome.Rejected());

    private static bool NativeCommandIsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsJsonObject(string json)
    {
        try { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private static bool TryNativeNullableId(JsonElement root, string name, out RunId? run)
    {
        run = null;
        if (!root.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Null) return true;
        if (node.ValueKind != JsonValueKind.String || node.GetString() is not { } text
            || !Guid.TryParseExact(text, "D", out var value) || text != value.ToString("D")) return false;
        run = new RunId(value);
        return true;
    }

    private static bool TryNativeNullableRevision(JsonElement root, string name, out long? revision)
    {
        revision = null;
        if (!root.TryGetProperty(name, out var node)) return false;
        if (node.ValueKind == JsonValueKind.Null) return true;
        if (!node.TryGetInt64(out var value) || value < 1) return false;
        revision = value;
        return true;
    }

    private static string WireStringValue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string EncodeNativeCommandPayload(NativeCommandPayload payload)
    {
        static string NullableField(string key, string? value) => value is null
            ? JsonObj.FieldRaw(key, "null") : JsonObj.Field(key, value);
        var args = "[" + string.Join(",", payload.Arguments.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]";
        var fields = new List<string>
        {
            JsonObj.Field("canonicalCommandId", payload.CanonicalCommandId), JsonObj.Field("kind", payload.Kind),
            JsonObj.FieldRaw("arguments", args), NullableField("prompt", payload.Prompt),
            NullableField("origin", payload.Origin), NullableField("forcedToolName", payload.ForcedToolName),
            NullableField("workflowId", payload.WorkflowId), NullableField("workflowVersion", payload.WorkflowVersion),
        };
        return "{" + string.Join(",", fields) + "}";
    }

    internal bool TryReadNativeCommandPayload(CommandId invocation, out NativeCommandInvocationAdmitted admission,
        out string payloadJson)
    {
        admission = null!;
        payloadJson = "";
        if (_lastSessionId is not { } session || _artifacts is not IProtectedArtifactStore protectedStore) return false;
        var events = _store.ReadFrom(session, 1);
        var evt = events.LastOrDefault(item => _codecs.Decode(item) is NativeCommandInvocationAdmitted candidate
            && candidate.InvocationId == invocation && candidate.SessionId == session);
        if (evt is null || _codecs.Decode(evt) is not NativeCommandInvocationAdmitted decoded) return false;
        try
        {
            admission = decoded;
            payloadJson = protectedStore.GetProtectedText(decoded.ProtectedPayload,
                NativeCommandPurpose(session, invocation));
            return Encoding.UTF8.GetByteCount(payloadJson) <= 131_072;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            admission = null!;
            payloadJson = "";
            return false;
        }
    }

    internal bool TryBeginNativeCommand(CommandId invocation, bool explicitRecovery,
        out NativeCommandInvocationAdmitted admission, out NativeCommandPayload payload)
    {
        admission = null!;
        payload = null!;
        if (!TryReadNativeCommandPayload(invocation, out var admitted, out var payloadJson)
            || !TryDecodeNativeCommandPayload(payloadJson, out var decoded)) return false;
        var isNativeToolAction = decoded.Kind == "nativeTool";
        var isProcessControl = isNativeToolAction && admitted.CanonicalCommandId == NativeProcessControlCommandId;
        if (isNativeToolAction && !NativeToolActionPayloadMatches(admitted, decoded)) return false;
        lock (_modeAuthorityMutationGate)
        {
            var session = admitted.SessionId;
            if (_lastSessionId != session || admitted.RunId is not { } expectedRun
                || new RunControlService(_store, _codecs).ActiveRun(session) != expectedRun
                || !EnsureWriterOwnershipForCommand()
                || !NativeCommandScopeMatches(session, expectedRun, admitted.ExpectedModeRevision,
                    admitted.ExpectedPolicyRevision, admitted.ExpectedSelectionRevision,
                    admitted.ExpectedRoutingPolicyRevision, admitted.CatalogRevision,
                    allowActiveProcessControl: isProcessControl,
                    processControlToolName: isProcessControl ? decoded.Arguments[0] : null,
                    processControlArgumentsJson: isProcessControl ? decoded.Arguments[1] : null)) return false;
            var history = _store.ReadFrom(session, 1);
            if (history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation)
                || history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation)) return false;
            var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
            if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted" }
                || receipt.ExecutionState is not null
                    && !(explicitRecovery && receipt.ExecutionState == "cancelled")) return false;

            if (decoded.Kind == "nativeTool")
            {
                if (!NativeToolActionPayloadMatches(admitted, decoded)) return false;
                if (admitted.CanonicalCommandId == NativeWorktreeIntegrationCommandId
                    && !CanExecuteNativeWorktreeIntegration(session, expectedRun, decoded.Arguments[1], CancellationToken.None))
                    return false;
            }
            else
            {
                var liveCatalog = CurrentNativeCommandCatalog();
                var liveDescriptor = liveCatalog.Commands.SingleOrDefault(item =>
                    item.CanonicalCommandId == admitted.CanonicalCommandId);
                if (liveDescriptor is not { Enabled: true }
                    || !FixedHashEquals(liveDescriptor.SourceFingerprint, admitted.SourceFingerprint)) return false;
            }

            WorkflowRequested? workflowToSet = null;
            PromptExpanded? promptToSet = null;
            if (decoded.Kind == "compiledWorkflow")
            {
                if (decoded.WorkflowId is not { Length: > 0 } workflowId
                    || decoded.WorkflowVersion is not { Length: > 0 } workflowVersion) return false;
                var requested = new WorkflowRequested(new WorkflowRef(workflowId, workflowVersion), decoded.Arguments);
                requested.Validate();
                workflowToSet = requested;
            }
            else if (decoded.Kind is "prompt" or "forcedExtension")
            {
                if (decoded.Prompt is not { Length: > 0 } prompt
                    || decoded.Kind == "forcedExtension" && decoded.ForcedToolName is not { Length: > 0 }
                    || decoded.Kind == "prompt" && decoded.ForcedToolName is not null) return false;
                promptToSet = new PromptExpanded(prompt, decoded.Origin ?? "NativeCommand");
            }
            else if (decoded.Kind != "nativeTool") return false;

            if (decoded.CanonicalCommandId != admitted.CanonicalCommandId || decoded.Kind != admitted.Kind)
                return false;
            using var causation = CausationScope.Begin(new CommandCausation(invocation));
            new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationStarted(invocation,
                decoded.Kind, expectedRun), DurabilityClass.Barrier);
            _lastWorkflowRequested = workflowToSet;
            _lastWorkflowInvocation = workflowToSet is null ? null
                : new WorkflowInvocation(invocation.ToString(), workflowToSet, session, expectedRun);
            _lastPromptExpanded = promptToSet;
            _lastForcedCommandTool = decoded.ForcedToolName;
            _pendingPromptOrigin = decoded.Origin;
            admission = admitted;
            payload = decoded;
            return true;
        }
    }

    private bool NativeToolActionPayloadMatches(NativeCommandInvocationAdmitted admitted,
        NativeCommandPayload payload)
    {
        if (admitted.Kind != "nativeTool" || payload.Kind != "nativeTool" || payload.Arguments.Length != 2
            || payload.Arguments[1].Length > 16_384 || !IsJsonObject(payload.Arguments[1])) return false;
        if (admitted.CanonicalCommandId == NativeProcessControlCommandId)
            return FixedHashEquals(admitted.SourceFingerprint, NativeProcessControlFingerprint)
                && payload.Arguments[0] is "process.read" or "process.send_input" or "process.stop";
        return admitted.CanonicalCommandId == NativeWorktreeIntegrationCommandId
            && FixedHashEquals(admitted.SourceFingerprint, NativeWorktreeIntegrationFingerprint)
            && payload.Arguments[0] == "worktree.integrate";
    }

    internal void FinishNativeCommand(CommandId invocation, string outcome)
    {
        if (_lastSessionId is not { } session || !EnsureWriterOwnershipForCommand()) return;
        lock (_modeAuthorityMutationGate)
        {
            var history = _store.ReadFrom(session, 1);
            if (TryReadNativeCommandPayload(invocation, out var admission, out var payloadJson)
                && admission.SessionId == session && admission.Kind == "nativeTool"
                && TryDecodeNativeCommandPayload(payloadJson, out var payload)
                && NativeToolActionPayloadMatches(admission, payload)
                && payload.Arguments[0] == "worktree.integrate")
                return; // Worktree tool-only completion is represented by its hash-bound execution receipt.
            if (!history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationStarted started
                    && started.InvocationId == invocation)
                || history.Any(evt => _codecs.Decode(evt) is NativeCommandInvocationFinished finished
                    && finished.InvocationId == invocation)) return;
            using var causation = CausationScope.Begin(new CommandCausation(invocation));
            new EventStream(_store, _codecs, session).Append(new NativeCommandInvocationFinished(invocation, outcome,
                new RunControlService(_store, _codecs).ActiveRun(session)), DurabilityClass.Barrier);
        }
    }

    internal bool NativeCommandRequiredEffectSucceeded(CommandId invocation)
    {
        if (!TryReadNativeCommandPayload(invocation, out var admission, out var payloadJson)
            || !TryDecodeNativeCommandPayload(payloadJson, out var payload)) return false;
        if (admission.Kind is "prompt" or "compiledWorkflow") return admission.Kind == payload.Kind;
        if (admission.Kind == "nativeTool")
        {
            if (!NativeToolActionPayloadMatches(admission, payload)
                || admission.RunId is not { } nativeRun) return false;
            var events = _store.ReadFrom(admission.SessionId, 1).Where(item => item.RunId == nativeRun).ToArray();
            var toolName = payload.Arguments[0];
            var providerCallId = "native-action-" + invocation;
            var requestedEvent = events.FirstOrDefault(item => _codecs.Decode(item) is ToolCallRequested call
                && call.ProviderCallId == providerCallId && call.ToolName == toolName);
            return requestedEvent is not null && _codecs.Decode(requestedEvent) is ToolCallRequested requestedCall
                && events.Any(item => item.Sequence > requestedEvent.Sequence && item.ToolCallId == requestedCall.ToolCallId
                    && _codecs.Decode(item) is ToolCallSucceeded);
        }
        if (admission.Kind != "forcedExtension" || admission.RunId is not { } run
            || payload.Kind != "forcedExtension" || payload.ForcedToolName is not { Length: > 0 } forcedToolName)
            return false;

        var history = _store.ReadFrom(admission.SessionId, 1);
        var started = history.FirstOrDefault(evt => evt.RunId == run
            && _codecs.Decode(evt) is NativeCommandInvocationStarted item
            && item.InvocationId == invocation && item.Kind == "forcedExtension");
        if (started is null) return false;

        var runEvents = history.Where(evt => evt.RunId == run).ToArray();
        LaneId? rootLane;
        try
        {
            var runProjection = RunProjection.Replay(admission.SessionId, run, _codecs, runEvents);
            if (runProjection.RootTask is not { } rootTask) return false;
            var rootLanes = LaneProjection.Replay(_codecs, runEvents).ForTask(rootTask);
            if (rootLanes.Count != 1) return false;
            rootLane = rootLanes[0].Id;
        }
        catch (Exception)
        {
            return false;
        }
        if (rootLane is null) return false;
        var turnStarted = runEvents.FirstOrDefault(evt => evt.Sequence > started.Sequence
            && evt.LaneId == rootLane && _codecs.Decode(evt) is TurnStarted turn
            && turn.LaneId == rootLane);
        if (turnStarted?.TurnId is not { } requiredTurn) return false;

        var requested = new HashSet<ToolCallId>();
        var succeeded = new HashSet<ToolCallId>();
        foreach (var evt in runEvents.Where(evt => evt.Sequence > turnStarted.Sequence
            && evt.LaneId == rootLane && evt.TurnId == requiredTurn))
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallRequested call when string.Equals(call.ToolName, forcedToolName, StringComparison.Ordinal):
                    requested.Add(call.ToolCallId);
                    break;
                case ToolCallSucceeded success:
                    succeeded.Add(success.ToolCallId);
                    break;
            }
        }
        return requested.Overlaps(succeeded);
    }

    internal void CompleteNativeWorktreeExecutionReceipt(string invocationText, string requestHash)
    {
        lock (_modeAuthorityMutationGate)
            CompleteNativeWorktreeExecutionReceiptUnderAuthority(invocationText, requestHash);
    }

    private void CompleteNativeWorktreeExecutionReceiptUnderAuthority(string invocationText, string requestHash)
    {
        if (!Guid.TryParseExact(invocationText, "D", out var invocationGuid)
            || invocationText != invocationGuid.ToString("D"))
            throw new InvalidDataException("Native worktree receipt identity is invalid.");
        var invocation = new CommandId(invocationGuid);
        if (!TryReadNativeCommandPayload(invocation, out var admission, out var payloadJson)
            || !TryDecodeNativeCommandPayload(payloadJson, out var payload)
            || admission.Kind != "nativeTool" || admission.RunId is not { } run
            || !NativeToolActionPayloadMatches(admission, payload) || payload.Arguments[0] != "worktree.integrate"
            || _lastSessionId != admission.SessionId || !EnsureWriterOwnershipForCommand()
            || _lastSessionId != admission.SessionId
            || !string.Equals(ReceiptStore?.ReadCommandReceipt(invocationText)?.RequestHash, requestHash,
                StringComparison.Ordinal))
            throw new InvalidDataException("Native worktree receipt admission no longer matches its protected payload.");
        var receipt = ReceiptStore?.ReadCommandReceipt(invocationText);
        if (receipt is not { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" }
            || receipt.SessionId != admission.SessionId.ToString() || receipt.ExecutionRunId != run.ToString())
            throw new InvalidDataException("Native worktree receipt is not the expected deferred command.");

        var history = _store.ReadFrom(admission.SessionId, 1).Where(item => item.RunId == run).ToArray();
        var started = history.SingleOrDefault(item => _codecs.Decode(item) is NativeCommandInvocationStarted value
            && value.InvocationId == invocation && value.Kind == "nativeTool" && value.RunId == run);
        var callEvents = history.Where(item => item.Sequence > (started?.Sequence ?? long.MaxValue)
            && _codecs.Decode(item) is ToolCallRequested call
            && call.ProviderCallId == "native-action-" + invocation).ToArray();
        if (started is null || callEvents.Length != 1 || _codecs.Decode(callEvents[0]) is not ToolCallRequested requested
            || requested.ToolName != payload.Arguments[0]
            || !string.Equals(requested.ArgumentsJson, payload.Arguments[1], StringComparison.Ordinal)
            || callEvents[0].TaskId is null || callEvents[0].LaneId is null || callEvents[0].TurnId is null
            || !NativeToolInvocationMatches(admission.SessionId, run, requested.ProviderCallId,
                requested.ToolName, requested.ArgumentsJson))
            throw new InvalidDataException("Native worktree ToolCall is not bound to its exact admission and scope.");
        var call = requested.ToolCallId;
        var outcomes = history.Where(item => item.Sequence > callEvents[0].Sequence && item.ToolCallId == call
                && _codecs.Decode(item) is ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown
                    or ToolCallReconciled)
            .Select(item => (Event: item, Payload: _codecs.Decode(item))).ToArray();
        using var argumentDocument = JsonDocument.Parse(payload.Arguments[1]);
        var proposalId = argumentDocument.RootElement.GetProperty("proposalId").GetString();
        string state;
        string code;
        var completedIntegration = history.SingleOrDefault(item => item.Sequence > callEvents[0].Sequence
            && item.ToolCallId == call && item.TaskId == callEvents[0].TaskId
            && item.LaneId == callEvents[0].LaneId && item.TurnId == callEvents[0].TurnId
            && _codecs.Decode(item) is WorktreeIntegrationStateRecorded recorded
            && recorded.State == WorktreeIntegrationState.Completed
            && recorded.ProposalId == proposalId);
        if (outcomes.Length == 1 && outcomes[0].Payload is ToolCallSucceeded
            && completedIntegration is not null && completedIntegration.Sequence < outcomes[0].Event.Sequence)
        {
            state = "completed";
            code = "native_tool_completed";
        }
        else if (outcomes.Length == 1 && outcomes[0].Payload is ToolCallFailed
            || outcomes.Length == 0 && history.Any(item => item.ToolCallId == call
                && _codecs.Decode(item) is PermissionDenied))
        {
            state = "failed";
            code = "native_tool_failed";
        }
        else
        {
            state = "unknown";
            code = "effect_outcome_unknown";
        }
        ReceiptStore?.CompleteDeferredCommandExecution(invocationText, requestHash, state, code,
            admission.SessionId.ToString(), run.ToString());
    }

    internal bool NativeToolInvocationMatches(SessionId session, RunId run, string providerCallId,
        string toolName, string argumentsJson)
    {
        const string prefix = "native-action-";
        if (!providerCallId.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(providerCallId[prefix.Length..], "D", out var invocationGuid)
            || providerCallId != prefix + invocationGuid.ToString("D")) return false;
        var invocation = new CommandId(invocationGuid);
        if (!TryReadNativeCommandPayload(invocation, out var admission, out var payloadJson)
            || admission.InvocationId != invocation || admission.SessionId != session || admission.RunId != run
            || !TryDecodeNativeCommandPayload(payloadJson, out var payload)
            || payload.CanonicalCommandId != admission.CanonicalCommandId
            || !NativeToolActionPayloadMatches(admission, payload)
            || payload.Arguments[0] != toolName
            || !string.Equals(payload.Arguments[1], argumentsJson, StringComparison.Ordinal)) return false;
        var events = _store.ReadFrom(session, 1);
        return events.Any(item => _codecs.Decode(item) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation && started.Kind == "nativeTool" && started.RunId == run)
            && !events.Any(item => _codecs.Decode(item) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation);
    }

    internal bool IsNativeWorktreeToolPermission(SessionId session, RunId run, InteractionId interaction,
        string? responseCommandId = null)
    {
        var history = _store.ReadFrom(session, 1);
        var permissions = history.Where(item => item.RunId == run && _codecs.Decode(item) is PermissionRequested request
            && request.InteractionId == interaction).ToArray();
        if (permissions.Length != 1 || _codecs.Decode(permissions[0]) is not PermissionRequested permission
            || permission.ToolCallId is not { } toolCall || permissions[0].TaskId is not { } task
            || permissions[0].LaneId is not { } lane || permissions[0].TurnId is not { } turn
            || permissions[0].ExecutionId is not { } execution) return false;
        var interactions = history.Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
            && item.TurnId == turn && item.ExecutionId == execution
            && (item.ToolCallId is null || item.ToolCallId == toolCall)
            && _codecs.Decode(item) is InteractionRequested request
            && request.InteractionId == interaction && request.Kind == InteractionKind.Permission).ToArray();
        var calls = history.Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
            && item.TurnId == turn && item.ExecutionId == execution && item.ToolCallId == toolCall
            && _codecs.Decode(item) is ToolCallRequested).ToArray();
        if (interactions.Length != 1 || calls.Length != 1
            || _codecs.Decode(calls[0]) is not ToolCallRequested call || call.ToolName != "worktree.integrate"
            || !call.ProviderCallId.StartsWith("native-action-", StringComparison.Ordinal)
            || !Guid.TryParseExact(call.ProviderCallId["native-action-".Length..], "D", out var invocationGuid)
            || call.ProviderCallId != "native-action-" + invocationGuid.ToString("D")) return false;
        var invocation = new CommandId(invocationGuid);
        var receipt = ReceiptStore?.ReadCommandReceipt(invocation.ToString());
        var admissions = history.Where(item => _codecs.Decode(item) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation && admitted.SessionId == session
            && admitted.RunId == run && admitted.Kind == "nativeTool"
            && admitted.CanonicalCommandId == NativeWorktreeIntegrationCommandId).ToArray();
        var starts = history.Where(item => _codecs.Decode(item) is NativeCommandInvocationStarted started
            && started.InvocationId == invocation && started.Kind == "nativeTool" && started.RunId == run).ToArray();
        var admissionAndCallMatch = receipt is { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                OutcomeKind: "Accepted", ExecutionState: "deferred", ExecutionCode: "input_required" }
            && receipt.SessionId == session.ToString() && receipt.ExecutionRunId == run.ToString()
            && admissions.Length == 1 && starts.Length == 1 && starts[0].Sequence > admissions[0].Sequence
            && starts[0].RunId == run
            && NativeToolInvocationMatches(session, run, call.ProviderCallId, call.ToolName, call.ArgumentsJson);
        if (!admissionAndCallMatch) return false;
        var resolutions = history.Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
            && item.TurnId == turn && item.ExecutionId == execution
            && (item.ToolCallId is null || item.ToolCallId == toolCall)
            && _codecs.Decode(item) is InteractionResolved resolved && resolved.InteractionId == interaction).ToArray();
        var expirations = history.Where(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
            && item.TurnId == turn && item.ExecutionId == execution
            && _codecs.Decode(item) is InteractionExpired expired && expired.InteractionId == interaction).ToArray();
        if (responseCommandId is null) return resolutions.Length == 0 && expirations.Length == 0;
        if (!Guid.TryParseExact(responseCommandId, "D", out var responseGuid)
            || responseCommandId != responseGuid.ToString("D") || resolutions.Length != 1 || expirations.Length != 0
            || _codecs.Decode(resolutions[0]) is not InteractionResolved resolution
            || resolution.Cause != InteractionCause.User
            || resolution.OptionId is not ("deny" or "allow_once" or "allow_run" or "allow_workspace")
            || resolutions[0].Causation is not CommandCausation resolutionCause
            || resolutionCause.CommandId.ToString() != responseCommandId)
            return false;
        return history.Any(item => item.RunId == run && item.TaskId == task && item.LaneId == lane
            && item.TurnId == turn && item.ExecutionId == execution && item.ToolCallId == toolCall
            && item.Causation is CommandCausation resumeCause && resumeCause.CommandId.ToString() == responseCommandId
            && _codecs.Decode(item) is RunInteractionResumed resumed && resumed.RunId == run
            && resumed.InteractionId == interaction && resumed.CommandId == responseCommandId)
            && RunProjection.Replay(session, run, _codecs, history).State == RunState.Running;
    }

    private string EncodeNativeCommandRecovery()
    {
        if (_lastSessionId is not { } session)
            return CommandCatalogJson.Encode(new NativeCommandRecoverySnapshot([]));
        var events = _store.ReadFrom(session, 1);
        var admitted = new Dictionary<CommandId, (NativeCommandInvocationAdmitted Event, long Sequence)>();
        var started = new HashSet<CommandId>();
        var finished = new Dictionary<CommandId, NativeCommandInvocationFinished>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case NativeCommandInvocationAdmitted admission when admission.SessionId == session:
                    admitted[admission.InvocationId] = (admission, evt.Sequence);
                    break;
                case NativeCommandInvocationStarted execution:
                    started.Add(execution.InvocationId);
                    break;
                case NativeCommandInvocationFinished completion:
                    finished[completion.InvocationId] = completion;
                    break;
            }
        }

        var receipts = _store is OmniCore.Infrastructure.SqliteEventStore receiptStore ? receiptStore : null;
        var rows = new List<NativeCommandRecoveryEntry>();
        foreach (var (id, value) in admitted.OrderByDescending(item => item.Value.Sequence).Take(32))
        {
            var admission = value.Event;
            var receipt = receipts?.ReadCommandReceipt(id.ToString());
            var processControlCompleted = admission.CanonicalCommandId == NativeProcessControlCommandId
                && receipt is { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                    OutcomeKind: "Accepted", ExecutionState: "completed",
                    ExecutionCode: "native_tool_completed" } completedReceipt
                && string.Equals(completedReceipt.ExecutionRunId, admission.RunId?.ToString(), StringComparison.Ordinal);
            string state;
            string? reason;
            if (finished.TryGetValue(id, out var completion))
            {
                state = completion.OutcomeCode == "completed" ? "completed" : "blocked";
                reason = completion.OutcomeCode == "completed" ? null : "invocation_pipeline_incomplete";
            }
            else if (processControlCompleted)
            {
                // The tool-only control path intentionally has no model-command Started/Finished
                // pair. Its exact terminal execution receipt is the durable completion proof.
                state = "completed";
                reason = null;
            }
            else if (started.Contains(id))
            {
                // Without its exact terminal receipt an ordinary started invocation is uncertain.
                state = "blocked";
                reason = "execution_started_or_uncertain";
            }
            else if (receipt is { State: OmniCore.Infrastructure.CommandReceiptState.Completed,
                         OutcomeKind: "Accepted", ExecutionState: null or "cancelled" })
            {
                state = "ready";
                reason = null;
            }
            else
            {
                state = "blocked";
                reason = "invocation_receipt_unavailable";
            }
            rows.Add(new NativeCommandRecoveryEntry(id.ToString(), admission.SessionId.ToString(),
                admission.RunId?.ToString(), admission.CanonicalCommandId, admission.Kind, state, reason, value.Sequence));
        }
        return CommandCatalogJson.Encode(new NativeCommandRecoverySnapshot(rows));
    }

    private string EncodeNativeWorkflowRecovery()
    {
        if (_lastSessionId is not { } session)
            return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(null, null, null, null, []));
        var run = new RunControlService(_store, _codecs).ActiveRun(session);
        var policyRevision = ReadSessionSecurityPolicy(session)?.PolicyRevision;
        var mode = CurrentModeAuthority();
        if (run is null)
            return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(session.ToString(), null,
                null, policyRevision, []));
        var events = _store.ReadFrom(session, 1);
        var admitted = new Dictionary<CommandId, (NativeCommandInvocationAdmitted Admission, long Sequence)>();
        var started = new HashSet<CommandId>();
        var finished = new Dictionary<CommandId, NativeCommandInvocationFinished>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case NativeCommandInvocationAdmitted value when value.SessionId == session
                    && value.RunId == run && value.Kind == "compiledWorkflow":
                    admitted[value.InvocationId] = (value, evt.Sequence);
                    break;
                case NativeCommandInvocationStarted value when value.Kind == "compiledWorkflow":
                    started.Add(value.InvocationId);
                    break;
                case NativeCommandInvocationFinished value:
                    finished[value.InvocationId] = value;
                    break;
            }
        }
        var rows = new List<NativeWorkflowRecoveryEntry>();
        foreach (var (id, value) in admitted.OrderByDescending(item => item.Value.Sequence).Take(16))
        {
            var admission = value.Admission;
            var state = "blocked";
            string? reason = "workflow_invocation_unavailable";
            string? stageName = null;
            string workflowId = "";
            if (TryReadNativeCommandPayload(id, out var payloadAdmission, out var payloadJson)
                && payloadAdmission == admission && TryDecodeNativeCommandPayload(payloadJson, out var decoded)
                && decoded.WorkflowId is { Length: > 0 } idValue)
                workflowId = idValue;
            if (finished.TryGetValue(id, out var completion))
            {
                state = completion.OutcomeCode == "completed" ? "completed" : "blocked";
                reason = completion.OutcomeCode == "completed" ? null : "workflow_invocation_incomplete";
            }
            else if (!started.Contains(id))
            {
                // The generic command recovery view owns the admitted-but-not-started case.
                continue;
            }
            else if (NativeWorkflowContinuationIsAllowed(id, out var reasonCode, out _, out var nextStage))
            { state = "ready"; reason = null; stageName = nextStage; }
            else
            {
                reason = reasonCode;
                state = reasonCode == "workflow.child_pending" ? "pending" : "blocked";
                stageName = nextStage;
            }
            rows.Add(new NativeWorkflowRecoveryEntry(id.ToString(), session.ToString(), run.ToString(),
                admission.CanonicalCommandId, workflowId, state, reason, stageName, value.Sequence));
        }
        return CommandCatalogJson.Encode(new NativeWorkflowRecoverySnapshot(session.ToString(), run.ToString(),
            mode?.Revision, policyRevision, rows));
    }

    private NativeCommandCatalogSnapshot CurrentNativeCommandCatalog(RunModePreference? modePreferenceOverride = null)
    {
        RefreshProjectExtensionCommands();
        var session = _lastSessionId;
        var run = session is { } currentSession
            ? new RunControlService(_store, _codecs).ActiveRun(currentSession) : null;
        var policy = session is { } policySession ? ReadSessionSecurityPolicy(policySession) : null;
        var modePreference = modePreferenceOverride ?? ReadModePreference();
        var mode = run is null ? null : CurrentModeAuthority();
        SessionModelSelection? selection = null;
        SessionRoutingPolicy? routing = null;
        if (session is { } selectedSession)
        {
            var history = _store.ReadFrom(selectedSession, 1);
            selection = SessionModelSelectionProjection.Read(history, _codecs, selectedSession);
            routing = SessionRoutingAuthorization.Read(history, _codecs, selectedSession);
        }

        var catalogContext = run is not null
            ? CommandContextForCurrentRun()
            : new CommandContext(session, null, modePreference.Mode, _workspaceRoot, false,
                "A Run is required for workflow authorization.");
        var catalog = _commandService.Catalog(catalogContext);
        var modelCatalog = SessionModelCatalog.Read(_platformPathsFactory(), CancellationToken.None);
        var revisionInput = new StringBuilder();
        foreach (var command in catalog.Commands.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            // Enabled/disabled is scoped by the separate Run/mode CAS fields. The catalog token
            // hashes command definitions and sources so it remains stable when the first Run is
            // created atomically with a native invocation.
            AppendRevisionPart(revisionInput, command.Id);
            AppendRevisionPart(revisionInput, command.Name);
            AppendRevisionPart(revisionInput, command.Kind);
            AppendRevisionPart(revisionInput, command.Source);
            AppendRevisionPart(revisionInput, command.SourceFingerprint ?? "");
            AppendRevisionPart(revisionInput, command.Description);
            AppendRevisionPart(revisionInput, command.Arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var argument in command.Arguments) AppendRevisionPart(revisionInput, argument);
            AppendRevisionPart(revisionInput, "command-end");
        }
        AppendRevisionPart(revisionInput, NativeWorktreeIntegrationCommandId);
        AppendRevisionPart(revisionInput, "nativeTool");
        AppendRevisionPart(revisionInput, NativeWorktreeIntegrationFingerprint);
        AppendRevisionPart(revisionInput, "Integrate a passive Git worktree proposal through the normal tool permission and receipt pipeline.");
        AppendRevisionPart(revisionInput, "worktree.integrate");
        AppendRevisionPart(revisionInput, "argumentsJson");
        AppendRevisionPart(revisionInput, "command-end");
        AppendRevisionPart(revisionInput, session?.ToString() ?? "");
        AppendRevisionPart(revisionInput, modePreference.Mode.ToString());
        AppendRevisionPart(revisionInput, modePreference.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendRevisionPart(revisionInput, policy?.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        AppendRevisionPart(revisionInput, selection?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");
        AppendRevisionPart(revisionInput, routing?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
        AppendRevisionPart(revisionInput, modelCatalog.Revision);
        var catalogRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput.ToString())));
        var hasQuiescentScope = session is { } current && policy is not null && (run is { } activeRun
            ? mode is not null && NativeCommandRunIsQuiescent(current, activeRun)
            : !HasSessionSecurityInFlightAuthority(current)
                && new RunControlService(_store, _codecs).UnreconciledEffects(current).Count == 0);
        var effectiveMode = mode?.Mode ?? modePreference.Mode;
        var worktreeModeCompatible = effectiveMode is RunMode.Act or RunMode.Orchestrate;
        var workflowRootReady = session is { } workflowSession && run is { } workflowRun
            && mode?.Mode == RunMode.Orchestrate && HasNativeWorkflowRootContext(workflowSession, workflowRun);
        var commands = catalog.Commands.Select(command =>
        {
            var descriptor = _commandService.ResolveCanonical(command.Id).Descriptor;
            var kind = descriptor.Source.Kind == SourceKind.Extension ? "forcedExtension" : descriptor.Kind switch
            {
                CommandKind.Prompt => "prompt",
                CommandKind.Workflow => "compiledWorkflow",
                _ => "unsupported",
            };
            var modeCompatible = kind switch
            {
                "prompt" => effectiveMode is RunMode.Plan or RunMode.Act,
                "forcedExtension" => run is not null && mode?.Mode == RunMode.Act,
                "compiledWorkflow" => workflowRootReady,
                _ => false,
            };
            var firstPromptAllowed = run is not null || command.Id == "core:explain" && kind == "prompt";
            var enabled = command.Enabled && hasQuiescentScope && firstPromptAllowed && modeCompatible;
            var disabledReason = enabled ? null : command.DisabledReason
                ?? (policy is null ? "A configured Session security policy is required." : !firstPromptAllowed
                    ? "This command requires an existing Session Run." : !hasQuiescentScope
                        ? "The Session Run must be quiescent." : kind == "compiledWorkflow" && !workflowRootReady
                            ? "A live root execution with its persisted context is required for this workflow."
                            : !modeCompatible
                    ? "The command is unavailable in the current Run mode." : "The Session Run must be quiescent.");
            return new NativeCommandCatalogEntry(command.Id, kind, enabled, disabledReason,
                CommandService.Fingerprint(descriptor), command.Description, command.Arguments);
        }).Append(new NativeCommandCatalogEntry(NativeWorktreeIntegrationCommandId, "nativeTool",
            run is not null && hasQuiescentScope && worktreeModeCompatible && _workspaceRoot is { Length: > 0 },
            run is null ? "A Run is required for workflow authorization." : !hasQuiescentScope
                ? "The Session Run must be quiescent." : !worktreeModeCompatible
                    ? "Worktree integration requires Act or Orchestrate mode." : _workspaceRoot is not { Length: > 0 }
                    ? "A physical workspace root is required." : null,
            NativeWorktreeIntegrationFingerprint,
            "Integrate a passive Git worktree proposal through the normal tool permission and receipt pipeline.",
            ["toolName", "argumentsJson"])).ToArray();
        return new NativeCommandCatalogSnapshot(session?.ToString(), run?.ToString(), mode?.Revision,
            policy?.PolicyRevision, selection?.Revision ?? 0, routing?.Revision, catalogRevision, commands);
    }

    private static void AppendRevisionPart(StringBuilder target, string value) =>
        target.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('\n');

    internal bool NativeCommandRunIsQuiescentForProcessAction(SessionId session, RunId run) =>
        NativeCommandRunIsQuiescent(session, run);

    private bool NativeProcessControlRunIsQuiescent(SessionId session, RunId run, LaneId lane,
        PersistentProcessSessionManager manager)
    {
        var history = _store.ReadFrom(session, 1);
        if (IsSessionTurnInFlight(history, run)
            || HasSessionSecurityInFlightAuthorityExceptProcesses(session, run, lane, manager)) return false;
        return RunProjection.Replay(session, run, _codecs, EventsForRun(history, run)).State == RunState.Running;
    }

    private bool NativeCommandRunIsQuiescent(SessionId session, RunId run)
    {
        if (HasSessionSecurityInFlightAuthority(session)) return false;
        var history = _store.ReadFrom(session, 1);
        if (IsSessionTurnInFlight(history, run)) return false;
        return RunProjection.Replay(session, run, _codecs, EventsForRun(history, run)).State == RunState.Running;
    }

    internal static bool TryDecodeNativeCommandPayload(string json, out NativeCommandPayload payload)
    {
        payload = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !WireString(root, "canonicalCommandId", out var canonicalId)
                || !WireString(root, "kind", out var kind)
                || !root.TryGetProperty("arguments", out var argsNode) || argsNode.ValueKind != JsonValueKind.Array)
                return false;
            var arguments = new List<string>();
            foreach (var item in argsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } argument) return false;
                arguments.Add(argument);
            }
            string? ReadNullable(string name) => root.TryGetProperty(name, out var item)
                && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            payload = new NativeCommandPayload(canonicalId, kind, arguments.ToArray(), ReadNullable("prompt"),
                ReadNullable("origin"), ReadNullable("forcedToolName"), ReadNullable("workflowId"),
                ReadNullable("workflowVersion"));
            return true;
        }
        catch (JsonException) { return false; }
    }
}
