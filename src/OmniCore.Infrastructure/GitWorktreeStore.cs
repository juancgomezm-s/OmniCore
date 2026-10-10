namespace OmniCore.Infrastructure;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Git snapshot/worktree persistence for M7 (ADR-0021). All Git calls use argv through
/// IProcessRuntime; the real index is never selected as GIT_INDEX_FILE for a mutating command.
/// Ownership records survive cancellation and failed provisioning. Automatic orphan adoption and
/// the seven-day ref expiry sweep are intentionally deferred; refs are retained past seven days
/// until a later explicit retention worker can verify ownership and age.
/// </summary>
public sealed class GitWorktreeStore
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private const int CapturedOutputLimit = 1_048_576;
    private readonly IProcessRuntime _processes;
    private readonly RedactionPolicy _redaction;

    public GitWorktreeStore(IProcessRuntime processes, RedactionPolicy? redaction = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _redaction = redaction ?? new RedactionPolicy();
    }

    /// <summary>Read-only repository inspection. IndexTree is null because Git's write-tree mutates its object database.</summary>
    public async Task<WorktreeInspectOutcome> InspectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.InvalidPath);
        string canonicalPath;
        try { canonicalPath = Canonical(repositoryPath); }
        catch (Exception) { return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        if (HasReparseDirectoryComponent(canonicalPath))
            return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
        if (!Directory.Exists(canonicalPath))
            return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.NoRepository);

        var root = await Git(canonicalPath, ["rev-parse", "--show-toplevel"], null, cancellationToken);
        if (!root.Success) return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.NoRepository);
        var common = await Git(root.Output.Trim(), ["rev-parse", "--path-format=absolute", "--git-common-dir"], null, cancellationToken);
        if (!common.Success) return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.NoRepository);
        var head = await Git(root.Output.Trim(), ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken);
        if (!head.Success) return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.MissingHead);
        var branch = await Git(root.Output.Trim(), ["symbolic-ref", "--quiet", "--short", "HEAD"], null, cancellationToken);
        try
        {
            return WorktreeInspectOutcome.Success(new WorktreeRepositoryIdentity(
                Canonical(root.Output.Trim()), Canonical(common.Output.Trim()), head.Output.Trim(),
                branch.Success ? branch.Output.Trim() : null));
        }
        catch (Exception) { return WorktreeInspectOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
    }

    public async Task<WorktreeCreateOutcome> CreateAsync(WorktreeCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkspaceId is null || request.LaneId is null)
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath);
        if (request.Base == WorktreeBase.PatchOverlay)
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedBase);
        if (request.Base is not (WorktreeBase.Head or WorktreeBase.SnapshotOfWorkingTree))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedBase);
        var ignoredPolicy = request.IgnoredFiles ?? IgnoredFilesPolicy.None;
        if (ignoredPolicy is not IgnoredFilesPolicy.NonePolicy)
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedIgnoredFilesPolicy);

        var inspected = await InspectAsync(request.RepositoryPath, cancellationToken);
        if (!inspected.Succeeded) return WorktreeCreateOutcome.Failure(inspected.ErrorCode!.Value);
        var repo = inspected.Identity!;
        string dataRoot;
        try { dataRoot = Canonical(request.WorkspaceDataRoot); }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        if (HasReparseDirectoryComponent(dataRoot))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
        if (IsWithinOrEqual(repo.RepoRoot, dataRoot) || IsWithinOrEqual(dataRoot, repo.RepoRoot))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.PathOutsideDataRoot);
        // WorkspaceId is derived only by the Host from its canonical workspace path (ADR-0038).
        // Infrastructure cannot reproduce Host's path-folding rules; validate the persisted
        // identity shape and bind it into ownership metadata instead of deriving it again here.
        if (!IsWorkspaceId(request.WorkspaceId.ToString()))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath);

        var files = await Git(repo.RepoRoot, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (!files.Success) return WorktreeCreateOutcome.Failure(files.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (files.Output.Length >= CapturedOutputLimit)
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);
        if (!IsCompleteNulList(files.Output))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);
        var workingPaths = SplitNul(files.Output);
        foreach (var path in workingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ls-files reports an untracked embedded repository as a directory ending in '/'.
            if (path.EndsWith('/') && IsSafeGitPath(path[..^1]))
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedSubmoduleState);
            if (!IsSafeGitPath(path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            if (_redaction.IsSecretPath(path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.SecretPathPresent);
            if (HasReparseComponent(repo.RepoRoot, path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
        }

        var headEntries = await Git(repo.RepoRoot, ["ls-tree", "-r", "-z", "--full-tree", repo.HeadCommit], null, cancellationToken);
        if (!headEntries.Success) return WorktreeCreateOutcome.Failure(headEntries.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (headEntries.Output.Length >= CapturedOutputLimit || !IsCompleteNulList(headEntries.Output))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);
        var headPaths = new List<string>();
        foreach (var entry in SplitNul(headEntries.Output))
        {
            var tab = entry.IndexOf('\t');
            if (tab <= 0 || tab == entry.Length - 1) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitCommandFailed);
            var mode = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var path = entry[(tab + 1)..];
            if (!IsSafeGitPath(path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            headPaths.Add(path);
            if (_redaction.IsSecretPath(path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.SecretPathPresent);
            if (mode == "120000") return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            if (mode == "160000") return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedSubmoduleState);
        }

        var unmerged = await Git(repo.RepoRoot, ["ls-files", "--unmerged", "-z"], null, cancellationToken);
        if (!unmerged.Success) return WorktreeCreateOutcome.Failure(unmerged.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (unmerged.Output.Length > 0) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.Conflict);

        var staged = await Git(repo.RepoRoot, ["ls-files", "--stage", "-z"], null, cancellationToken);
        if (!staged.Success) return WorktreeCreateOutcome.Failure(staged.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (SplitNul(staged.Output).Any(static entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedSubmoduleState);

        var filters = await CheckFilters(repo.RepoRoot, workingPaths, cancellationToken, null);
        if (!filters.Success) return WorktreeCreateOutcome.Failure(filters.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        filters = await CheckFilters(repo.RepoRoot, headPaths, cancellationToken, repo.HeadCommit);
        if (!filters.Success) return WorktreeCreateOutcome.Failure(filters.Error ?? GitWorktreeErrorCode.GitCommandFailed);

        var transforms = await CheckTransforms(repo.RepoRoot, workingPaths, cancellationToken, null);
        if (!transforms.Success) return WorktreeCreateOutcome.Failure(transforms.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        transforms = await CheckTransforms(repo.RepoRoot, headPaths, cancellationToken, repo.HeadCommit);
        if (!transforms.Success) return WorktreeCreateOutcome.Failure(transforms.Error ?? GitWorktreeErrorCode.GitCommandFailed);

        // Validate every future directory before creating the data root or any child. Existing
        // reparse points anywhere in the path would redirect writes outside the private data tree.
        var ownershipId = Guid.NewGuid().ToString("N");
        var laneFolder = Path.Combine(dataRoot, "worktrees", SafeSegment(request.LaneId.ToString()));
        var worktreePath = Path.Combine(laneFolder, ownershipId);
        var metadataDirectory = Path.Combine(dataRoot, "worktree-metadata");
        var metadataPath = Path.Combine(metadataDirectory, ownershipId + ".json");
        if (!IsWithinOrEqual(dataRoot, laneFolder) || !IsWithinOrEqual(dataRoot, metadataDirectory)
            || HasReparseDirectoryComponent(laneFolder) || HasReparseDirectoryComponent(metadataDirectory)
            || HasReparseDirectoryComponent(metadataPath)
            || Directory.Exists(worktreePath) || File.Exists(worktreePath))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);

        try
        {
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(laneFolder);
            Directory.CreateDirectory(metadataDirectory);
            if (HasReparseDirectoryComponent(dataRoot) || HasReparseDirectoryComponent(laneFolder)
                || HasReparseDirectoryComponent(metadataDirectory))
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
        }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }

        var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<string> pathsToHash = request.Base == WorktreeBase.SnapshotOfWorkingTree ? workingPaths : Array.Empty<string>();
        foreach (var path in pathsToHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(repo.RepoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            if (HasReparseComponent(repo.RepoRoot, path)) return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            try { sourceHashes.Add(path, await HashFile(fullPath, cancellationToken)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        }

        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshotRef = SnapshotRefFor(request.WorkspaceId.ToString(), request.LaneId.ToString(), snapshotId);
        var meta = new OwnershipMetadata(1, ownershipId, request.WorkspaceId.ToString(), request.LaneId.ToString(), snapshotId,
            dataRoot, repo.RepoRoot, repo.GitCommonDirectory, worktreePath, metadataPath, snapshotRef,
            null, DateTimeOffset.UtcNow, "creating");
        try { await PersistMetadata(dataRoot, metadataPath, meta, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }

        string snapshotCommit;
        string snapshotTree;
        string userIndexTree;
        try
        {
            if (HasReparseDirectoryComponent(dataRoot) || !IsWithinOrEqual(dataRoot, laneFolder)
                || !IsWithinOrEqual(dataRoot, metadataDirectory) || HasReparseDirectoryComponent(laneFolder)
                || HasReparseDirectoryComponent(metadataDirectory))
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            using var temp = new PrivateTempDirectory(dataRoot);
            var indexFile = Path.Combine(temp.Path, "snapshot.index");
            var currentHead = await Git(repo.RepoRoot, ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken);
            if (!currentHead.Success || !string.Equals(currentHead.Output.Trim(), repo.HeadCommit, StringComparison.OrdinalIgnoreCase))
                return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.Conflict);
            var headTree = await Git(repo.RepoRoot, ["rev-parse", "--verify", repo.HeadCommit + "^{tree}"], null, cancellationToken);
            if (!headTree.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
            var headTreeId = headTree.Output.Trim();
            if (request.Base == WorktreeBase.Head)
            {
                snapshotTree = headTreeId;
                var index = await CaptureIndexTree(repo.RepoRoot, temp.Path, cancellationToken);
                if (!index.Success) return await MarkFailed(dataRoot, metadataPath, index.Error ?? GitWorktreeErrorCode.GitCommandFailed);
                userIndexTree = index.Output;
                snapshotCommit = repo.HeadCommit;
            }
            else
            {
                var index = await CaptureIndexTree(repo.RepoRoot, temp.Path, cancellationToken);
                if (!index.Success) return await MarkFailed(dataRoot, metadataPath, index.Error ?? GitWorktreeErrorCode.GitCommandFailed);
                userIndexTree = index.Output;
                var env = IndexEnvironment(indexFile);
                var readTree = await Git(repo.RepoRoot, ["read-tree", repo.HeadCommit], env, cancellationToken);
                if (!readTree.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
                var add = await Git(repo.RepoRoot, ["add", "-A", "--", "."], env, cancellationToken);
                if (!add.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
                // Git may turn an untracked nested repository into a gitlink during add.
                // Inspect the candidate index before retaining a ref or materializing a tree.
                var candidate = await Git(repo.RepoRoot, ["ls-files", "--stage", "-z"], env, cancellationToken);
                if (!candidate.Success) return await MarkFailed(dataRoot, metadataPath, candidate.Error ?? GitWorktreeErrorCode.GitCommandFailed);
                if (!IsCompleteNulList(candidate.Output))
                    return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitOutputTruncated);
                if (SplitNul(candidate.Output).Any(static entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
                    return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.UnsupportedSubmoduleState);
                var tree = await Git(repo.RepoRoot, ["write-tree"], env, cancellationToken);
                if (!tree.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
                snapshotTree = tree.Output.Trim();
                var commit = await Git(repo.RepoRoot,
                    ["-c", "user.name=OmniCore snapshot", "-c", "user.email=omnicore@localhost", "commit-tree", snapshotTree, "-p", repo.HeadCommit, "-m", "OmniCore private workspace snapshot"], null, cancellationToken);
                if (!commit.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
                snapshotCommit = commit.Output.Trim();
            }

            var update = await Git(repo.RepoRoot,
                ["update-ref", "--no-deref", snapshotRef, snapshotCommit, new string('0', snapshotCommit.Length)], null, cancellationToken);
            if (!update.Success) return await MarkFailed(dataRoot, metadataPath, GitWorktreeErrorCode.GitCommandFailed);
            meta = meta with { SnapshotCommit = snapshotCommit, State = "snapshot" };
            await PersistMetadata(dataRoot, metadataPath, meta, cancellationToken);

            if (HasReparseDirectoryComponent(dataRoot) || HasReparseDirectoryComponent(laneFolder)
                || HasReparseDirectoryComponent(metadataDirectory) || !IsWithinOrEqual(dataRoot, worktreePath))
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
            var addWorktree = await Git(repo.RepoRoot, ["worktree", "add", "--detach", "--", worktreePath, snapshotCommit], null, cancellationToken);
            if (!addWorktree.Success)
            {
                await PersistMetadata(dataRoot, metadataPath, meta with { State = "failed" }, cancellationToken);
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitCommandFailed);
            }
            if (!PathIsOwnedDirectory(dataRoot, worktreePath))
                return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

            meta = meta with { State = "active" };
            await PersistMetadata(dataRoot, metadataPath, meta, cancellationToken);
            foreach (var (path, expectedHash) in sourceHashes)
            {
                var materializedPath = Path.Combine(worktreePath, path.Replace('/', Path.DirectorySeparatorChar));
                if (HasReparseComponent(worktreePath, path) || !File.Exists(materializedPath)
                    || !string.Equals(expectedHash, await HashFile(materializedPath, cancellationToken), StringComparison.Ordinal))
                    return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
            }
            return WorktreeCreateOutcome.Success(new WorktreeIdentity(request.WorkspaceId, request.LaneId,
                repo.RepoRoot, repo.GitCommonDirectory, repo.HeadCommit, repo.Branch, snapshotCommit, snapshotTree,
                userIndexTree, worktreePath, snapshotRef, ownershipId, metadataPath, dataRoot, meta.CreatedAtUtc));
        }
        catch (OperationCanceledException)
        {
            // Keep metadata and any private ref for deterministic recovery; never delete user data on cancellation.
            throw;
        }
        catch (Exception)
        {
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.GitCommandFailed);
        }
    }

    public async Task<WorktreeCleanupOutcome> CleanupAsync(WorktreeOwnership ownership, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        try
        {
            if (HasReparseDirectoryComponent(ownership.MetadataPath))
                return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        }
        catch (Exception) { return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }
        if (!File.Exists(ownership.MetadataPath)) return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        OwnershipMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(ownership.MetadataPath, cancellationToken), GitWorktreeJsonContext.Default.OwnershipMetadata);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.OwnershipId) || string.IsNullOrWhiteSpace(metadata.WorkspaceId)
            || string.IsNullOrWhiteSpace(metadata.LaneId) || string.IsNullOrWhiteSpace(metadata.SnapshotId)
            || string.IsNullOrWhiteSpace(metadata.DataRoot) || string.IsNullOrWhiteSpace(metadata.RepositoryRoot)
            || string.IsNullOrWhiteSpace(metadata.GitCommonDirectory) || string.IsNullOrWhiteSpace(metadata.WorktreePath)
            || string.IsNullOrWhiteSpace(metadata.MetadataPath) || string.IsNullOrWhiteSpace(metadata.SnapshotRef)
            || metadata.Version != 1 || metadata.State != "active"
            || metadata.OwnershipId != ownership.OwnershipId
            || !Guid.TryParseExact(metadata.OwnershipId, "N", out _)
            || !IsWorkspaceId(metadata.WorkspaceId)
            || metadata.WorkspaceId != ownership.WorkspaceId || metadata.LaneId != ownership.LaneId
            || !PathEquals(metadata.DataRoot, ownership.DataRoot)
            || !PathEquals(metadata.RepositoryRoot, ownership.RepositoryRoot)
            || !PathEquals(metadata.GitCommonDirectory, ownership.GitCommonDirectory)
            || !PathEquals(metadata.WorktreePath, ownership.WorktreePath)
            || metadata.SnapshotRef != ownership.SnapshotRef || metadata.SnapshotCommit != ownership.SnapshotCommit
            || metadata.SnapshotCommit is null
            || !Guid.TryParseExact(metadata.SnapshotId, "N", out _)
            || metadata.SnapshotRef != SnapshotRefFor(metadata.WorkspaceId, metadata.LaneId, metadata.SnapshotId)
            || !PathEquals(metadata.MetadataPath, ownership.MetadataPath)
            || !PathEquals(metadata.MetadataPath, Path.Combine(metadata.DataRoot, "worktree-metadata", metadata.OwnershipId + ".json"))
            || !PathEquals(metadata.WorktreePath, Path.Combine(metadata.DataRoot, "worktrees", SafeSegment(metadata.LaneId), metadata.OwnershipId))
            || !PathIsOwnedDirectory(metadata.DataRoot, metadata.WorktreePath))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        var repository = await InspectAsync(metadata.RepositoryRoot, cancellationToken);
        var worktree = await InspectAsync(metadata.WorktreePath, cancellationToken);
        if (!repository.Succeeded || !worktree.Succeeded
            || !PathEquals(repository.Identity!.RepoRoot, metadata.RepositoryRoot)
            || !PathEquals(repository.Identity!.GitCommonDirectory, metadata.GitCommonDirectory)
            || !PathEquals(worktree.Identity!.RepoRoot, metadata.WorktreePath)
            || !PathEquals(worktree.Identity!.GitCommonDirectory, metadata.GitCommonDirectory))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        var referencedSnapshot = await Git(metadata.RepositoryRoot,
            ["rev-parse", "--verify", metadata.SnapshotRef + "^{commit}"], null, cancellationToken);
        if (!referencedSnapshot.Success || !string.Equals(referencedSnapshot.Output.Trim(), metadata.SnapshotCommit, StringComparison.OrdinalIgnoreCase))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        // A clean index can still contain new lane commits. Preserve them until integration or
        // an explicit discard has retained their history; this checkpoint has neither operation.
        if (worktree.Identity!.Branch is not null
            || !string.Equals(worktree.Identity.HeadCommit, metadata.SnapshotCommit, StringComparison.OrdinalIgnoreCase))
            return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.WorktreeDirty);

        var status = await Git(metadata.WorktreePath, ["status", "--porcelain", "--untracked-files=all", "--ignored=matching"], null, cancellationToken);
        if (!status.Success) return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        if (status.Output.Length != 0) return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.WorktreeDirty);
        var remove = await Git(metadata.RepositoryRoot, ["worktree", "remove", "--", metadata.WorktreePath], null, cancellationToken);
        if (!remove.Success) return WorktreeCleanupOutcome.Failure(GitWorktreeErrorCode.WorktreeDirty);
        // Snapshot refs are retained for the ADR-0021 seven-day inspection window.
        await PersistMetadata(metadata.DataRoot, ownership.MetadataPath, metadata with { State = "removed" }, cancellationToken);
        return WorktreeCleanupOutcome.Success();
    }

    private async Task<GitResult> CaptureIndexTree(string root, string tempDirectory, CancellationToken cancellationToken)
    {
        var indexPath = await Git(root, ["rev-parse", "--path-format=absolute", "--git-path", "index"], null, cancellationToken);
        if (!indexPath.Success) return GitResult.Failure(indexPath.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var source = indexPath.Output.Trim();
        if (!File.Exists(source)) return GitResult.Failure(GitWorktreeErrorCode.UnsupportedIndex);
        var copy = Path.Combine(tempDirectory, "user-index.copy");
        try { File.Copy(source, copy, true); }
        catch (Exception) { return GitResult.Failure(GitWorktreeErrorCode.UnsupportedIndex); }
        var tree = await Git(root, ["write-tree"], IndexEnvironment(copy), cancellationToken);
        return tree.Success ? GitResult.Ok(tree.Output.Trim()) : GitResult.Failure(tree.Error ?? GitWorktreeErrorCode.GitCommandFailed);
    }

    private async Task<GitResult> CheckFilters(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken,
        string? sourceTree)
    {
        for (var offset = 0; offset < paths.Count; offset += 96)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = paths.Skip(offset).Take(96).ToArray();
            var args = new List<string> { "check-attr" };
            if (sourceTree is not null) args.Add("--source=" + sourceTree);
            args.AddRange(["-z", "filter", "--", .. chunk]);
            var result = await Git(root, args, null, cancellationToken);
            if (!result.Success)
            {
                var failure = result.Error ?? GitWorktreeErrorCode.GitCommandFailed;
                if (sourceTree is not null && failure == GitWorktreeErrorCode.GitCommandFailed)
                    failure = GitWorktreeErrorCode.UnsupportedFilter;
                return GitResult.Failure(failure);
            }
            if (result.Output.Length >= CapturedOutputLimit || !IsCompleteNulList(result.Output))
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            var parts = SplitNul(result.Output);
            if (parts.Count != chunk.Length * 3)
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            for (var i = 2; i < parts.Count; i += 3)
            if (parts[i] is not ("unspecified" or "unset")) return GitResult.Failure(GitWorktreeErrorCode.UnsupportedFilter);
        }
        return GitResult.Ok("");
    }

    private async Task<GitResult> CheckTransforms(string root, IReadOnlyList<string> paths,
        CancellationToken cancellationToken, string? sourceTree)
    {
        string[] attributes = ["text", "eol", "working-tree-encoding", "ident"];
        for (var offset = 0; offset < paths.Count; offset += 96)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = paths.Skip(offset).Take(96).ToArray();
            var args = new List<string> { "check-attr" };
            if (sourceTree is not null) args.Add("--source=" + sourceTree);
            args.AddRange(["-z", .. attributes, "--", .. chunk]);
            var result = await Git(root, args, null, cancellationToken);
            if (!result.Success)
            {
                var failure = result.Error ?? GitWorktreeErrorCode.GitCommandFailed;
                if (sourceTree is not null && failure == GitWorktreeErrorCode.GitCommandFailed)
                    failure = GitWorktreeErrorCode.UnsupportedTransform;
                return GitResult.Failure(failure);
            }
            if (result.Output.Length >= CapturedOutputLimit || !IsCompleteNulList(result.Output))
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            var parts = SplitNul(result.Output);
            if (parts.Count != chunk.Length * attributes.Length * 3)
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            for (var i = 2; i < parts.Count; i += 3)
                if (parts[i] is not ("unspecified" or "unset"))
                    return GitResult.Failure(GitWorktreeErrorCode.UnsupportedTransform);
        }
        return GitResult.Ok("");
    }

    private async Task<GitResult> Git(string workingDirectory, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var disabledHooksPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var args = new List<string>
        {
            "--no-optional-locks",
            "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=" + disabledHooksPath,
            "-c", "core.autocrlf=false",
            "-c", "core.eol=lf",
            "-c", "core.safecrlf=false",
            "-c", "commit.gpgSign=false",
        };
        args.AddRange(command);
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0" };
        if (environment is not null) foreach (var pair in environment) env[pair.Key] = pair.Value;
        try
        {
            var handle = _processes.Launch(new ProcessLaunch("git", args, workingDirectory, env, true), cancellationToken);
            using var registration = cancellationToken.Register(() => CancelTree(handle));
            ProcessResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(
                    () => _processes.Wait(handle, GitTimeout, cancellationToken), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancelTree(handle);
                throw;
            }
            catch (Exception)
            {
                CancelTree(handle);
                return GitResult.Failure(GitWorktreeErrorCode.GitCommandFailed);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                CancelTree(handle);
                throw new OperationCanceledException(cancellationToken);
            }
            if (result.TimedOut)
            {
                CancelTree(handle);
                return GitResult.Failure(GitWorktreeErrorCode.GitCommandFailed);
            }
            if (result.ExitCode != 0) return GitResult.Failure(GitWorktreeErrorCode.GitCommandFailed);
            var output = result.Stdout ?? "";
            if (output.Length >= CapturedOutputLimit)
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            return GitResult.Ok(output);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return GitResult.Failure(GitWorktreeErrorCode.GitCommandFailed); }
    }

    private void CancelTree(ProcessHandle handle)
    {
        try { _processes.CancelTree(handle); }
        catch (Exception) { }
    }

    private async Task<WorktreeCreateOutcome> MarkFailed(string dataRoot, string metadataPath, GitWorktreeErrorCode error)
    {
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metadataPath);
                var metadata = JsonSerializer.Deserialize(json, GitWorktreeJsonContext.Default.OwnershipMetadata);
                if (metadata is not null) await PersistMetadata(dataRoot, metadataPath, metadata with { State = "failed" }, CancellationToken.None);
            }
            catch (Exception) { }
        }
        return WorktreeCreateOutcome.Failure(error);
    }

    private static async Task PersistMetadata(string dataRoot, string path, OwnershipMetadata metadata, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Canonical(path));
        if (parent is null || !IsWithinOrEqual(dataRoot, path) || !IsWithinOrEqual(dataRoot, parent)
            || HasReparseDirectoryComponent(dataRoot) || HasReparseDirectoryComponent(parent) || HasReparseDirectoryComponent(path))
            throw new IOException("Unsafe metadata path.");
        var json = JsonSerializer.Serialize(metadata, GitWorktreeJsonContext.Default.OwnershipMetadata);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        if (HasReparseDirectoryComponent(temp)) throw new IOException("Unsafe metadata temporary path.");
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(json.AsMemory(), cancellationToken);
        if (HasReparseDirectoryComponent(dataRoot) || HasReparseDirectoryComponent(parent) || HasReparseDirectoryComponent(path))
            throw new IOException("Metadata path changed during write.");
        File.Move(temp, path, true);
    }

    private static IReadOnlyDictionary<string, string> IndexEnvironment(string path) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = path };

    private static List<string> SplitNul(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool IsCompleteNulList(string text) => text.Length == 0 || text[^1] == '\0';

    private static async Task<string> HashFile(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool HasReparseComponent(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (Exception) { return true; }
        }
        return false;
    }

    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsWorkspaceId(string value) => value.Length == 16 && value.All(Uri.IsHexDigit);
    private static bool IsSafeGitPath(string path) => !string.IsNullOrEmpty(path)
        && !Path.IsPathRooted(path) && path.IndexOf('\0') < 0
        && path.Replace('\\', '/').Split('/').All(static part => part.Length > 0 && part is not ("." or ".."));
    private static string SafeSegment(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private static string SnapshotRefFor(string workspaceId, string laneId, string snapshotId) =>
        $"refs/omnicore/snapshots/{SafeSegment(workspaceId)}/{SafeSegment(laneId)}/{snapshotId}";
    private static bool IsWithinOrEqual(string parent, string child) => PathEquals(parent, child) || IsWithin(parent, child);
    private static bool IsWithin(string parent, string child) => Path.GetRelativePath(parent, child) is var rel && rel != "."
        && !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static bool PathEquals(string a, string b) => string.Equals(Canonical(a), Canonical(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathIsOwnedDirectory(string dataRoot, string path)
    {
        try
        {
            var root = Canonical(dataRoot);
            var full = Canonical(path);
            if (!IsWithin(root, full) || !Directory.Exists(full) || HasReparseDirectoryComponent(full)) return false;
            var info = new DirectoryInfo(full);
            return (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception) { return false; }
    }

    private static bool HasReparseDirectoryComponent(string path)
    {
        try
        {
            var current = Path.GetPathRoot(path)!;
            foreach (var part in Path.GetFullPath(path)[Path.GetPathRoot(path)!.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((Directory.Exists(current) || File.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch (Exception) { return true; }
    }

    private sealed record GitResult(bool Success, string Output, GitWorktreeErrorCode? Error)
    {
        public static GitResult Ok(string output) => new(true, output, null);
        public static GitResult Failure(GitWorktreeErrorCode error) => new(false, "", error);
    }

    internal sealed record OwnershipMetadata(int Version, string OwnershipId, string WorkspaceId, string LaneId,
        string SnapshotId, string DataRoot, string RepositoryRoot, string GitCommonDirectory, string WorktreePath, string MetadataPath,
        string SnapshotRef, string? SnapshotCommit, DateTimeOffset CreatedAtUtc, string State);

    private sealed class PrivateTempDirectory : IDisposable
    {
        public string Path { get; }
        public PrivateTempDirectory(string parent)
        {
            Path = System.IO.Path.Combine(parent, ".m7-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { } }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitWorktreeStore.OwnershipMetadata))]
internal partial class GitWorktreeJsonContext : JsonSerializerContext { }
