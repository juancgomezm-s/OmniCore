namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Conservative startup recovery for old, closed or pre-journal lane worktrees. A
/// missing/ambiguous journal binding is never treated as proof that a recent or active lane is
/// disposable. Infrastructure cleanup independently refuses dirty, committed, or unsafe trees.</summary>
public static class WorktreeStartupRecoveryHost
{
    public static async Task<WorktreeStartupRecoveryOutcome> SweepAsync(IEventStore store,
        IEventCodecRegistry codecs, string workspacePath, IPlatformPaths paths, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(paths);
        if (store is not IWorkspaceJournalReader reader)
            return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.UnsupportedOwnershipMetadata);

        string root;
        string dataRoot;
        string workspaceId;
        try
        {
            root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath);
            var canonical = ProjectIdentity.CanonicalWorkspacePath(root);
            workspaceId = WorkspaceId.Of(canonical).ToString();
            dataRoot = OmniHost.WorkspaceDataDirectory(paths, root);
        }
        catch (Exception) { return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }

        var repository = await new GitWorktreeStore(SystemProcessRuntime.Instance())
            .InspectAsync(root, cancellationToken).ConfigureAwait(false);
        if (!repository.Succeeded)
            return WorktreeStartupRecoveryOutcome.Failure(repository.ErrorCode ?? GitWorktreeErrorCode.NoRepository);

        var lifecycle = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var journalBindings = new Dictionary<string, (LaneId Lane, string WorktreePath)>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var activeLanes = new HashSet<LaneId>();
        var failedProvisioningLaneTerminals = new Dictionary<LaneId, DateTimeOffset>();
        var ambiguousLanes = new HashSet<LaneId>();
        try
        {
            foreach (var createdEnvelope in reader.ReadEvents(EventType.Of("session.created")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (codecs.Decode(createdEnvelope) is not SessionCreated created
                    || !string.Equals(created.WorkspaceId, workspaceId, StringComparison.Ordinal)) continue;

                var events = store.ReadFrom(created.SessionId, 1);
                var roots = events.Select(codecs.Decode).OfType<WorkspaceRootEstablished>()
                    .Where(item => item.SessionId == created.SessionId).ToArray();
                if (roots.Length != 1 || !PathEquals(roots[0].CanonicalRoot, root)
                    || !WorkspaceRootIdentity.Verify(root, roots[0].DurableIdentity))
                    return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

                var payloads = events.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
                var laneIds = payloads.SelectMany(item => item.Payload switch
                    {
                        LaneCreated lane => new[] { lane.LaneId },
                        LaneProvisioning lane => new[] { lane.LaneId },
                        LaneStarted lane => new[] { lane.LaneId },
                        LaneBlocked lane => new[] { lane.LaneId },
                        LaneUnblocked lane => new[] { lane.LaneId },
                        _ => Array.Empty<LaneId>(),
                    }).Distinct().ToArray();
                var provisioningLanes = payloads.Where(item => item.Payload is LaneProvisioning)
                    .Select(item => ((LaneProvisioning)item.Payload).LaneId).ToHashSet();
                var startedLanes = payloads.Where(item => item.Payload is LaneStarted)
                    .Select(item => ((LaneStarted)item.Payload).LaneId).ToHashSet();
                var journalBoundLanes = payloads.Where(item => item.Payload is LaneWorktreeCreated)
                    .Select(item => ((LaneWorktreeCreated)item.Payload).LaneId).ToHashSet();
                foreach (var laneId in laneIds)
                {
                    var terminalEvents = payloads.Where(item => item.Payload switch
                        {
                            LaneCompleted completed => completed.LaneId == laneId,
                            LaneFailed failed => failed.LaneId == laneId,
                            LaneCancelled cancelled => cancelled.LaneId == laneId,
                            _ => false,
                        }).ToArray();
                    var isTerminal = terminalEvents.Length > 0;
                    if (!isTerminal) activeLanes.Add(laneId);
                    else if (terminalEvents.Length > 1) ambiguousLanes.Add(laneId);
                    else if (provisioningLanes.Contains(laneId) && !startedLanes.Contains(laneId)
                        && !journalBoundLanes.Contains(laneId)
                        && (terminalEvents[0].Payload is LaneFailed or LaneCancelled))
                        failedProvisioningLaneTerminals[laneId] = terminalEvents[0].Envelope.Timestamp;
                }
                foreach (var entry in payloads.Where(item => item.Payload is LaneWorktreeCreated))
                {
                    var binding = (LaneWorktreeCreated)entry.Payload;
                    if (!PathEquals(binding.RepositoryRoot, root)
                        || !PathEquals(binding.GitCommonDirectory, repository.Identity!.GitCommonDirectory))
                        return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

                    if (journalBindings.TryGetValue(binding.OwnershipId, out var priorBinding)
                        && (priorBinding.Lane != binding.LaneId || !PathEquals(priorBinding.WorktreePath, binding.WorktreePath)))
                        ambiguous.Add(binding.OwnershipId);
                    else journalBindings[binding.OwnershipId] = (binding.LaneId, binding.WorktreePath);

                    var terminal = payloads.Where(item => item.Payload switch
                        {
                            LaneCompleted completed => completed.LaneId == binding.LaneId,
                            LaneFailed failed => failed.LaneId == binding.LaneId,
                            LaneCancelled cancelled => cancelled.LaneId == binding.LaneId,
                            _ => false,
                        })
                        .Select(item => item.Envelope.Timestamp).ToArray();
                    if (terminal.Length > 1)
                    {
                        ambiguous.Add(binding.OwnershipId);
                        continue;
                    }

                    DateTimeOffset? closedAt = terminal.Length == 0 ? null : terminal[0];
                    if (lifecycle.TryGetValue(binding.OwnershipId, out var prior) && prior != closedAt)
                        ambiguous.Add(binding.OwnershipId);
                    else lifecycle[binding.OwnershipId] = closedAt;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.UnsupportedOwnershipMetadata); }

        return await SweepExpiredAsync(root, dataRoot, workspaceId, repository.Identity!.GitCommonDirectory,
            lifecycle, activeLanes, failedProvisioningLaneTerminals, ambiguousLanes, ambiguous, nowUtc,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WorktreeStartupRecoveryOutcome> SweepExpiredAsync(string repositoryRoot,
        string dataRoot, string workspaceId, string gitCommonDirectory,
        IReadOnlyDictionary<string, DateTimeOffset?> journalLifecycle, IReadOnlySet<LaneId> activeLanes,
        IReadOnlyDictionary<LaneId, DateTimeOffset> failedProvisioningLaneTerminals,
        IReadOnlySet<LaneId> ambiguousLanes, IReadOnlySet<string> ambiguousOwnership,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var inspected = await store.InspectAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!inspected.Succeeded || !PathEquals(inspected.Identity!.GitCommonDirectory, gitCommonDirectory))
            return WorktreeStartupRecoveryOutcome.Failure(inspected.ErrorCode ?? GitWorktreeErrorCode.OwnershipMismatch);

        string metadataDirectory;
        try { metadataDirectory = Path.Combine(Path.GetFullPath(dataRoot), "worktree-metadata"); }
        catch (Exception) { return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        if (!Directory.Exists(metadataDirectory)) return WorktreeStartupRecoveryOutcome.Empty;
        try
        {
            if (!PathEquals(inspected.Identity!.RepoRoot, Path.GetFullPath(repositoryRoot))
                || IsWithinOrEqual(repositoryRoot, dataRoot) || IsWithinOrEqual(dataRoot, repositoryRoot)
                || HasReparseAncestor(metadataDirectory) || !IsWithin(dataRoot, metadataDirectory))
                return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
        }
        catch (Exception) { return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.UnsafePath); }

        var removed = 0;
        var preserved = 0;
        var failed = 0;
        string[] files;
        try { files = Directory.GetFiles(metadataDirectory, "*.json", SearchOption.TopDirectoryOnly); }
        catch (Exception) { return WorktreeStartupRecoveryOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownershipId = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(ownershipId, "N", out _) || ambiguousOwnership.Contains(ownershipId))
            { preserved++; continue; }

            WorktreeIdentity? identity;
            try
            {
                var loaded = await store.LoadIdentityAsync(repositoryRoot, dataRoot, ownershipId, cancellationToken)
                    .ConfigureAwait(false);
                identity = loaded.Succeeded ? loaded.Worktree : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failed++; continue; }
            if (identity is null || !PathEquals(identity.GitCommonDirectory, gitCommonDirectory))
            { preserved++; continue; }

            // A terminal Lane is still durable provenance, and may be the source for a later
            // Verify or user-directed integration. Only metadata with no journal binding is an
            // orphan candidate; terminal worktrees are removed explicitly after their consumer
            // has recorded cleanup/disposition.
            if (journalLifecycle.ContainsKey(ownershipId) || activeLanes.Contains(identity.LaneId)
                || ambiguousLanes.Contains(identity.LaneId))
            { preserved++; continue; }

            // Metadata timestamps are mutable user data and cannot authorize deleting an
            // unjournaled directory. A pre-created worktree is eligible only when the durable
            // journal proves provisioning failed/cancelled at least one retention period ago.
            if (!failedProvisioningLaneTerminals.TryGetValue(identity.LaneId, out var laneTerminal))
            { preserved++; continue; }
            var eligibleAfter = laneTerminal.ToUniversalTime().Add(GitWorktreeStore.SnapshotReferenceRetention);
            if (nowUtc.ToUniversalTime() < eligibleAfter) { preserved++; continue; }

            var cleanup = await store.CleanupAsync(identity.Ownership, cancellationToken).ConfigureAwait(false);
            if (cleanup.Removed) removed++;
            else if (cleanup.Error == GitWorktreeErrorCode.WorktreeDirty) preserved++;
            else failed++;
        }

        var refs = await store.PruneExpiredSnapshotRefsAsync(repositoryRoot, dataRoot, workspaceId,
            nowUtc, dryRun: false, cancellationToken).ConfigureAwait(false);
        return new(removed, preserved, failed, refs.Pruned, refs.Preserved, refs.Failed, refs.Error);
    }

    private static bool PathEquals(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception) { return false; }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsWithinOrEqual(string root, string candidate) => PathEquals(root, candidate)
        || IsWithin(root, candidate);

    private static bool HasReparseAncestor(string path)
    {
        var full = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(full) ?? throw new ArgumentException("Invalid root path.", nameof(path));
        var current = volume;
        foreach (var part in full[volume.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}

public sealed record WorktreeStartupRecoveryOutcome(int WorktreesRemoved, int WorktreesPreserved,
    int Failed, int SnapshotRefsPruned, int SnapshotRefsPreserved, int SnapshotRefsFailed,
    GitWorktreeErrorCode? ErrorCode)
{
    public static WorktreeStartupRecoveryOutcome Empty { get; } = new(0, 0, 0, 0, 0, 0, null);
    public bool Succeeded => ErrorCode is null && Failed == 0 && SnapshotRefsFailed == 0;
    public static WorktreeStartupRecoveryOutcome Failure(GitWorktreeErrorCode error) =>
        new(0, 0, 1, 0, 0, 0, error);
}
