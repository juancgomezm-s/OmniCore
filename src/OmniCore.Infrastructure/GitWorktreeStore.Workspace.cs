namespace OmniCore.Infrastructure;

using System.Text;
using OmniCore.Domain;
using Task = System.Threading.Tasks.Task;

public sealed partial class GitWorktreeStore
{
    /// <summary>Read-only, argv-only queries; never refresh the user's index or invoke hooks/diff drivers.</summary>
    public async Task<WorkspaceGitView> ReadWorkspaceAsync(string root, CancellationToken token)
    {
        var inspected = await InspectAsync(root, token);
        if (!inspected.Succeeded) return new(false, "", "", 0, 0, [], [], []);
        if (!PathEquals(inspected.Identity!.RepoRoot, root)) throw new IOException("workspace_must_be_repository_root");
        var status = await RequiredGit(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], token);
        var counts = new Dictionary<string, (int Added, int Deleted)>(StringComparer.Ordinal);
        foreach (var args in new[] { new[] { "diff", "--numstat", "-z", "--no-renames", "--no-ext-diff" },
            new[] { "diff", "--cached", "--numstat", "-z", "--no-renames", "--no-ext-diff" } })
            foreach (var entry in (await RequiredGit(root, args, token)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = entry.Split('\t', 3);
                if (fields.Length != 3) throw new IOException("git_numstat_invalid");
                var old = counts.GetValueOrDefault(fields[2]);
                counts[fields[2]] = (old.Added + (int.TryParse(fields[0], out var a) ? a : 0),
                    old.Deleted + (int.TryParse(fields[1], out var d) ? d : 0));
            }
        var parts = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<WorkspaceGitFile>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length < 4) throw new IOException("git_status_invalid");
            var xy = parts[i][..2]; var path = parts[i][3..];
            if (xy.Contains('R') || xy.Contains('C')) i++; // porcelain -z is destination, then original.
            if (!IsSafeGitPath(path) || _redaction.IsSecretPath(path)) continue;
            var c = counts.GetValueOrDefault(path);
            files.Add(new(path, xy, c.Added, c.Deleted));
        }
        var upstreamResult = await Git(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], null, token);
        var upstream = upstreamResult.Success ? upstreamResult.Output.Trim() : "";
        var ahead = 0; var behind = 0;
        if (upstream.Length > 0)
        {
            var values = (await RequiredGit(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], token)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 2) { int.TryParse(values[0], out ahead); int.TryParse(values[1], out behind); }
        }
        var branches = (await RequiredGit(root, ["for-each-ref", "--count=200", "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)", "refs/heads/"], token))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(f => f.Length == 3).Select(f => new WorkspaceBranch(f[0], f[1], f[2])).ToArray();
        var worktrees = new List<WorkspaceWorktree>();
        foreach (var block in (await RequiredGit(root, ["worktree", "list", "--porcelain", "-z"], token)).Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var path = fields.FirstOrDefault(f => f.StartsWith("worktree ", StringComparison.Ordinal));
            if (path is not null) worktrees.Add(new(path[9..], fields.FirstOrDefault(f => f.StartsWith("HEAD ", StringComparison.Ordinal))?[5..] ?? "",
                fields.FirstOrDefault(f => f.StartsWith("branch ", StringComparison.Ordinal))?[7..]));
        }
        var committed = SplitNul(await RequiredGit(root, ["diff-tree", "--root", "--no-commit-id", "--name-only", "--no-renames", "-r", "-z", "HEAD"], token))
            .Where(path => IsSafeGitPath(path) && !_redaction.IsSecretPath(path)).ToArray();
        return new(true, inspected.Identity.Branch ?? "HEAD", upstream, ahead, behind, files, branches, worktrees)
            { LastCommit = inspected.Identity.HeadCommit, CommittedFiles = committed };
    }

    public async Task<IReadOnlyList<WorkspaceFile>> ReadDirectoryAsync(string root, string relative, CancellationToken token)
    {
        var full = SafeWorkspacePath(root, relative, allowRoot: true);
        var entries = Directory.EnumerateFileSystemEntries(full).Order(StringComparer.OrdinalIgnoreCase).Take(513).ToArray();
        if (entries.Length > 512) throw new IOException("directory_limit_512");
        var paths = entries.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Where(path => path != ".git"
            && !path.StartsWith(".git/", StringComparison.Ordinal) && !_redaction.IsSecretPath(path)).ToArray();
        var ignored = paths.Length == 0 ? null : await Git(root, ["check-ignore", "-z", "--stdin"], null, token,
            allowConflictExit: true, standardInput: string.Join('\0', paths.Select(path => Directory.Exists(Path.Combine(root, path)) ? path + "/" : path)) + "\0");
        var excluded = ignored?.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) ?? [];
        return paths.Where(path => !excluded.Contains(path) && !excluded.Contains(path + "/") && !HasReparseComponent(root, path)).Select(path =>
            new WorkspaceFile(path, Directory.Exists(Path.Combine(root, path)), "", 0, 0)).ToArray();
    }

    public async Task<IReadOnlyList<string>> SearchWorkspaceAsync(string root, string term, CancellationToken token)
    {
        if (term.Length is < 1 or > 160) throw new ArgumentException("search_invalid");
        return (await RequiredGit(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
            .Where(path => path.Contains(term, StringComparison.OrdinalIgnoreCase) && IsSafeGitPath(path)
                && !_redaction.IsSecretPath(path) && !HasReparseComponent(root, path)).Take(200).ToArray();
    }

    public async Task<WorkspaceFileView> ReadFileAsync(string root, string relative, bool diff, CancellationToken token)
    {
        var full = SafeWorkspacePath(root, relative);
        var ignored = await Git(root, ["check-ignore", "--", relative], null, token, allowConflictExit: true);
        if (ignored.Success) throw new IOException("workspace_path_ignored");
        if (!diff)
        {
            if (new FileInfo(full).Length > 262144) throw new IOException("file_limit_256k");
            var bytes = await File.ReadAllBytesAsync(full, token);
            if (bytes.Contains((byte)0)) return new(relative, "", true);
            return new(relative, new UTF8Encoding(false, true).GetString(bytes), false);
        }
        var result = await Git(root, ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "HEAD", "--", relative], null, token);
        if (!result.Success) throw new IOException("git_diff_unavailable");
        if (result.Output.Length == 0 && File.Exists(full))
        {
            var tracked = await Git(root, ["--literal-pathspecs", "ls-files", "--error-unmatch", "--", relative], null, token);
            if (!tracked.Success)
            {
                var file = await ReadFileAsync(root, relative, false, token);
                return new(relative, file.Binary ? "Binary file" : $"+++ {relative}\n" + string.Join('\n', file.Text.Split('\n').Select(line => "+" + line)), file.Binary);
            }
        }
        return new(relative, result.Output, false);
    }

    public string SafeWorkspacePath(string root, string relative, bool allowRoot = false)
    {
        if (relative.Length == 0 && allowRoot) return Canonical(root);
        if (!IsSafeGitPath(relative) || relative.Replace('\\', '/').Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase))
            || _redaction.IsSecretPath(relative) || HasReparseComponent(root, relative)) throw new IOException("workspace_path_unsafe");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithinOrEqual(root, full) || PathEquals(root, full)) throw new IOException("workspace_path_outside_root");
        return full;
    }

    public async Task<WorkspaceFileView> ReadCheckpointDiffAsync(string root, string path, WorkspaceSnapshot point,
        WorkspaceSnapshot? previous, CancellationToken token)
    {
        SafeWorkspacePath(root, path);
        if (!IsOid(point.Commit) || !IsOid(point.SourceHead) || previous is not null && !IsOid(previous.Commit))
            throw new IOException("checkpoint_identity_invalid");
        var diff = await RequiredGit(root, ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-renames",
            previous?.Commit ?? point.SourceHead, point.Commit, "--", path], token);
        return new(path, diff, false);
    }

    private async Task<string> RequiredGit(string root, IReadOnlyList<string> args, CancellationToken token,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await Git(root, args, env, token);
        if (!result.Success) throw new IOException("git_query_failed:" + result.Error);
        return result.Output;
    }

    /// <summary>A checkpoint is a private Git ref, not a commit on the user's branch. No worktree is materialized here.</summary>
    public async Task<WorkspaceSnapshot> CaptureWorkspaceAsync(string root, string dataRoot, CancellationToken token)
    {
        var inspected = await InspectAsync(root, token);
        if (!inspected.Succeeded || !PathEquals(inspected.Identity!.RepoRoot, root)) throw new IOException("snapshot_repository_unavailable");
        root = inspected.Identity.RepoRoot;
        dataRoot = Canonical(dataRoot);
        if (IsWithinOrEqual(root, dataRoot) || HasReparseDirectoryComponent(dataRoot)) throw new IOException("snapshot_data_root_unsafe");
        var paths = SplitNul(await RequiredGit(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token));
        if (paths.Count > 10000) throw new IOException("snapshot_file_limit_10000");
        long bytes = 0;
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var full = SafeWorkspacePath(root, path);
            if (File.Exists(full)) bytes += new FileInfo(full).Length;
            if (bytes > 64L * 1024 * 1024) throw new IOException("snapshot_bytes_limit_64m");
            hashes[path] = File.Exists(full) ? await HashFile(full, token) : null;
        }
        var unmerged = await RequiredGit(root, ["ls-files", "--unmerged", "-z"], token);
        if (unmerged.Length > 0) throw new IOException("snapshot_conflicts");
        var filters = await CheckFilters(root, paths, token, null);
        var transforms = await CheckTransforms(root, paths, token, null);
        if (!filters.Success || !transforms.Success) throw new IOException("snapshot_unsupported_transform");
        Directory.CreateDirectory(dataRoot);
        using var temp = new PrivateTempDirectory(dataRoot);
        var env = IndexEnvironment(Path.Combine(temp.Path, "checkpoint.index"));
        await RequiredGit(root, ["read-tree", inspected.Identity.HeadCommit], token, env);
        await RequiredGit(root, ["add", "-A", "--", "."], token, env);
        var candidate = await RequiredGit(root, ["ls-files", "--stage", "-z"], token, env);
        if (SplitNul(candidate).Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal) || entry.StartsWith("120000 ", StringComparison.Ordinal)))
            throw new IOException("snapshot_unsupported_link");
        var tree = (await RequiredGit(root, ["write-tree"], token, env)).Trim();
        var currentPaths = SplitNul(await RequiredGit(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token));
        if (!SamePaths(paths, currentPaths)) throw new IOException("snapshot_workspace_changed");
        foreach (var (path, hash) in hashes)
        {
            var full = SafeWorkspacePath(root, path);
            var current = File.Exists(full) ? await HashFile(full, token) : null;
            if (current != hash) throw new IOException("snapshot_workspace_changed");
        }
        if ((await RequiredGit(root, ["rev-parse", "HEAD"], token)).Trim() != inspected.Identity.HeadCommit)
            throw new IOException("snapshot_head_changed");
        var commit = (await RequiredGit(root, ["-c", "user.name=OmniCore snapshot", "-c", "user.email=omnicore@localhost", "commit-tree", tree,
            "-p", inspected.Identity.HeadCommit, "-m", "OmniCore private conversation checkpoint"], token)).Trim();
        var reference = "refs/omnicore/checkpoints/" + Guid.NewGuid().ToString("N");
        await RequiredGit(root, ["update-ref", reference, commit, new string('0', commit.Length)], token);
        return new(commit, tree, reference, inspected.Identity.HeadCommit);
    }

    public async Task CreateCheckpointWorktreeAsync(string root, string destination, string branch, WorkspaceSnapshot snapshot, CancellationToken token)
    {
        if (!branch.StartsWith("omnicore/alternative-", StringComparison.Ordinal) || branch.Length != 53
            || !Guid.TryParseExact(branch[21..], "N", out _) || HasReparseDirectoryComponent(destination)
            || Directory.Exists(destination) || !snapshot.Reference.StartsWith("refs/omnicore/checkpoints/", StringComparison.Ordinal))
            throw new IOException("alternative_target_unsafe");
        var commit = (await RequiredGit(root, ["rev-parse", "--verify", snapshot.Reference + "^{commit}"], token)).Trim();
        if (commit != snapshot.Commit) throw new IOException("checkpoint_ref_changed");
        try { await RequiredGit(root, ["worktree", "add", "-b", branch, "--", destination, commit], token); }
        catch
        {
            // A failed Git add can create its branch before materializing the directory.
            var owned = await Git(root, ["rev-parse", "--verify", "refs/heads/" + branch], null, CancellationToken.None);
            if (owned.Success && owned.Output.Trim() == commit)
                await RemoveCheckpointWorktreeAsync(root, destination, branch);
            throw;
        }
    }

    public async Task RemoveCheckpointWorktreeAsync(string root, string destination, string branch)
    {
        if (!branch.StartsWith("omnicore/alternative-", StringComparison.Ordinal) || branch.Length != 53
            || !Guid.TryParseExact(branch[21..], "N", out _) || HasReparseDirectoryComponent(destination)
            || IsWithinOrEqual(root, destination)) throw new IOException("alternative_cleanup_unsafe");
        if (Directory.Exists(destination))
        {
            var state = await Git(destination, ["status", "--porcelain", "--untracked-files=all"], null, CancellationToken.None);
            if (!state.Success || state.Output.Length > 0) throw new IOException("alternative_orphan_changed");
        }
        var result = await Git(root, ["worktree", "remove", "--force", "--", destination], null, CancellationToken.None);
        if (!result.Success && Directory.Exists(destination)) throw new IOException("alternative_cleanup_failed");
        var exists = await Git(root, ["show-ref", "--verify", "refs/heads/" + branch], null, CancellationToken.None);
        if (exists.Success) await RequiredGit(root, ["branch", "-D", "--", branch], CancellationToken.None);
    }
}
