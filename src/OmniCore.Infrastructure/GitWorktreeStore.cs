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
    private const int MaxIntegrationFiles = 64;
    private const long MaxIntegrationImageBytes = 8L * 1024 * 1024;
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
        var meta = new OwnershipMetadata(2, ownershipId, request.WorkspaceId.ToString(), request.LaneId.ToString(), snapshotId,
            dataRoot, repo.RepoRoot, repo.GitCommonDirectory, worktreePath, metadataPath, snapshotRef,
            request.Base.ToString(), repo.HeadCommit, repo.Branch, null, null, null, DateTimeOffset.UtcNow, "creating");
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
            meta = meta with { SnapshotCommit = snapshotCommit, SnapshotTree = snapshotTree, UserIndexTree = userIndexTree, State = "snapshot" };
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
                userIndexTree, worktreePath, snapshotRef, ownershipId, metadataPath, dataRoot, meta.CreatedAtUtc)
                { Base = request.Base });
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

    /// <summary>Loads only a complete version-2 ownership record whose paths and Git identities agree.</summary>
    public async Task<WorktreeCreateOutcome> LoadIdentityAsync(string repositoryRoot, string expectedDataRoot,
        string ownershipId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(ownershipId, "N", out _))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        string repoPath;
        string dataRoot;
        try { repoPath = Canonical(repositoryRoot); dataRoot = Canonical(expectedDataRoot); }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        if (HasReparseDirectoryComponent(repoPath) || HasReparseDirectoryComponent(dataRoot)
            || IsWithinOrEqual(repoPath, dataRoot) || IsWithinOrEqual(dataRoot, repoPath))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsafePath);

        var metadataPath = Path.Combine(dataRoot, "worktree-metadata", ownershipId + ".json");
        if (HasReparseDirectoryComponent(metadataPath) || !File.Exists(metadataPath))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        OwnershipMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(metadataPath, cancellationToken),
                GitWorktreeJsonContext.Default.OwnershipMetadata);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }
        if (metadata is null || metadata.Version != 2)
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        WorktreeBase baseKind;
        try
        {
        if (string.IsNullOrWhiteSpace(metadata.State) || string.IsNullOrWhiteSpace(metadata.OwnershipId)
            || string.IsNullOrWhiteSpace(metadata.WorkspaceId) || string.IsNullOrWhiteSpace(metadata.LaneId)
            || string.IsNullOrWhiteSpace(metadata.SnapshotId) || string.IsNullOrWhiteSpace(metadata.Base)
            || string.IsNullOrWhiteSpace(metadata.DataRoot) || string.IsNullOrWhiteSpace(metadata.RepositoryRoot)
            || string.IsNullOrWhiteSpace(metadata.GitCommonDirectory) || string.IsNullOrWhiteSpace(metadata.WorktreePath)
            || string.IsNullOrWhiteSpace(metadata.MetadataPath) || string.IsNullOrWhiteSpace(metadata.SnapshotRef)
            || string.IsNullOrWhiteSpace(metadata.Head) || string.IsNullOrWhiteSpace(metadata.SnapshotCommit)
            || string.IsNullOrWhiteSpace(metadata.SnapshotTree) || string.IsNullOrWhiteSpace(metadata.UserIndexTree)
            || metadata.State != "active" || metadata.OwnershipId != ownershipId
            || !IsWorkspaceId(metadata.WorkspaceId) || !Guid.TryParseExact(metadata.LaneId, "D", out _)
            || !Guid.TryParseExact(metadata.SnapshotId, "N", out _)
            || !(metadata.Base == nameof(WorktreeBase.Head) || metadata.Base == nameof(WorktreeBase.SnapshotOfWorkingTree))
            || !Enum.TryParse(metadata.Base, ignoreCase: false, out baseKind)
            || !IsOid(metadata.Head) || (metadata.Branch is not null && string.IsNullOrWhiteSpace(metadata.Branch))
            || !IsOid(metadata.SnapshotTree) || !IsOid(metadata.UserIndexTree) || !IsOid(metadata.SnapshotCommit)
            || !PathEquals(metadata.DataRoot, dataRoot) || !PathEquals(metadata.RepositoryRoot, repoPath)
            || !PathEquals(metadata.MetadataPath, metadataPath)
            || !PathEquals(metadata.WorktreePath, Path.Combine(dataRoot, "worktrees", SafeSegment(metadata.LaneId), ownershipId))
            || metadata.SnapshotRef != SnapshotRefFor(metadata.WorkspaceId, metadata.LaneId, metadata.SnapshotId)
            || !PathIsOwnedDirectory(dataRoot, metadata.WorktreePath))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        }
        catch (Exception) { return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }

        var repository = await InspectAsync(repoPath, cancellationToken);
        var lane = await InspectAsync(metadata.WorktreePath, cancellationToken);
        if (!repository.Succeeded || !lane.Succeeded
            || !PathEquals(repository.Identity!.RepoRoot, repoPath)
            || !PathEquals(repository.Identity.GitCommonDirectory, metadata.GitCommonDirectory)
            || !PathEquals(repository.Identity.GitCommonDirectory, lane.Identity!.GitCommonDirectory)
            || !PathEquals(lane.Identity.RepoRoot, metadata.WorktreePath))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        var snapshotRef = await Git(repoPath, ["rev-parse", "--verify", metadata.SnapshotRef + "^{commit}"], null, cancellationToken);
        if (!snapshotRef.Success || !string.Equals(snapshotRef.Output.Trim(), metadata.SnapshotCommit, StringComparison.OrdinalIgnoreCase))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        var snapshotTree = await Git(repoPath, ["rev-parse", "--verify", metadata.SnapshotCommit + "^{tree}"], null, cancellationToken);
        if (!snapshotTree.Success || !string.Equals(snapshotTree.Output.Trim(), metadata.SnapshotTree, StringComparison.OrdinalIgnoreCase))
            return WorktreeCreateOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        return WorktreeCreateOutcome.Success(new WorktreeIdentity(WorkspaceId.Parse(metadata.WorkspaceId),
            LaneId.Parse(metadata.LaneId), repoPath, metadata.GitCommonDirectory, metadata.Head, metadata.Branch,
            metadata.SnapshotCommit, metadata.SnapshotTree, metadata.UserIndexTree, metadata.WorktreePath,
            metadata.SnapshotRef, metadata.OwnershipId, metadata.MetadataPath, metadata.DataRoot, metadata.CreatedAtUtc)
            { Base = baseKind });
    }

    /// <summary>
    /// Builds a read-only 3-way proposal: S=base, current workspace=ours, current lane=theirs.
    /// All new Git objects and indexes are private temporary data outside the repository.
    /// </summary>
    public async Task<WorktreeIntegrationPreviewOutcome> PreviewIntegrationAsync(
        WorktreeIntegrationPreviewRequest request, CancellationToken cancellationToken)
    {
        try { return await PreviewIntegrationCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        catch (UnauthorizedAccessException) { return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        catch (ArgumentException) { return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
    }

    private async Task<WorktreeIntegrationPreviewOutcome> PreviewIntegrationCoreAsync(
        WorktreeIntegrationPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Worktree;
        if (source is null) return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        var loaded = await LoadIdentityAsync(source.RepositoryRoot, source.WorkspaceDataRoot, source.OwnershipId, cancellationToken);
        if (!loaded.Succeeded) return WorktreeIntegrationPreviewOutcome.Failure(loaded.Error!.Value);
        var owned = loaded.Worktree!;
        try
        {
            if (!IdentityEquals(source, owned)) return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        }
        catch (Exception) { return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch); }

        var workspace = await InspectAsync(owned.RepositoryRoot, cancellationToken);
        var lane = await InspectAsync(owned.WorktreePath, cancellationToken);
        if (!workspace.Succeeded || !lane.Succeeded
            || !PathEquals(workspace.Identity!.RepoRoot, owned.RepositoryRoot)
            || !PathEquals(workspace.Identity.GitCommonDirectory, owned.GitCommonDirectory)
            || !PathEquals(lane.Identity!.RepoRoot, owned.WorktreePath)
            || !PathEquals(lane.Identity.GitCommonDirectory, owned.GitCommonDirectory))
            return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        PrivateTempDirectory temp;
        try { temp = new PrivateTempDirectory(owned.WorkspaceDataRoot); }
        catch (Exception) { return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        using (temp)
        {
        var privateObjects = await CreatePrivateObjectEnvironment(owned.RepositoryRoot, temp.Path, cancellationToken);
        if (!privateObjects.Success) return WorktreeIntegrationPreviewOutcome.Failure(privateObjects.Error!.Value);
        var objectEnv = privateObjects.Environment!;
        var userCapture = await CaptureCurrentTree(owned.RepositoryRoot, workspace.Identity.HeadCommit, temp.Path,
            "ours.index", objectEnv, cancellationToken);
        if (!userCapture.Success) return WorktreeIntegrationPreviewOutcome.Failure(userCapture.Error!.Value);
        var laneCapture = await CaptureCurrentTree(owned.WorktreePath, lane.Identity.HeadCommit, temp.Path,
            "theirs.index", objectEnv, cancellationToken);
        if (!laneCapture.Success) return WorktreeIntegrationPreviewOutcome.Failure(laneCapture.Error!.Value);

        var baseCommit = owned.SnapshotCommit;
        var oursCommit = userCapture.Commit!;
        var theirsCommit = laneCapture.Commit!;
        var merge = await Git(owned.RepositoryRoot,
            ["merge-tree", "--write-tree", "--name-only", "--no-messages", "-z", "--merge-base=" + baseCommit, oursCommit, theirsCommit],
            objectEnv, cancellationToken, allowConflictExit: true);
        if (merge.Error is not null && merge.ExitCode != 1)
            return WorktreeIntegrationPreviewOutcome.Failure(merge.Error.Value);
        if (!TryParseMergeTreeOutput(merge.Output, merge.ExitCode, out var mergeTree, out var conflictPaths))
            return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);

        var changes = new List<WorktreeIntegrationChange>();
        if (conflictPaths.Count == 0)
        {
            var oursTree = await Git(owned.RepositoryRoot, ["rev-parse", "--verify", oursCommit + "^{tree}"], objectEnv, cancellationToken);
            if (!oursTree.Success) return WorktreeIntegrationPreviewOutcome.Failure(oursTree.Error!.Value);
            var oursEntries = await ReadTreeEntries(owned.RepositoryRoot, oursTree.Output.Trim(), objectEnv, cancellationToken);
            if (!oursEntries.Success || oursEntries.Entries is null)
                return WorktreeIntegrationPreviewOutcome.Failure(oursEntries.Error ?? GitWorktreeErrorCode.GitCommandFailed);
            var mergedEntries = await ReadTreeEntries(owned.RepositoryRoot, mergeTree!, objectEnv, cancellationToken);
            if (!mergedEntries.Success || mergedEntries.Entries is null)
                return WorktreeIntegrationPreviewOutcome.Failure(mergedEntries.Error ?? GitWorktreeErrorCode.GitCommandFailed);
            var materialized = await MaterializeAndHashTree(owned.RepositoryRoot, mergeTree!, temp.Path, objectEnv, "merged", cancellationToken);
            if (!materialized.Success) return WorktreeIntegrationPreviewOutcome.Failure(materialized.Error!.Value);
            var diff = await Git(owned.RepositoryRoot,
                ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--no-renames", oursTree.Output.Trim(), mergeTree!],
                objectEnv, cancellationToken);
            if (!diff.Success) return WorktreeIntegrationPreviewOutcome.Failure(diff.Error!.Value);
            if (!TryParseNameStatus(diff.Output, out var changed))
                return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            foreach (var path in changed)
            {
                if (!IsSafeGitPath(path) || _redaction.IsSecretPath(path))
                    return WorktreeIntegrationPreviewOutcome.Failure(_redaction.IsSecretPath(path)
                        ? GitWorktreeErrorCode.SecretPathPresent : GitWorktreeErrorCode.UnsafePath);
                var pre = oursEntries.Entries.GetValueOrDefault(path);
                var post = mergedEntries.Entries.GetValueOrDefault(path);
                if ((pre is not null && pre.Mode is not ("100644" or "100755"))
                    || (post is not null && post.Mode is not ("100644" or "100755")))
                    return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
                var preHash = userCapture.Hashes.GetValueOrDefault(path);
                var postHash = post is null ? null : materialized.Hashes!.GetValueOrDefault(path);
                if (post is not null && postHash is null)
                    return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                changes.Add(new WorktreeIntegrationChange(path, preHash, postHash, pre?.Mode, post?.Mode));
            }
        }

        var finalWorkspace = await InspectAsync(owned.RepositoryRoot, cancellationToken);
        var finalLane = await InspectAsync(owned.WorktreePath, cancellationToken);
        if (!finalWorkspace.Succeeded || !finalLane.Succeeded
            || !string.Equals(finalWorkspace.Identity!.HeadCommit, workspace.Identity.HeadCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalWorkspace.Identity.Branch, workspace.Identity.Branch, StringComparison.Ordinal)
            || !string.Equals(finalLane.Identity!.HeadCommit, lane.Identity.HeadCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalLane.Identity.Branch, lane.Identity.Branch, StringComparison.Ordinal)
            || !(await VerifyCurrentCapture(owned.RepositoryRoot, userCapture, cancellationToken))
            || !(await VerifyCurrentCapture(owned.WorktreePath, laneCapture, cancellationToken)))
            return WorktreeIntegrationPreviewOutcome.Failure(GitWorktreeErrorCode.WorkspaceChanged);

        var proposalId = ComputeProposalId(owned, workspace.Identity, lane.Identity, userCapture.Tree!, laneCapture.Tree!,
            mergeTree, changes);
        return WorktreeIntegrationPreviewOutcome.Success(new WorktreeIntegrationPreview(
            proposalId, owned.RepositoryRoot, owned.GitCommonDirectory, baseCommit, oursCommit, theirsCommit,
            mergeTree, workspace.Identity.HeadCommit, workspace.Identity.Branch,
            !string.Equals(workspace.Identity.HeadCommit, owned.Head, StringComparison.OrdinalIgnoreCase),
            !string.Equals(workspace.Identity.Branch, owned.Branch, StringComparison.Ordinal),
            Array.AsReadOnly(conflictPaths.Select(static path => new WorktreeIntegrationConflict(path)).ToArray()),
            Array.AsReadOnly(changes.ToArray()),
            DateTimeOffset.UtcNow)
        {
            OwnershipId = owned.OwnershipId,
            OursTree = userCapture.Tree!,
            TheirsTree = laneCapture.Tree!,
            LaneHeadAtCapture = lane.Identity.HeadCommit,
            LaneBranchAtCapture = lane.Identity.Branch,
        });
        }
    }

    /// <summary>Re-captures a preview for an authorized apply. The ProposalId and path set must
    /// exactly match the freshly captured trees; no user files or index are changed.</summary>
    public async Task<WorktreeIntegrationCaptureOutcome> CaptureIntegrationForApplyAsync(
        WorktreeIntegrationCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { return await CaptureIntegrationForApplyCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        catch (UnauthorizedAccessException) { return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        catch (ArgumentException) { return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
    }

    private async Task<WorktreeIntegrationCaptureOutcome> CaptureIntegrationForApplyCoreAsync(
        WorktreeIntegrationCaptureRequest request, CancellationToken cancellationToken)
    {
        var worktree = request.Worktree;
        if (worktree is null || string.IsNullOrWhiteSpace(request.ProposalId)
            || request.ClaimedPaths is null || request.ClaimedPaths.Count == 0)
            return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        var previewResult = await PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), cancellationToken)
            .ConfigureAwait(false);
        if (!previewResult.Succeeded) return WorktreeIntegrationCaptureOutcome.Failure(previewResult.Error!.Value);
        var preview = previewResult.Preview!;
        if (!string.Equals(preview.ProposalId, request.ProposalId, StringComparison.Ordinal)
            || preview.HasConflicts)
            return WorktreeIntegrationCaptureOutcome.Failure(preview.HasConflicts
                ? GitWorktreeErrorCode.Conflict : GitWorktreeErrorCode.WorkspaceChanged);
        var expectedPaths = preview.Changes.Select(static change => change.RelativePath).Order(StringComparer.Ordinal).ToArray();
        var claimedPaths = request.ClaimedPaths.Order(StringComparer.Ordinal).ToArray();
        if (expectedPaths.Length != claimedPaths.Length
            || !expectedPaths.SequenceEqual(claimedPaths, StringComparer.Ordinal)
            || HasPlatformPathCollisions(claimedPaths))
            return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        if (expectedPaths.Length > MaxIntegrationFiles)
            return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);

        var loaded = await LoadIdentityAsync(worktree.RepositoryRoot, worktree.WorkspaceDataRoot,
            worktree.OwnershipId, cancellationToken).ConfigureAwait(false);
        if (!loaded.Succeeded || !IdentityEquals(worktree, loaded.Worktree!))
            return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);
        var owned = loaded.Worktree!;
        var workspace = await InspectAsync(owned.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        var lane = await InspectAsync(owned.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (!workspace.Succeeded || !lane.Succeeded
            || !PathEquals(workspace.Identity!.GitCommonDirectory, owned.GitCommonDirectory)
            || !PathEquals(lane.Identity!.GitCommonDirectory, owned.GitCommonDirectory)
            || !string.Equals(workspace.Identity.HeadCommit, preview.WorkspaceHeadAtCapture, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(workspace.Identity.Branch, preview.WorkspaceBranchAtCapture, StringComparison.Ordinal)
            || !string.Equals(lane.Identity.HeadCommit, preview.LaneHeadAtCapture, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lane.Identity.Branch, preview.LaneBranchAtCapture, StringComparison.Ordinal))
            return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.OwnershipMismatch);

        PrivateTempDirectory temp;
        try { temp = new PrivateTempDirectory(owned.WorkspaceDataRoot); }
        catch (Exception) { return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.InvalidPath); }
        using (temp)
        {
            var privateObjects = await CreatePrivateObjectEnvironment(owned.RepositoryRoot, temp.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!privateObjects.Success) return WorktreeIntegrationCaptureOutcome.Failure(privateObjects.Error!.Value);
            var objectEnvironment = privateObjects.Environment!;
            var ours = await CaptureCurrentTree(owned.RepositoryRoot, workspace.Identity.HeadCommit, temp.Path,
                "apply-ours.index", objectEnvironment, cancellationToken).ConfigureAwait(false);
            var theirs = await CaptureCurrentTree(owned.WorktreePath, lane.Identity.HeadCommit, temp.Path,
                "apply-theirs.index", objectEnvironment, cancellationToken).ConfigureAwait(false);
            if (!ours.Success || !theirs.Success)
                return WorktreeIntegrationCaptureOutcome.Failure(ours.Error ?? theirs.Error ?? GitWorktreeErrorCode.GitCommandFailed);
            if (!string.Equals(ours.Tree, preview.OursTree, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(theirs.Tree, preview.TheirsTree, StringComparison.OrdinalIgnoreCase))
                return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.WorkspaceChanged);

            var merge = await Git(owned.RepositoryRoot,
                ["merge-tree", "--write-tree", "--name-only", "--no-messages", "-z",
                    "--merge-base=" + owned.SnapshotCommit, ours.Commit!, theirs.Commit!],
                objectEnvironment, cancellationToken, allowConflictExit: true).ConfigureAwait(false);
            if (merge.Error is not null && merge.ExitCode != 1)
                return WorktreeIntegrationCaptureOutcome.Failure(merge.Error.Value);
            if (!TryParseMergeTreeOutput(merge.Output, merge.ExitCode, out var mergeTree, out var conflicts))
                return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            if (conflicts.Count > 0 || !string.Equals(mergeTree, preview.MergeTree, StringComparison.OrdinalIgnoreCase))
                return WorktreeIntegrationCaptureOutcome.Failure(conflicts.Count > 0
                    ? GitWorktreeErrorCode.Conflict : GitWorktreeErrorCode.WorkspaceChanged);

            var mergedView = await MaterializeAndHashTree(owned.RepositoryRoot, mergeTree!, temp.Path,
                objectEnvironment, "apply-merged", cancellationToken).ConfigureAwait(false);
            if (!mergedView.Success) return WorktreeIntegrationCaptureOutcome.Failure(mergedView.Error!.Value);

            var images = new List<WorktreeIntegrationFileImage>(expectedPaths.Length);
            long totalCapturedBytes = 0;
            foreach (var path in expectedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = preview.Changes.Single(item => item.RelativePath == path);
                if (change.ExpectedPreMode is not (null or "100644")
                    || change.ExpectedPostMode is not (null or "100644")
                    || change.ExpectedPreMode is not null && change.ExpectedPostMode is not null
                        && change.ExpectedPreMode != change.ExpectedPostMode)
                    return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                var full = Path.Combine(owned.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!SafeIntegrationLeaf(owned.RepositoryRoot, full))
                    return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsafePath);
                byte[]? before = null;
                if (change.ExpectedPreSha256 is not null)
                {
                    if (!File.Exists(full) || new FileInfo(full).Length > 2 * 1024 * 1024)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                    totalCapturedBytes += new FileInfo(full).Length;
                    if (totalCapturedBytes > MaxIntegrationImageBytes)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                    before = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
                    if (Sha256(before) != change.ExpectedPreSha256)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.WorkspaceChanged);
                }
                byte[]? after = null;
                if (change.ExpectedPostSha256 is not null)
                {
                    var mergedFull = Path.Combine(mergedView.WorktreePath!, path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(mergedFull) || new FileInfo(mergedFull).Length > 2 * 1024 * 1024)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                    totalCapturedBytes += new FileInfo(mergedFull).Length;
                    if (totalCapturedBytes > MaxIntegrationImageBytes)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                    after = await File.ReadAllBytesAsync(mergedFull, cancellationToken).ConfigureAwait(false);
                    if (Sha256(after) != change.ExpectedPostSha256)
                        return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.UnsupportedTransform);
                }
                images.Add(new WorktreeIntegrationFileImage(path, change.ExpectedPreSha256,
                    change.ExpectedPostSha256, change.ExpectedPreMode, change.ExpectedPostMode, before, after));
            }

            var finalWorkspace = await InspectAsync(owned.RepositoryRoot, cancellationToken).ConfigureAwait(false);
            var finalLane = await InspectAsync(owned.WorktreePath, cancellationToken).ConfigureAwait(false);
            if (!finalWorkspace.Succeeded || !finalLane.Succeeded
                || !string.Equals(finalWorkspace.Identity!.HeadCommit, preview.WorkspaceHeadAtCapture, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(finalWorkspace.Identity.Branch, preview.WorkspaceBranchAtCapture, StringComparison.Ordinal)
                || !string.Equals(finalLane.Identity!.HeadCommit, preview.LaneHeadAtCapture, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(finalLane.Identity.Branch, preview.LaneBranchAtCapture, StringComparison.Ordinal)
                || !(await VerifyCurrentCapture(owned.RepositoryRoot, ours, cancellationToken).ConfigureAwait(false))
                || !(await VerifyCurrentCapture(owned.WorktreePath, theirs, cancellationToken).ConfigureAwait(false)))
                return WorktreeIntegrationCaptureOutcome.Failure(GitWorktreeErrorCode.WorkspaceChanged);
            return WorktreeIntegrationCaptureOutcome.Success(new WorktreeIntegrationCapture(preview,
                Array.AsReadOnly(images.ToArray())));
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
            || metadata.Version != 2 || metadata.State != "active"
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

    private async Task<PrivateObjectEnvironmentResult> CreatePrivateObjectEnvironment(string repositoryRoot,
        string tempDirectory, CancellationToken cancellationToken)
    {
        var sourceObjects = await Git(repositoryRoot,
            ["rev-parse", "--path-format=absolute", "--git-path", "objects"], null, cancellationToken);
        if (!sourceObjects.Success) return PrivateObjectEnvironmentResult.Fail(sourceObjects.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        string source;
        try { source = Canonical(sourceObjects.Output.Trim()); }
        catch (Exception) { return PrivateObjectEnvironmentResult.Fail(GitWorktreeErrorCode.InvalidPath); }
        if (HasReparseDirectoryComponent(source) || !Directory.Exists(source))
            return PrivateObjectEnvironmentResult.Fail(GitWorktreeErrorCode.UnsafePath);
        var objects = Path.Combine(tempDirectory, "objects");
        var info = Path.Combine(objects, "info");
        try
        {
            Directory.CreateDirectory(info);
            // Git's alternates parser treats a CR in a CRLF record as part of the path on Windows.
            // Store a slash-normalized absolute path and LF-only terminator.
            await File.WriteAllTextAsync(Path.Combine(info, "alternates"), source.Replace('\\', '/') + "\n",
                new UTF8Encoding(false), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return PrivateObjectEnvironmentResult.Fail(GitWorktreeErrorCode.InvalidPath); }
        return PrivateObjectEnvironmentResult.Ok(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_OBJECT_DIRECTORY"] = objects,
        });
    }

    private async Task<CapturedSideResult> CaptureCurrentTree(string root, string expectedHead, string tempDirectory,
        string indexName, IReadOnlyDictionary<string, string> objectEnvironment, CancellationToken cancellationToken)
    {
        var currentHead = await Git(root, ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken);
        if (!currentHead.Success) return CapturedSideResult.Fail(GitWorktreeErrorCode.MissingHead);
        if (!string.Equals(currentHead.Output.Trim(), expectedHead, StringComparison.OrdinalIgnoreCase))
            return CapturedSideResult.Fail(GitWorktreeErrorCode.WorkspaceChanged);
        var head = currentHead.Output.Trim();
        var headEntries = await ReadTreeEntries(root, head, objectEnvironment, cancellationToken);
        if (!headEntries.Success) return CapturedSideResult.Fail(headEntries.Error!.Value);
        var pathsResult = await ListWorkingPaths(root, cancellationToken);
        if (!pathsResult.Success) return CapturedSideResult.Fail(pathsResult.Error!.Value);
        var workingPaths = pathsResult.Paths!;
        var allPaths = headEntries.Entries!.Keys.Concat(workingPaths).Distinct(StringComparer.Ordinal).ToArray();
        var unmerged = await Git(root, ["ls-files", "--unmerged", "-z"], null, cancellationToken);
        if (!unmerged.Success) return CapturedSideResult.Fail(unmerged.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (unmerged.Output.Length != 0) return CapturedSideResult.Fail(GitWorktreeErrorCode.Conflict);

        foreach (var path in allPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeGitPath(path)) return CapturedSideResult.Fail(GitWorktreeErrorCode.UnsafePath);
            if (_redaction.IsSecretPath(path)) return CapturedSideResult.Fail(GitWorktreeErrorCode.SecretPathPresent);
            if (HasReparseComponent(root, path)) return CapturedSideResult.Fail(GitWorktreeErrorCode.UnsafePath);
        }
        foreach (var check in new[]
        {
            await CheckFilters(root, workingPaths, cancellationToken, null, objectEnvironment),
            await CheckFilters(root, headEntries.Paths!, cancellationToken, head, objectEnvironment),
            await CheckTransforms(root, workingPaths, cancellationToken, null, objectEnvironment),
            await CheckTransforms(root, headEntries.Paths!, cancellationToken, head, objectEnvironment),
            await CheckMergeDrivers(root, workingPaths, cancellationToken, null, objectEnvironment),
            await CheckMergeDrivers(root, headEntries.Paths!, cancellationToken, head, objectEnvironment),
        })
            if (!check.Success) return CapturedSideResult.Fail(check.Error ?? GitWorktreeErrorCode.GitCommandFailed);

        var before = await HashWorkingPaths(root, allPaths, cancellationToken);
        if (!before.Success) return CapturedSideResult.Fail(before.Error!.Value);
        var indexFile = Path.Combine(tempDirectory, indexName);
        var env = CombineEnvironments(objectEnvironment, IndexEnvironment(indexFile));
        var readTree = await Git(root, ["read-tree", head], env, cancellationToken);
        if (!readTree.Success) return CapturedSideResult.Fail(readTree.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var add = await Git(root, ["add", "-A", "--", "."], env, cancellationToken);
        if (!add.Success) return CapturedSideResult.Fail(add.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var stage = await Git(root, ["ls-files", "--stage", "-z"], env, cancellationToken);
        if (!stage.Success) return CapturedSideResult.Fail(stage.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (!IsCompleteNulList(stage.Output)) return CapturedSideResult.Fail(GitWorktreeErrorCode.GitOutputTruncated);
        if (SplitNul(stage.Output).Any(static entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
            return CapturedSideResult.Fail(GitWorktreeErrorCode.UnsupportedSubmoduleState);
        var tree = await Git(root, ["write-tree"], env, cancellationToken);
        if (!tree.Success || !IsOid(tree.Output.Trim()))
            return CapturedSideResult.Fail(tree.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var treeMaterialization = await MaterializeAndHashTree(root, tree.Output.Trim(), tempDirectory, objectEnvironment,
            Path.GetFileNameWithoutExtension(indexName), cancellationToken);
        if (!treeMaterialization.Success) return CapturedSideResult.Fail(treeMaterialization.Error!.Value);
        var expectedMaterializedHashes = before.Hashes!.Where(static item => item.Value is not null)
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        if (!SameHashes(expectedMaterializedHashes, treeMaterialization.Hashes!))
            return CapturedSideResult.Fail(GitWorktreeErrorCode.UnsupportedTransform);
        var commit = await Git(root,
            ["-c", "user.name=OmniCore preview", "-c", "user.email=omnicore-preview@localhost", "commit-tree",
                tree.Output.Trim(), "-p", head, "-m", "OmniCore temporary integration preview"], objectEnvironment, cancellationToken);
        if (!commit.Success || !IsOid(commit.Output.Trim()))
            return CapturedSideResult.Fail(commit.Error ?? GitWorktreeErrorCode.GitCommandFailed);

        var afterPaths = await ListWorkingPaths(root, cancellationToken);
        var after = await HashWorkingPaths(root, allPaths, cancellationToken);
        if (!afterPaths.Success || !after.Success || !SamePaths(workingPaths, afterPaths.Paths!)
            || !SameHashes(before.Hashes!, after.Hashes!))
            return CapturedSideResult.Fail(GitWorktreeErrorCode.WorkspaceChanged);
        return CapturedSideResult.Ok(commit.Output.Trim(), tree.Output.Trim(), head, workingPaths, allPaths, before.Hashes!);
    }

    private async Task<TreeEntriesResult> ReadTreeEntries(string root, string treeish,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var result = await Git(root, ["ls-tree", "-r", "-z", "--full-tree", treeish], environment, cancellationToken);
        if (!result.Success) return TreeEntriesResult.Fail(result.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (!IsCompleteNulList(result.Output)) return TreeEntriesResult.Fail(GitWorktreeErrorCode.GitOutputTruncated);
        var entries = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        foreach (var raw in SplitNul(result.Output))
        {
            var tab = raw.IndexOf('\t');
            if (tab <= 0 || tab == raw.Length - 1) return TreeEntriesResult.Fail(GitWorktreeErrorCode.GitCommandFailed);
            var fields = raw[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = raw[(tab + 1)..];
            if (fields.Length != 3 || !IsOid(fields[2]) || !IsSafeGitPath(path))
                return TreeEntriesResult.Fail(GitWorktreeErrorCode.UnsafePath);
            if (_redaction.IsSecretPath(path)) return TreeEntriesResult.Fail(GitWorktreeErrorCode.SecretPathPresent);
            if (fields[0] == "120000") return TreeEntriesResult.Fail(GitWorktreeErrorCode.UnsafePath);
            if (fields[0] == "160000") return TreeEntriesResult.Fail(GitWorktreeErrorCode.UnsupportedSubmoduleState);
            if (fields[1] != "blob" || fields[0] is not ("100644" or "100755"))
                return TreeEntriesResult.Fail(GitWorktreeErrorCode.UnsafePath);
            entries.Add(path, new TreeEntry(fields[0], fields[2]));
        }
        if (HasPlatformPathCollisions(entries.Keys)) return TreeEntriesResult.Fail(GitWorktreeErrorCode.UnsafePath);
        return TreeEntriesResult.Ok(entries);
    }

    private async Task<WorkingPathsResult> ListWorkingPaths(string root, CancellationToken cancellationToken)
    {
        var result = await Git(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], null, cancellationToken);
        if (!result.Success) return WorkingPathsResult.Fail(result.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        if (!IsCompleteNulList(result.Output)) return WorkingPathsResult.Fail(GitWorktreeErrorCode.GitOutputTruncated);
        var paths = SplitNul(result.Output);
        if (HasPlatformPathCollisions(paths)) return WorkingPathsResult.Fail(GitWorktreeErrorCode.UnsafePath);
        foreach (var path in paths)
        {
            if (!IsSafeGitPath(path)) return WorkingPathsResult.Fail(GitWorktreeErrorCode.UnsafePath);
            if (_redaction.IsSecretPath(path)) return WorkingPathsResult.Fail(GitWorktreeErrorCode.SecretPathPresent);
            if (HasReparseComponent(root, path)) return WorkingPathsResult.Fail(GitWorktreeErrorCode.UnsafePath);
        }
        return WorkingPathsResult.Ok(paths);
    }

    private async Task<HashMapResult> HashWorkingPaths(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeGitPath(path) || HasReparseComponent(root, path)) return HashMapResult.Fail(GitWorktreeErrorCode.UnsafePath);
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (Directory.Exists(full)) return HashMapResult.Fail(GitWorktreeErrorCode.UnsafePath);
                hashes[path] = File.Exists(full) ? await HashFile(full, cancellationToken) : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return HashMapResult.Fail(GitWorktreeErrorCode.WorkspaceChanged); }
        }
        return HashMapResult.Ok(hashes);
    }

    private async Task<bool> VerifyCurrentCapture(string root, CapturedSideResult capture, CancellationToken cancellationToken)
    {
        var head = await Git(root, ["rev-parse", "--verify", "HEAD^{commit}"], null, cancellationToken);
        if (!head.Success || !string.Equals(head.Output.Trim(), capture.Head, StringComparison.OrdinalIgnoreCase)) return false;
        var paths = await ListWorkingPaths(root, cancellationToken);
        if (!paths.Success || !SamePaths(capture.WorkingPaths, paths.Paths!)) return false;
        var hashes = await HashWorkingPaths(root, capture.HashedPaths, cancellationToken);
        return hashes.Success && SameHashes(capture.Hashes, hashes.Hashes!);
    }

    private async Task<MaterializedTreeResult> MaterializeAndHashTree(string root, string tree, string tempDirectory,
        IReadOnlyDictionary<string, string> objectEnvironment, string name, CancellationToken cancellationToken)
    {
        var entries = await ReadTreeEntries(root, tree, objectEnvironment, cancellationToken);
        if (!entries.Success) return MaterializedTreeResult.Fail(entries.Error!.Value);
        foreach (var check in new[]
        {
            await CheckFilters(root, entries.Paths!, cancellationToken, tree, objectEnvironment),
            await CheckTransforms(root, entries.Paths!, cancellationToken, tree, objectEnvironment),
            await CheckMergeDrivers(root, entries.Paths!, cancellationToken, tree, objectEnvironment),
        })
            if (!check.Success) return MaterializedTreeResult.Fail(check.Error ?? GitWorktreeErrorCode.GitCommandFailed);

        var worktree = Path.Combine(tempDirectory, name + "-view");
        Directory.CreateDirectory(worktree);
        if (HasReparseDirectoryComponent(worktree)) return MaterializedTreeResult.Fail(GitWorktreeErrorCode.UnsafePath);
        var index = Path.Combine(tempDirectory, name + ".materialized.index");
        var env = CombineEnvironments(objectEnvironment, IndexEnvironment(index), new Dictionary<string, string> { ["GIT_WORK_TREE"] = worktree });
        var readTree = await Git(root, ["read-tree", tree], env, cancellationToken);
        if (!readTree.Success) return MaterializedTreeResult.Fail(readTree.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var checkout = await Git(root, ["checkout-index", "--all", "--force"], env, cancellationToken);
        if (!checkout.Success) return MaterializedTreeResult.Fail(checkout.Error ?? GitWorktreeErrorCode.GitCommandFailed);
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in entries.Paths!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReparseComponent(worktree, path)) return MaterializedTreeResult.Fail(GitWorktreeErrorCode.UnsafePath);
            var full = Path.Combine(worktree, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return MaterializedTreeResult.Fail(GitWorktreeErrorCode.UnsupportedTransform);
            try { hashes[path] = await HashFile(full, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return MaterializedTreeResult.Fail(GitWorktreeErrorCode.UnsupportedTransform); }
        }
        return MaterializedTreeResult.Ok(hashes, worktree);
    }

    private async Task<GitResult> CheckMergeDrivers(string root, IReadOnlyList<string> paths,
        CancellationToken cancellationToken, string? sourceTree, IReadOnlyDictionary<string, string>? environment = null)
    {
        for (var offset = 0; offset < paths.Count; offset += 96)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = paths.Skip(offset).Take(96).ToArray();
            var args = new List<string> { "check-attr" };
            if (sourceTree is not null) args.Add("--source=" + sourceTree);
            args.AddRange(["-z", "merge", "--", .. chunk]);
            var result = await Git(root, args, environment, cancellationToken);
            if (!result.Success) return GitResult.Failure(sourceTree is not null
                ? GitWorktreeErrorCode.UnsupportedMergeDriver : result.Error ?? GitWorktreeErrorCode.GitCommandFailed);
            if (!IsCompleteNulList(result.Output)) return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            var parts = SplitNul(result.Output);
            if (parts.Count != chunk.Length * 3) return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            for (var i = 2; i < parts.Count; i += 3)
                if (parts[i] is not ("unspecified" or "unset")) return GitResult.Failure(GitWorktreeErrorCode.UnsupportedMergeDriver);
        }
        return GitResult.Ok("");
    }

    private static IReadOnlyDictionary<string, string> CombineEnvironments(params IReadOnlyDictionary<string, string>[] sources)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var pair in source) result[pair.Key] = pair.Value;
        return result;
    }

    private static bool SamePaths(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool SameHashes(IReadOnlyDictionary<string, string?> left, IReadOnlyDictionary<string, string?> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool IdentityEquals(WorktreeIdentity left, WorktreeIdentity right) =>
        left.WorkspaceId.Equals(right.WorkspaceId) && left.LaneId == right.LaneId
        && PathEquals(left.RepositoryRoot, right.RepositoryRoot) && PathEquals(left.GitCommonDirectory, right.GitCommonDirectory)
        && PathEquals(left.WorktreePath, right.WorktreePath) && PathEquals(left.WorkspaceDataRoot, right.WorkspaceDataRoot)
        && left.Head == right.Head && left.Branch == right.Branch && left.SnapshotCommit == right.SnapshotCommit
        && left.SnapshotTree == right.SnapshotTree && left.UserIndexTree == right.UserIndexTree
        && left.SnapshotRef == right.SnapshotRef && left.OwnershipId == right.OwnershipId && left.Base == right.Base;

    private static bool TryParseMergeTreeOutput(string output, int exitCode, out string? mergeTree, out List<string> conflicts)
    {
        mergeTree = null;
        conflicts = [];
        if (output.Length == 0 || output.Length >= CapturedOutputLimit) return false;
        var delimiter = output.IndexOf('\0');
        if (delimiter < 0) return false;
        mergeTree = output[..delimiter].Trim();
        if (!IsOid(mergeTree)) return false;
        var tail = output[(delimiter + 1)..];
        if (exitCode == 0) return tail.Length == 0;
        if (exitCode != 1 || !IsCompleteNulList(tail)) return false;
        conflicts = SplitNul(tail);
        return conflicts.Count > 0 && conflicts.All(IsSafeGitPath) && !HasPlatformPathCollisions(conflicts);
    }

    private static bool TryParseNameStatus(string output, out List<string> paths)
    {
        paths = [];
        if (output.Length == 0) return true;
        if (output.Length >= CapturedOutputLimit || !IsCompleteNulList(output)) return false;
        var tokens = SplitNul(output);
        if (tokens.Count % 2 != 0) return false;
        for (var i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i] is not ("A" or "D" or "M" or "T")) return false;
            if (!IsSafeGitPath(tokens[i + 1])) return false;
            paths.Add(tokens[i + 1]);
        }
        return !HasPlatformPathCollisions(paths);
    }

    private async Task<GitResult> CheckFilters(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken,
        string? sourceTree, IReadOnlyDictionary<string, string>? environment = null)
    {
        for (var offset = 0; offset < paths.Count; offset += 96)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = paths.Skip(offset).Take(96).ToArray();
            var args = new List<string> { "check-attr" };
            if (sourceTree is not null) args.Add("--source=" + sourceTree);
            args.AddRange(["-z", "filter", "--", .. chunk]);
            var result = await Git(root, args, environment, cancellationToken);
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
        CancellationToken cancellationToken, string? sourceTree, IReadOnlyDictionary<string, string>? environment = null)
    {
        string[] attributes = ["text", "eol", "working-tree-encoding", "ident"];
        for (var offset = 0; offset < paths.Count; offset += 96)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = paths.Skip(offset).Take(96).ToArray();
            var args = new List<string> { "check-attr" };
            if (sourceTree is not null) args.Add("--source=" + sourceTree);
            args.AddRange(["-z", .. attributes, "--", .. chunk]);
            var result = await Git(root, args, environment, cancellationToken);
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
        CancellationToken cancellationToken, bool allowConflictExit = false)
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
            "-c", "merge.default=text",
            "-c", "merge.renormalize=false",
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
            var output = result.Stdout ?? "";
            if (output.Length >= CapturedOutputLimit)
                return GitResult.Failure(GitWorktreeErrorCode.GitOutputTruncated);
            if (result.ExitCode == 0) return GitResult.Ok(output);
            if (allowConflictExit && result.ExitCode == 1)
                return new GitResult(false, output, GitWorktreeErrorCode.GitCommandFailed, result.ExitCode);
            return GitResult.Failure(GitWorktreeErrorCode.GitCommandFailed);
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
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ComputeProposalId(WorktreeIdentity ownership, WorktreeRepositoryIdentity workspace,
        WorktreeRepositoryIdentity lane, string oursTree, string theirsTree, string? mergeTree,
        IReadOnlyList<WorktreeIntegrationChange> changes)
    {
        var fields = new List<string>
        {
            ownership.WorkspaceId.ToString(), ownership.LaneId.ToString(), ownership.OwnershipId,
            Canonical(ownership.RepositoryRoot), Canonical(ownership.GitCommonDirectory),
            ownership.SnapshotCommit, ownership.SnapshotTree, oursTree, theirsTree, mergeTree ?? "",
            workspace.HeadCommit, workspace.Branch ?? "", lane.HeadCommit, lane.Branch ?? "",
        };
        foreach (var change in changes.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            fields.Add(change.RelativePath);
            fields.Add(change.ExpectedPreSha256 ?? "absent");
            fields.Add(change.ExpectedPostSha256 ?? "absent");
            fields.Add(change.ExpectedPreMode ?? "absent");
            fields.Add(change.ExpectedPostMode ?? "absent");
        }
        var bytes = Encoding.UTF8.GetBytes(string.Join('\0', fields));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool SafeIntegrationLeaf(string root, string fullPath)
    {
        try
        {
            var canonicalRoot = Canonical(root);
            var canonicalFull = Canonical(fullPath);
            if (!IsWithin(canonicalRoot, canonicalFull) || HasReparseDirectoryComponent(canonicalFull)) return false;
            var parent = Path.GetDirectoryName(canonicalFull);
            if (parent is null || !Directory.Exists(parent) || HasReparseDirectoryComponent(parent)) return false;
            if (Directory.Exists(canonicalFull)) return false;
            if (File.Exists(canonicalFull) && (File.GetAttributes(canonicalFull) & FileAttributes.ReparsePoint) != 0)
                return false;
            return true;
        }
        catch (Exception) { return false; }
    }
    private static bool IsWorkspaceId(string value) => value.Length == 16 && value.All(Uri.IsHexDigit);
    private static bool IsOid(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static bool IsSafeGitPath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.IndexOf('\0') >= 0
            || path.Contains('\uFFFD') || path.Any(char.IsControl)) return false;
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or "..")) return false;
        if (!OperatingSystem.IsWindows()) return true;
        foreach (var part in parts)
        {
            if (part.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0 || part.EndsWith(' ') || part.EndsWith('.')) return false;
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')) return false;
        }
        return true;
    }

    private static bool HasPlatformPathCollisions(IEnumerable<string> paths)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparison);
        return paths.Any(path => !seen.Add(path.Replace('\\', '/')));
    }
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

    private sealed record GitResult(bool Success, string Output, GitWorktreeErrorCode? Error, int ExitCode = 0)
    {
        public static GitResult Ok(string output) => new(true, output, null);
        public static GitResult Failure(GitWorktreeErrorCode error) => new(false, "", error);
    }

    private sealed record TreeEntry(string Mode, string ObjectId);
    private sealed record PrivateObjectEnvironmentResult(bool Success, IReadOnlyDictionary<string, string>? Environment,
        GitWorktreeErrorCode? Error)
    {
        public static PrivateObjectEnvironmentResult Ok(IReadOnlyDictionary<string, string> environment) => new(true, environment, null);
        public static PrivateObjectEnvironmentResult Fail(GitWorktreeErrorCode error) => new(false, null, error);
    }
    private sealed record TreeEntriesResult(bool Success, Dictionary<string, TreeEntry>? Entries,
        GitWorktreeErrorCode? Error)
    {
        public IReadOnlyList<string>? Paths => Entries?.Keys.ToArray();
        public static TreeEntriesResult Ok(Dictionary<string, TreeEntry> entries) => new(true, entries, null);
        public static TreeEntriesResult Fail(GitWorktreeErrorCode error) => new(false, null, error);
    }
    private sealed record WorkingPathsResult(bool Success, List<string>? Paths, GitWorktreeErrorCode? Error)
    {
        public static WorkingPathsResult Ok(List<string> paths) => new(true, paths, null);
        public static WorkingPathsResult Fail(GitWorktreeErrorCode error) => new(false, null, error);
    }
    private sealed record HashMapResult(bool Success, Dictionary<string, string?>? Hashes, GitWorktreeErrorCode? Error)
    {
        public static HashMapResult Ok(Dictionary<string, string?> hashes) => new(true, hashes, null);
        public static HashMapResult Fail(GitWorktreeErrorCode error) => new(false, null, error);
    }
    private sealed record CapturedSideResult(bool Success, string? Commit, string? Tree, string? Head,
        List<string> WorkingPaths, string[] HashedPaths, Dictionary<string, string?> Hashes, GitWorktreeErrorCode? Error)
    {
        public static CapturedSideResult Ok(string commit, string tree, string head, List<string> workingPaths,
            string[] hashedPaths, Dictionary<string, string?> hashes) => new(true, commit, tree, head, workingPaths, hashedPaths, hashes, null);
        public static CapturedSideResult Fail(GitWorktreeErrorCode error) => new(false, null, null, null, [], [], new(StringComparer.Ordinal), error);
    }
    private sealed record MaterializedTreeResult(bool Success, Dictionary<string, string?>? Hashes,
        string? WorktreePath, GitWorktreeErrorCode? Error)
    {
        public static MaterializedTreeResult Ok(Dictionary<string, string?> hashes, string worktreePath) =>
            new(true, hashes, worktreePath, null);
        public static MaterializedTreeResult Fail(GitWorktreeErrorCode error) => new(false, null, null, error);
    }

    internal sealed record OwnershipMetadata(int Version, string OwnershipId, string WorkspaceId, string LaneId,
        string SnapshotId, string DataRoot, string RepositoryRoot, string GitCommonDirectory, string WorktreePath, string MetadataPath,
        string SnapshotRef, string Base, string Head, string? Branch, string? SnapshotTree, string? UserIndexTree,
        string? SnapshotCommit, DateTimeOffset CreatedAtUtc, string State);

    private sealed class PrivateTempDirectory : IDisposable
    {
        private readonly string _parent;
        public string Path { get; }
        public PrivateTempDirectory(string parent)
        {
            _parent = Canonical(parent);
            Path = System.IO.Path.Combine(_parent, ".m7-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try
            {
                if (IsWithin(_parent, Canonical(Path)) && !HasReparseDirectoryComponent(Path)) DeleteOwnedTree(Path);
            }
            catch (Exception) { }
        }

        private static void DeleteOwnedTree(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteOwnedTree(entry);
                    if (Directory.Exists(entry)) Directory.Delete(entry, false);
                }
                else
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            if (Directory.Exists(directory))
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(directory, false);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(GitWorktreeStore.OwnershipMetadata))]
internal partial class GitWorktreeJsonContext : JsonSerializerContext { }
