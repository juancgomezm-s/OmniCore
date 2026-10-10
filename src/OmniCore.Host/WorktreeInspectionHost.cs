namespace OmniCore.Host;

using OmniCore.Execution;
using OmniCore.Infrastructure;

/// <summary>Read-only client entry point for M7 repository identity. No provisioning authority.</summary>
public static class WorktreeInspectionHost
{
    public static async Task<WorktreeInspectionResult> InspectAsync(string repositoryPath,
        CancellationToken cancellationToken)
    {
        var store = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var inspected = await store.InspectAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        return inspected.Identity is { } identity && inspected.Succeeded
            ? new(true, identity.RepoRoot, identity.GitCommonDirectory, identity.HeadCommit, identity.Branch, null)
            : new(false, null, null, null, null, inspected.ErrorCode?.ToString() ?? "GitCommandFailed");
    }
}

/// <summary>Client-safe scalars; domain and Git implementation types stay in the Host.</summary>
public sealed record WorktreeInspectionResult(bool Succeeded, string? RepositoryRoot,
    string? GitCommonDirectory, string? HeadCommit, string? Branch, string? ErrorCode);
