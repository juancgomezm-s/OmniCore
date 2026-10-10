using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private string? ResolveLaneWorktreeRootForRecovery(SessionId session, RunId run,
        LaneWorktreeCreated created)
    {
        try
        {
            if (created.LaneId.Value == Guid.Empty || string.IsNullOrWhiteSpace(created.OwnershipId)) return null;
            var verifiedRoot = VerifiedWorkspaceRoot(session);
            if (verifiedRoot is null) return null;
            var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(verifiedRoot);
            var workspaceDataRoot = OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), physicalRoot);
            var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
                .LoadIdentityAsync(physicalRoot, workspaceDataRoot, created.OwnershipId,
                    CancellationToken.None).GetAwaiter().GetResult();
            if (!loaded.Succeeded || loaded.Worktree is not { } identity
                || identity.LaneId != created.LaneId
                || !SamePhysicalPath(identity.RepositoryRoot, created.RepositoryRoot)
                || !SamePhysicalPath(identity.GitCommonDirectory, created.GitCommonDirectory)
                || !SamePhysicalPath(identity.WorktreePath, created.WorktreePath)
                || identity.SnapshotCommit != created.SnapshotCommit
                || identity.SnapshotTree != created.SnapshotTree)
                return null;
            return identity.WorktreePath;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
