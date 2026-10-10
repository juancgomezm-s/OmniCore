namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Client entry point for a preview. Owns composition; exposes no apply authority.</summary>
public static class WorktreePreviewHost
{
    public static async System.Threading.Tasks.Task<WorktreePreviewResult> PreviewAsync(string repositoryPath,
        string ownershipId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(ownershipId, "N", out _))
            return WorktreePreviewResult.Failure("InvalidPath");
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var inspected = await store.InspectAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!inspected.Succeeded)
            return WorktreePreviewResult.Failure(inspected.ErrorCode?.ToString() ?? "GitCommandFailed");
        string root;
        string dataRoot;
        try
        {
            root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(inspected.Identity!.RepoRoot);
            var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root));
            // Resolve from user platform paths and Host identity, never from a client metadata path.
            // Avoid WorkspaceDirectory(): looking up missing ownership must not create that scope.
            dataRoot = Path.Combine(new DefaultPlatformPaths().DataDirectory, "workspaces", workspace.ToString());
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return WorktreePreviewResult.Failure("InvalidPath");
        }
        var loaded = await store.LoadIdentityAsync(root, dataRoot, ownershipId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Succeeded)
            return WorktreePreviewResult.Failure(loaded.Error?.ToString() ?? "OwnershipMismatch");
        var outcome = await store.PreviewIntegrationAsync(new(loaded.Worktree!), cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
            return WorktreePreviewResult.Failure(outcome.Error?.ToString() ?? "GitCommandFailed");
        var preview = outcome.Preview!;
        return new(true, null, preview.ProposalId, preview.WorkspaceHeadChanged, preview.WorkspaceBranchChanged,
            preview.Conflicts.Select(item => item.RelativePath).ToArray(),
            preview.Changes.Select(item => new WorktreePreviewChange(item.RelativePath,
                item.ExpectedPreSha256, item.ExpectedPostSha256, item.ExpectedPreMode, item.ExpectedPostMode)).ToArray());
    }
}

public sealed record WorktreePreviewChange(string Path, string? ExpectedPreSha256, string? ExpectedPostSha256,
    string? ExpectedPreMode, string? ExpectedPostMode);

public sealed record WorktreePreviewResult(bool Succeeded, string? ErrorCode, string? ProposalId,
    bool WorkspaceHeadChanged, bool WorkspaceBranchChanged, IReadOnlyList<string> Conflicts,
    IReadOnlyList<WorktreePreviewChange> Changes)
{
    public static WorktreePreviewResult Failure(string code) => new(false, code, null, false, false, [], []);
}
