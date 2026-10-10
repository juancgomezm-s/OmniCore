using System.Diagnostics;
using System.Collections.Concurrent;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class GitWorktreeStoreTests
{
    [Fact]
    public async Task Snapshot_captures_working_tree_without_changing_real_index_or_copying_ignored_secret()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.Write(".gitignore", ".env\n");
        fixture.CommitAll("base");
        fixture.Write("tracked.txt", "staged\n");
        fixture.Git("add", "tracked.txt");
        fixture.Write("tracked.txt", "working tree\n");
        fixture.Write("new file with spaces.txt", "untracked\n");
        fixture.Write(".env", "TEST_SECRET=do-not-copy\n");

        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var trackedBefore = File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt"));
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var outcome = await store.CreateAsync(fixture.Request(), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error?.ToString());
        var worktree = Assert.IsType<WorktreeIdentity>(outcome.Worktree);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(fixture.Git("write-tree").Trim(), worktree.UserIndexTree);
        Assert.Equal(trackedBefore, File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt")));
        Assert.Equal("working tree\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "tracked.txt")));
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "new file with spaces.txt")));
        Assert.False(File.Exists(Path.Combine(worktree.WorktreePath, ".env")));
        Assert.Equal(worktree.SnapshotCommit, fixture.Git("rev-parse", worktree.SnapshotRef).Trim());

        var cleanup = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);
        Assert.True(cleanup.Removed);
        Assert.False(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(worktree.SnapshotCommit, fixture.Git("rev-parse", worktree.SnapshotRef).Trim());
    }

    [Fact]
    public async Task Head_only_secret_is_rejected_even_after_it_was_deleted_from_index_and_worktree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("credentials.json", "private fixture\n");
        fixture.CommitAll("secret path in head");
        fixture.Git("rm", "--quiet", "credentials.json");
        var result = await new GitWorktreeStore(new SystemProcessRuntime()).CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.SecretPathPresent, result.Error);
    }

    [Fact]
    public async Task Refuses_equal_or_nested_data_roots_before_creating_anything_and_accepts_value_equal_workspace_ids()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());

        var equalRoot = await store.CreateAsync(fixture.Request() with { WorkspaceDataRoot = fixture.Repository }, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.PathOutsideDataRoot, equalRoot.Error);
        var nestedPath = Path.Combine(fixture.Repository, "must-not-be-created");
        var nestedRoot = await store.CreateAsync(fixture.Request() with { WorkspaceDataRoot = nestedPath }, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.PathOutsideDataRoot, nestedRoot.Error);
        Assert.False(Directory.Exists(nestedPath));

        // Parse constructs a distinct WorkspaceId instance; equality is value based in the contract.
        var sameId = WorkspaceId.Parse(fixture.Request().WorkspaceId.ToString());
        var created = await store.CreateAsync(fixture.Request() with { WorkspaceId = sameId }, CancellationToken.None);
        Assert.True(created.Succeeded, created.Error?.ToString());
    }

    [Fact]
    public async Task Filter_marker_is_rejected_without_executing_configured_filter()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        var marker = Path.Combine(fixture.Root, "filter-ran.marker");
        fixture.Write(".gitattributes", "*.payload filter=marker\n");
        fixture.Write("sample.payload", "payload\n");
        fixture.CommitAll("filter fixture");
        fixture.Git("config", "filter.marker.clean", $"cmd /c echo ran > \"{marker}\"");

        var result = await new GitWorktreeStore(new SystemProcessRuntime()).CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.UnsupportedFilter, result.Error);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Cleanup_rejects_tampered_common_directory_without_removing_worktree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var outcome = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(outcome.Worktree);
        var metadataText = File.ReadAllText(worktree.OwnershipMetadataPath);
        var alteredCommon = Path.Combine(fixture.Root, "foreign-git-common");
        File.WriteAllText(worktree.OwnershipMetadataPath, metadataText.Replace(worktree.GitCommonDirectory, alteredCommon, StringComparison.Ordinal));

        var cleanup = await store.CleanupAsync(worktree.Ownership with { GitCommonDirectory = alteredCommon }, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.OwnershipMismatch, cleanup.Error);
        Assert.True(Directory.Exists(worktree.WorktreePath));
    }

    [Fact]
    public async Task Secret_path_and_dirty_index_conflicts_fail_with_typed_codes_without_touching_index()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        fixture.Write("credentials.json", "private fixture\n");
        var originalIndex = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var store = new GitWorktreeStore(new SystemProcessRuntime());

        var secret = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.SecretPathPresent, secret.Error);
        Assert.Equal(originalIndex, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));

        File.Delete(Path.Combine(fixture.Repository, "credentials.json"));
        fixture.Write("tracked.txt", "staged\n");
        fixture.Git("add", "tracked.txt");
        fixture.Git("update-index", "--add", "--cacheinfo", $"160000,{fixture.Git("rev-parse", "HEAD").Trim()},module");
        var submodule = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.UnsupportedSubmoduleState, submodule.Error);
    }

    [Fact]
    public async Task Inspect_is_read_only_and_reports_no_repository_or_missing_head_as_typed_errors()
    {
        using var fixture = new GitFixture();
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        Assert.Equal(GitWorktreeErrorCode.NoRepository,
            (await store.InspectAsync(Path.Combine(fixture.Root, "missing"), CancellationToken.None)).ErrorCode);

        fixture.Init();
        Assert.Equal(GitWorktreeErrorCode.MissingHead,
            (await store.InspectAsync(fixture.Repository, CancellationToken.None)).ErrorCode);
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var beforeIndex = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var beforeHead = fixture.Git("rev-parse", "HEAD").Trim();
        var inspected = await store.InspectAsync(fixture.Repository, CancellationToken.None);
        Assert.True(inspected.Succeeded);
        Assert.Equal(beforeHead, inspected.Identity!.HeadCommit);
        Assert.Equal(beforeIndex, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
    }

    [Fact]
    public async Task Unsupported_options_are_typed_and_cleanup_preserves_dirty_lane_files()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());

        var patchOverlay = await store.CreateAsync(fixture.Request() with { Base = WorktreeBase.PatchOverlay }, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.UnsupportedBase, patchOverlay.Error);
        var allowlist = await store.CreateAsync(fixture.Request() with
        {
            IgnoredFiles = new IgnoredFilesPolicy.AllowlistPolicy(["bin/**"]),
        }, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.UnsupportedIgnoredFilesPolicy, allowlist.Error);

        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "tracked.txt"), "lane edit\n");
        var cleanup = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, cleanup.Error);
        Assert.Equal("lane edit\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "tracked.txt")));
    }

    [Fact]
    public async Task Cancellation_before_git_work_preserves_the_repository()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        fixture.Write("tracked.txt", "modified\n");
        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitWorktreeStore(new SystemProcessRuntime()).CreateAsync(fixture.Request(), cancellation.Token));

        Assert.Equal("modified\n", File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
    }

    [Fact]
    public async Task Runtime_cancellation_cancels_the_spawned_git_process_tree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var runtime = new ControlledRuntime(blockOnFirstLsFiles: true);
        using var cancellation = new CancellationTokenSource();
        var createTask = new GitWorktreeStore(runtime).CreateAsync(fixture.Request(), cancellation.Token);
        await runtime.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createTask);
        Assert.True(runtime.CancelCount > 0);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt")));
    }

    [Fact]
    public async Task Runtime_timeout_cancels_the_spawned_git_process_tree_and_returns_typed_failure()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var runtime = new ControlledRuntime(timeoutOnFirstLsFiles: true);

        var result = await new GitWorktreeStore(runtime).CreateAsync(fixture.Request(), CancellationToken.None);

        Assert.Equal(GitWorktreeErrorCode.GitCommandFailed, result.Error);
        Assert.True(runtime.CancelCount > 0);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt")));
    }

    [Fact]
    public async Task Unmerged_index_is_a_typed_conflict()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("conflict.txt", "base\n");
        fixture.CommitAll("base");
        fixture.Git("checkout", "-b", "other");
        fixture.Write("conflict.txt", "other\n");
        fixture.CommitAll("other");
        fixture.Git("checkout", "main", "--quiet");
        fixture.Write("conflict.txt", "main\n");
        fixture.CommitAll("main");
        fixture.GitExpectFailure("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "merge", "other");
        Assert.NotEmpty(fixture.Git("ls-files", "--unmerged"));

        var result = await new GitWorktreeStore(new SystemProcessRuntime()).CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.Conflict, result.Error);
    }

    [Fact]
    public async Task Cleanup_preserves_clean_worktree_with_new_lane_commits()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "tracked.txt"), "committed lane edit\n");
        fixture.Git("-C", worktree.WorktreePath, "add", "tracked.txt");
        fixture.Git("-C", worktree.WorktreePath, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "lane result");
        var laneHead = fixture.Git("-C", worktree.WorktreePath, "rev-parse", "HEAD").Trim();
        Assert.NotEqual(worktree.SnapshotCommit, laneHead);
        Assert.Empty(fixture.Git("-C", worktree.WorktreePath, "status", "--porcelain"));

        var cleanup = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);

        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, cleanup.Error);
        Assert.True(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(laneHead, fixture.Git("-C", worktree.WorktreePath, "rev-parse", "HEAD").Trim());
        Assert.Equal("committed lane edit\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "tracked.txt")));
        Assert.Equal(worktree.SnapshotCommit, fixture.Git("rev-parse", worktree.SnapshotRef).Trim());
    }

    [Fact]
    public async Task Untracked_nested_repository_is_rejected_before_retaining_snapshot_ref()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var nested = Path.Combine(fixture.Repository, "nested");
        Directory.CreateDirectory(nested);
        fixture.Git("-C", nested, "init", "--quiet", "--template=");
        File.WriteAllText(Path.Combine(nested, "nested.txt"), "nested content\n");
        fixture.Git("-C", nested, "add", "nested.txt");
        fixture.Git("-C", nested, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "nested base");
        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));

        var result = await new GitWorktreeStore(new SystemProcessRuntime()).CreateAsync(fixture.Request(), CancellationToken.None);

        Assert.Equal(GitWorktreeErrorCode.UnsupportedSubmoduleState, result.Error);
        Assert.Empty(fixture.Git("for-each-ref", "refs/omnicore/snapshots"));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal("nested content\n", File.ReadAllText(Path.Combine(nested, "nested.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".git", "worktrees")));
    }

    private sealed class GitFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-m7-git", Guid.NewGuid().ToString("N"));
        public string Repository => Path.Combine(Root, "repo with spaces");
        public string Data => Path.Combine(Root, "user data");

        public GitFixture()
        {
            Directory.CreateDirectory(Repository);
            Directory.CreateDirectory(Data);
        }

        public void Init()
        {
            Git("init", "--quiet", "--initial-branch=main");
            Git("config", "core.autocrlf", "false");
        }

        public void CommitAll(string message)
        {
            Git("add", "-A");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message);
        }

        public void Write(string relativePath, string text)
        {
            var path = Path.Combine(Repository, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public WorktreeCreateRequest Request() => new(WorkspaceId.Of(Repository), LaneId.New(), Repository, Data);

        public string Git(params string[] args) => RunGit(false, args);
        public string GitExpectFailure(params string[] args) => RunGit(true, args);

        private string RunGit(bool expectFailure, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Repository, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("No se inició Git.");
            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if ((process.ExitCode != 0) != expectFailure)
                throw new InvalidOperationException("Git fixture terminó con estado inesperado.");
            return stdout;
        }

        public void Dispose()
        {
            M7FixtureCleanup.DeleteOwnedTempDirectory(Root);
        }
    }

    private sealed class ControlledRuntime : IProcessRuntime
    {
        private readonly SystemProcessRuntime _inner = new();
        private readonly bool _blockOnFirstLsFiles;
        private readonly bool _timeoutOnFirstLsFiles;
        private readonly ConcurrentDictionary<int, ProcessLaunch> _launches = new();
        private int _triggered;
        private int _cancelCount;
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelCount => Volatile.Read(ref _cancelCount);

        public ControlledRuntime(bool blockOnFirstLsFiles = false, bool timeoutOnFirstLsFiles = false)
        {
            _blockOnFirstLsFiles = blockOnFirstLsFiles;
            _timeoutOnFirstLsFiles = timeoutOnFirstLsFiles;
        }

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            var handle = _inner.Launch(launch, cancellationToken);
            _launches[handle.Pid] = launch;
            return handle;
        }

        public void CancelTree(ProcessHandle handle)
        {
            Interlocked.Increment(ref _cancelCount);
            _inner.CancelTree(handle);
        }

        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // The first command containing ls-files is the snapshot discovery step, after read-only
            // inspection has succeeded. Keep the child alive here to exercise the runtime contract.
            var isLsFiles = _launches.TryGetValue(handle.Pid, out var launch)
                && launch.Args.Any(static arg => arg == "ls-files");
            var shouldTrigger = isLsFiles && Interlocked.CompareExchange(ref _triggered, 1, 0) == 0;
            if (shouldTrigger && _blockOnFirstLsFiles)
            {
                WaitStarted.TrySetResult();
                cancellationToken.WaitHandle.WaitOne();
                throw new OperationCanceledException(cancellationToken);
            }
            if (shouldTrigger && _timeoutOnFirstLsFiles)
                return new ProcessResult(0, null, null, timedOut: true);
            return _inner.Wait(handle, timeout, cancellationToken);
        }

        public bool IsAlive(ProcessHandle handle) => _inner.IsAlive(handle);
    }
}

internal static class M7FixtureCleanup
{
    public static void DeleteOwnedTempDirectory(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup escaped its temporary directory.");
        if (!Directory.Exists(full)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        // Git objects are read-only on Windows. Only clear that bit in this owned fixture tree.
        foreach (var file in Directory.EnumerateFiles(full, "*", options))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, recursive: true);
    }
}
