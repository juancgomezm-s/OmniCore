namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Authoritative explicit cleanup composition. The caller supplies only an ownership
/// token; repository and workspace data roots are derived from the selected physical workspace.</summary>
public static class WorktreeCleanupHost
{
    /// <summary>Scalar projection for the CLI boundary; Domain error enums stay inside Host.</summary>
    public static async Task<WorktreeCleanupCliOutcome> DiscardForCliAsync(string workspacePath,
        string ownershipId, CancellationToken cancellationToken)
    {
        var result = await DiscardAsync(workspacePath, ownershipId, cancellationToken).ConfigureAwait(false);
        return new(result.Removed, result.Error?.ToString());
    }

    /// <summary>Scalar projection for the CLI boundary; Domain error enums stay inside Host.</summary>
    public static async Task<WorktreeCleanupCliOutcome> CleanupForCliAsync(string workspacePath,
        string ownershipId, CancellationToken cancellationToken)
    {
        var result = await CleanupAsync(workspacePath, ownershipId, cancellationToken).ConfigureAwait(false);
        return new(result.Removed, result.Error?.ToString());
    }

    public static async Task<WorktreeCleanupOutcome> DiscardAsync(string workspacePath, string ownershipId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(ownershipId, "N", out _))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        string root;
        try { root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath); }
        catch (Exception) { return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        var dataRoot = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), root);
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var loaded = await store.LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Worktree is not { } identity)
            return WorktreeCleanupOutcome.Failure(loaded.Error ?? GitWorktreeErrorCode.OwnershipMismatch);
        var journalPath = Path.Combine(dataRoot, "journal.db");
        if (!File.Exists(journalPath)) return await store.DiscardAsync(identity.Ownership, cancellationToken).ConfigureAwait(false);
        var journal = new SqliteEventStore(journalPath);
        try
        {
            if (journal is not IWorkspaceJournalReader reader)
                return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            var codecs = EventCodecs.Create();
            var artifacts = OmniHost.CreateArtifactStore(dataRoot);
            if (HasUnfinishedLane(reader, journal, codecs, identity)
                || HasActiveWorkflowConsumer(reader, journal, codecs, artifacts,
                    WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)).ToString(), identity.OwnershipId))
                return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.WorktreeDirty);
            return await store.DiscardAsync(identity.Ownership, cancellationToken).ConfigureAwait(false);
        }
        finally { journal.Close(); }
    }

    public static async Task<WorktreeCleanupOutcome> CleanupAsync(string workspacePath, string ownershipId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(ownershipId, "N", out _))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        string root;
        try { root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath); }
        catch (Exception) { return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        var dataRoot = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), root);
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var journalPath = Path.Combine(dataRoot, "journal.db");
        if (!File.Exists(journalPath))
        {
            // Preserve the original explicit cleanup path for a genuinely clean, untouched
            // snapshot created before a journal was initialized. The Infrastructure method
            // revalidates ownership, ancestry, and a completely clean worktree and never forces.
            var cleanIdentity = await store.LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken)
                .ConfigureAwait(false);
            if (!cleanIdentity.Succeeded || cleanIdentity.Worktree is not { } owned)
                return WorktreeCleanupOutcome.Failure(cleanIdentity.Error ?? GitWorktreeErrorCode.OwnershipMismatch);
            return await store.CleanupAsync(owned.Ownership, cancellationToken).ConfigureAwait(false);
        }
        var journal = new SqliteEventStore(journalPath);
        try
        {
            return await CleanupWithDependenciesAsync(root, dataRoot, ownershipId, journal, EventCodecs.Create(),
                OmniHost.CreateArtifactStore(dataRoot), store, cancellationToken).ConfigureAwait(false);
        }
        finally { journal.Close(); }
    }

    internal static async Task<WorktreeCleanupOutcome> CleanupWithDependenciesAsync(string root, string dataRoot,
        string ownershipId, IEventStore journal, IEventCodecRegistry codecs, IArtifactStore artifacts,
        GitWorktreeStore store, CancellationToken cancellationToken)
    {
        if (journal is not IWorkspaceJournalReader reader)
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        var loaded = await store.LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Worktree is not { } identity)
            return WorktreeCleanupOutcome.Failure(loaded.Error ?? GitWorktreeErrorCode.OwnershipMismatch);
        var proof = FindIntegratedCleanupProof(reader, journal, codecs, artifacts, root, identity);
        if (proof is not null)
            return await store.CleanupIntegratedAsync(identity.Ownership, proof, cancellationToken).ConfigureAwait(false);

        // Keep the established clean-snapshot cleanup behavior for terminal/legacy lanes. The
        // backend still requires a clean tree and either the unchanged snapshot HEAD or ancestry
        // in the workspace; dirty results require the stronger complete-integration proof above.
        if (HasUnfinishedLane(reader, journal, codecs, identity)
            || HasActiveWorkflowConsumer(reader, journal, codecs, artifacts,
                WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)).ToString(), identity.OwnershipId))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.WorktreeDirty);
        return await store.CleanupAsync(identity.Ownership, cancellationToken).ConfigureAwait(false);
    }

    internal static bool HasUnfinishedLane(IWorkspaceJournalReader reader, IEventStore journal,
        IEventCodecRegistry codecs, WorktreeIdentity identity)
    {
        foreach (var sessionEnvelope in reader.ReadEvents(EventType.Of("session.created")))
        {
            if (codecs.Decode(sessionEnvelope) is not SessionCreated session) continue;
            var events = journal.ReadFrom(session.SessionId, 1);
            var payloads = events.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
            var bindings = payloads.Where(item => item.Payload is LaneWorktreeCreated binding
                    && binding.OwnershipId == identity.OwnershipId)
                .Select(item => (item.Envelope, Binding: (LaneWorktreeCreated)item.Payload!)).ToArray();
            if (bindings.Length > 0)
            {
                if (bindings.Length != 1 || bindings[0].Binding.LaneId != identity.LaneId
                    || bindings[0].Binding.TaskId != bindings[0].Envelope.TaskId
                    || bindings[0].Binding.LaneId != bindings[0].Envelope.LaneId
                    || bindings[0].Binding.ExecutionId != bindings[0].Envelope.ExecutionId)
                    return true;
                var runEvents = payloads.Where(item => item.Envelope.RunId == bindings[0].Envelope.RunId).ToArray();
                if (bindings[0].Envelope.RunId is null || !AllTasksAndLanesAreTerminal(runEvents)
                    || !HasSingleTerminalFor(runEvents, bindings[0].Binding.TaskId, bindings[0].Binding.LaneId))
                    return true;
            }
            foreach (var provisioning in payloads.Where(item => item.Payload is LaneProvisioning value && value.LaneId == identity.LaneId))
            {
                var laneEnvelope = provisioning.Envelope;
                var laneTerminalCount = payloads.Count(item => item.Envelope.RunId == laneEnvelope.RunId
                    && item.Envelope.LaneId == identity.LaneId && item.Payload switch
                    {
                        LaneCompleted terminal => terminal.LaneId == identity.LaneId,
                        LaneFailed terminal => terminal.LaneId == identity.LaneId,
                        LaneCancelled terminal => terminal.LaneId == identity.LaneId,
                        _ => false,
                    });
                var task = payloads.Where(item => item.Envelope.RunId == laneEnvelope.RunId
                        && item.Envelope.LaneId == identity.LaneId && item.Payload is TaskCreated)
                    .Select(item => ((TaskCreated)item.Payload!).TaskId).Distinct().ToArray();
                if (laneTerminalCount != 1 || task.Length != 1
                    || payloads.Count(item => item.Envelope.RunId == laneEnvelope.RunId
                        && item.Envelope.TaskId == task[0] && item.Payload switch
                        {
                            TaskCompleted terminal => terminal.TaskId == task[0],
                            TaskFailed terminal => terminal.TaskId == task[0],
                            TaskCancelled terminal => terminal.TaskId == task[0],
                            TaskSkipped terminal => terminal.TaskId == task[0],
                            _ => false,
                        }) != 1)
                    return true;
            }
        }
        return false;
    }

    private static WorktreeIntegratedCleanupProof? FindIntegratedCleanupProof(IWorkspaceJournalReader reader,
        IEventStore journal, IEventCodecRegistry codecs, IArtifactStore artifacts, string root,
        WorktreeIdentity identity)
    {
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)).ToString();
        var matches = new List<WorktreeIntegratedCleanupProof>();
        foreach (var sessionEnvelope in reader.ReadEvents(EventType.Of("session.created")))
        {
            if (codecs.Decode(sessionEnvelope) is not SessionCreated session
                || !string.Equals(session.WorkspaceId, workspaceId, StringComparison.Ordinal)) continue;
            var events = journal.ReadFrom(session.SessionId, 1);
            var payloads = events.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
            var roots = payloads.Where(item => item.Payload is WorkspaceRootEstablished established
                && established.SessionId == session.SessionId).Select(item => (WorkspaceRootEstablished)item.Payload!).ToArray();
            if (roots.Length != 1 || !PathEquals(roots[0].CanonicalRoot, root)
                || !WorkspaceRootIdentity.Verify(root, roots[0].DurableIdentity)) continue;

            var bindings = payloads.Where(item => item.Payload is LaneWorktreeCreated created
                    && created.OwnershipId == identity.OwnershipId)
                .Select(item => (item.Envelope, Binding: (LaneWorktreeCreated)item.Payload!)).ToArray();
            if (bindings.Length == 0) continue;
            if (bindings.Length != 1) return null;
            var binding = bindings[0].Binding;
            if (!PathEquals(binding.RepositoryRoot, identity.RepositoryRoot)
                || !PathEquals(binding.WorktreePath, identity.WorktreePath)
                || !PathEquals(binding.GitCommonDirectory, identity.GitCommonDirectory)
                || binding.SnapshotCommit != identity.SnapshotCommit
                || bindings[0].Envelope.TaskId != binding.TaskId || bindings[0].Envelope.LaneId != binding.LaneId
                || bindings[0].Envelope.ExecutionId != binding.ExecutionId)
                return null;
            if (HasActiveWorkflowConsumer(reader, journal, codecs, artifacts, workspaceId, identity.OwnershipId))
                return null;

            var bindingRunId = bindings[0].Envelope.RunId;
            if (bindingRunId is null) return null;
            var runEvents = payloads.Where(item => item.Envelope.RunId == bindingRunId).ToArray();
            var rootRuns = runEvents.Where(item => item.Payload is RunCreated run
                    && run.SessionId == session.SessionId && run.RunId == bindingRunId)
                .Select(item => (item.Envelope, Run: (RunCreated)item.Payload!)).ToArray();
            if (rootRuns.Length != 1) return null;
            var rootTask = rootRuns[0].Run.RootTask;
            var rootTaskStarts = runEvents.Where(item => item.Payload is TaskStarted started
                    && started.TaskId == rootTask)
                .Select(item => (item.Envelope, Start: (TaskStarted)item.Payload!)).ToArray();
            if (rootTaskStarts.Length != 1) return null;
            var rootLane = rootTaskStarts[0].Start.LaneId;

            // No Task or Lane consumer in this journal scope may still be running. This includes
            // Verify lanes, which are consumers of an implementation worktree rather than owners.
            if (!AllTasksAndLanesAreTerminal(runEvents)) return null;
            if (!HasSingleTerminalFor(runEvents, binding.TaskId, binding.LaneId)) return null;

            var completions = runEvents.Where(item => item.Payload is WorktreeIntegrationStateRecorded state
                    && state.OwnershipId == identity.OwnershipId && state.State == WorktreeIntegrationState.Completed)
                .Select(item => (item.Envelope, State: (WorktreeIntegrationStateRecorded)item.Payload!)).ToArray();
            if (completions.Length != 1) return null;
            var completion = completions[0];
            var state = completion.State;
            if (!state.CompleteLaneResult || !IsOid(state.LaneTreeAtCapture) || !IsOid(state.LaneHeadAtCapture)
                || !IsOid(state.ProposalId) || state.Files is not { Count: > 0 }
                || state.Files.Any(file => file.Outcome != ReconciliationOutcome.Applied)
                || completion.Envelope.LaneId != rootLane
                || completion.Envelope.TaskId != rootTask
                || completion.Envelope.RunId != rootRuns[0].Run.RunId)
                return null;

            var matchingStarts = runEvents.Where(item => item.Payload is WorktreeIntegrationStateRecorded started
                    && started.ToolCallId == state.ToolCallId && started.OwnershipId == identity.OwnershipId
                    && started.ProposalId == state.ProposalId && started.State == WorktreeIntegrationState.Started
                    && started.LaneTreeAtCapture == state.LaneTreeAtCapture
                    && started.LaneHeadAtCapture == state.LaneHeadAtCapture && started.CompleteLaneResult)
                .ToArray();
            var toolStarts = runEvents.Where(item => item.Payload is ToolCallStarted started
                    && started.ToolCallId == state.ToolCallId).ToArray();
            var requested = runEvents.Where(item => item.Payload is ToolCallRequested request
                    && request.ToolCallId == state.ToolCallId && request.ToolName == "worktree.integrate")
                .Select(item => (item.Envelope, Request: (ToolCallRequested)item.Payload!)).ToArray();
            var succeeded = runEvents.Where(item => item.Payload is ToolCallSucceeded success
                    && success.ToolCallId == state.ToolCallId).ToArray();
            if (matchingStarts.Length != 1 || toolStarts.Length != 1 || requested.Length != 1 || succeeded.Length != 1
                || matchingStarts[0].Envelope.LaneId != rootLane
                || matchingStarts[0].Envelope.TaskId != rootTask
                || matchingStarts[0].Envelope.RunId != rootRuns[0].Run.RunId
                || toolStarts[0].Envelope.LaneId != rootLane
                || toolStarts[0].Envelope.TaskId != rootTask
                || toolStarts[0].Envelope.RunId != rootRuns[0].Run.RunId
                || requested[0].Envelope.LaneId != rootLane
                || requested[0].Envelope.TaskId != rootTask
                || requested[0].Envelope.RunId != rootRuns[0].Run.RunId
                || succeeded[0].Envelope.LaneId != rootLane
                || succeeded[0].Envelope.TaskId != rootTask
                || succeeded[0].Envelope.RunId != rootRuns[0].Run.RunId)
                return null;
            var startState = (WorktreeIntegrationStateRecorded)matchingStarts[0].Payload!;
            if (startState.Files is not { Count: > 0 }
                || !startState.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(state.Files.Select(file => file.RelativePath))) return null;
            var startedTool = (ToolCallStarted)toolStarts[0].Payload!;
            if (!WorktreeIntegrationHostCoordinator.ManifestAuthorizesIntegratedCleanup(artifacts, root, identity,
                    state, startedTool, requested[0].Request.ArgumentsJson)
                || !IntegrationArgumentsMatch(requested[0].Request.ArgumentsJson, identity.OwnershipId, state.ProposalId,
                    state.Files.Select(file => file.RelativePath)))
                return null;
            matches.Add(new WorktreeIntegratedCleanupProof(identity.OwnershipId, state.ProposalId,
                state.LaneTreeAtCapture, state.LaneHeadAtCapture, state.ToolCallId.Value.ToString("N"), true));
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool AllTasksAndLanesAreTerminal(
        IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> events)
    {
        var tasks = events.Where(item => item.Payload is TaskCreated).Select(item => ((TaskCreated)item.Payload!).TaskId).Distinct().ToArray();
        foreach (var task in tasks)
            if (events.Count(item => item.Payload switch
                {
                    TaskCompleted terminal => terminal.TaskId == task,
                    TaskFailed terminal => terminal.TaskId == task,
                    TaskCancelled terminal => terminal.TaskId == task,
                    TaskSkipped terminal => terminal.TaskId == task,
                    _ => false,
                }) != 1) return false;
        var lanes = events.Where(item => item.Payload is LaneCreated).Select(item => ((LaneCreated)item.Payload!).LaneId).Distinct().ToArray();
        foreach (var lane in lanes)
            if (events.Count(item => item.Payload switch
                {
                    LaneCompleted terminal => terminal.LaneId == lane,
                    LaneFailed terminal => terminal.LaneId == lane,
                    LaneCancelled terminal => terminal.LaneId == lane,
                    _ => false,
                }) != 1) return false;
        return true;
    }

    internal static bool HasActiveWorkflowConsumer(IWorkspaceJournalReader reader, IEventStore journal,
        IEventCodecRegistry codecs, IArtifactStore artifacts, string workspaceId, string ownershipId)
    {
        foreach (var sessionEnvelope in reader.ReadEvents(EventType.Of("session.created")))
        {
            if (codecs.Decode(sessionEnvelope) is not SessionCreated session
                || !string.Equals(session.WorkspaceId, workspaceId, StringComparison.Ordinal)) continue;
            var events = journal.ReadFrom(session.SessionId, 1);
            var payloads = events.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
            foreach (var item in payloads.Where(item => item.Payload is LaneWorktreeCreated))
            {
                var created = (LaneWorktreeCreated)item.Payload!;
                if (!artifacts.Verify(created.TaskPacketRef.Hash, created.TaskPacketRef.Size)
                    || created.TaskPacketRef.Size is <= 0 or > LaneWorktreeProvisioner.MaximumTaskPacketBytes)
                    return true;
                var json = artifacts.GetText(created.TaskPacketRef.Hash);
                if (json is null || System.Text.Encoding.UTF8.GetByteCount(json) > LaneWorktreeProvisioner.MaximumTaskPacketBytes)
                    return true;
                TaskPacket? packet;
                try { packet = System.Text.Json.JsonSerializer.Deserialize(json, TaskPacketJsonContext.Default.TaskPacket); }
                catch (Exception) { return true; }
                if (packet?.WorkflowImplementation?.OwnershipId != ownershipId) continue;
                if (packet.LaneId != created.LaneId || packet.TaskId != created.TaskId
                    || packet.ExecutionId != created.ExecutionId || packet.SessionId != session.SessionId)
                    return true;
                var runEvents = payloads.Where(entry => entry.Envelope.RunId == packet.RunId).ToArray();
                if (!HasSingleTerminalFor(runEvents, packet.TaskId, packet.LaneId)) return true;
                if (runEvents.Count(entry => entry.Payload switch
                    {
                        LaneCompleted terminal => terminal.LaneId == packet.LaneId,
                        LaneFailed terminal => terminal.LaneId == packet.LaneId,
                        LaneCancelled terminal => terminal.LaneId == packet.LaneId,
                        _ => false,
                    }) != 1) return true;
            }
        }
        return false;
    }

    private static bool HasSingleTerminalFor(
        IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> events, TaskId task, LaneId lane) =>
        events.Count(item => item.Payload is TaskCompleted completed && completed.TaskId == task) == 1
        && events.Count(item => item.Payload is LaneCompleted completed && completed.LaneId == lane) == 1;

    private static bool IntegrationArgumentsMatch(string json, string ownershipId, string proposalId,
        IEnumerable<string> expectedPaths)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("ownershipId", out var owner) && owner.GetString() == ownershipId
                && root.TryGetProperty("proposalId", out var proposal) && proposal.GetString() == proposalId
                && root.TryGetProperty("paths", out var paths)
                && paths.ValueKind == System.Text.Json.JsonValueKind.Array
                && paths.EnumerateArray().All(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                && paths.EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(expectedPaths);
        }
        catch (Exception) { return false; }
    }

    private static bool IsOid(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record WorktreeCleanupCliOutcome(bool Removed, string? Error);
