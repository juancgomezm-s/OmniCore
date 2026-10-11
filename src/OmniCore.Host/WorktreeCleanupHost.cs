namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Authoritative explicit cleanup composition. The caller supplies only an ownership
/// token; repository and workspace data roots are derived from the selected physical workspace.</summary>
public static class WorktreeCleanupHost
{
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
        var loaded = await store.LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.Succeeded || loaded.Worktree is not { } identity)
            return WorktreeCleanupOutcome.Failure(loaded.Error ?? GitWorktreeErrorCode.OwnershipMismatch);
        return await store.CleanupAsync(identity.Ownership, cancellationToken).ConfigureAwait(false);
    }
}
