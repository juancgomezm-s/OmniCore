namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Derives the data directory from the selected physical workspace and runs only the
/// bounded seven-day snapshot-ref sweep. It never accepts an ownership metadata path.</summary>
public static class WorktreeRetentionHost
{
    public static async Task<WorktreeRetentionSweepOutcome> SweepAsync(string workspacePath, bool dryRun,
        CancellationToken cancellationToken)
    {
        string root;
        try { root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath); }
        catch (Exception) { return new(0, 0, 0, 0, GitWorktreeErrorCode.InvalidPath); }
        var dataRoot = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), root);
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root)).ToString();
        return await new GitWorktreeStore(SystemProcessRuntime.Instance())
            .PruneExpiredSnapshotRefsAsync(root, dataRoot, workspaceId, DateTimeOffset.UtcNow, dryRun,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
