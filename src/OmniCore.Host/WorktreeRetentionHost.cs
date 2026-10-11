namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Derives the data directory from the selected physical workspace and runs only the
/// bounded seven-day snapshot-ref sweep. It never accepts an ownership metadata path.</summary>
public static class WorktreeRetentionHost
{
    /// <summary>Scalar projection for the CLI boundary; Domain error enums stay inside Host.</summary>
    public static async Task<WorktreeRetentionCliOutcome> SweepForCliAsync(string workspacePath, bool dryRun,
        CancellationToken cancellationToken)
    {
        var result = await SweepAsync(workspacePath, dryRun, cancellationToken).ConfigureAwait(false);
        return new(result.Eligible, result.Pruned, result.Preserved, result.Failed, result.Error?.ToString());
    }

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

public sealed record WorktreeRetentionCliOutcome(int Eligible, int Pruned, int Preserved, int Failed,
    string? Error);
