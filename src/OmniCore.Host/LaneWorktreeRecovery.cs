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
                || !SamePhysicalPath(identity.RepositoryRoot, created.RepositoryRoot)
                || !SamePhysicalPath(identity.GitCommonDirectory, created.GitCommonDirectory)
                || !SamePhysicalPath(identity.WorktreePath, created.WorktreePath)
                || identity.SnapshotCommit != created.SnapshotCommit
                || identity.SnapshotTree != created.SnapshotTree)
                return null;
            if (identity.LaneId != created.LaneId) return null;
            return identity.WorktreePath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ResolveLaneWorktreeBoundRootForRecovery(SessionId session, RunId run,
        LaneWorktreeBound bound)
    {
        try
        {
            var verifiedRoot = VerifiedWorkspaceRoot(session);
            if (verifiedRoot is null) return null;
            var journal = AcquireStore().ReadFrom(session, 1).Where(evt => evt.RunId == run).ToArray();
            var stage = ReadWorkflowStageForTask(session, run, bound.TaskId);
            if (stage?.Definition.Stage != WorkflowStage.Verify || stage.Lane != bound.LaneId
                || !VerifyAcceptedImplementationBinding(stage, session, run, journal, CancellationToken.None))
                return null;
            var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(verifiedRoot);
            var dataRoot = OmniHost.WorkspaceDataDirectory(_platformPathsFactory(), physicalRoot);
            var loaded = new GitWorktreeStore(SystemProcessRuntime.Instance())
                .LoadIdentityAsync(physicalRoot, dataRoot, bound.OwnershipId, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!loaded.Succeeded || loaded.Worktree is not { } identity
                || identity.LaneId != bound.OwnerLaneId
                || identity.OwnershipId != bound.OwnershipId
                || !SamePhysicalPath(identity.RepositoryRoot, bound.RepositoryRoot)
                || !SamePhysicalPath(identity.GitCommonDirectory, bound.GitCommonDirectory)
                || !SamePhysicalPath(identity.WorktreePath, bound.WorktreePath)
                || identity.SnapshotCommit != bound.SnapshotCommit || identity.SnapshotTree != bound.SnapshotTree)
                return null;
            return identity.WorktreePath;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
