using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Host;
using OmniCore.Security;
using OmniCore.Tools;
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
    public async Task Preview_merges_lane_edits_against_snapshot_and_preserves_workspace_state_and_index()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("common.txt", "left base\ncontext one\ncontext two\ncontext three\nright base\n");
        fixture.Write("delete-me.txt", "remove in lane\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);

        // Later workspace changes remain ours; lane's non-overlapping edit and addition are proposed.
        fixture.Write("common.txt", "left user\ncontext one\ncontext two\ncontext three\nright base\n");
        fixture.Git("add", "common.txt");
        fixture.Write("user-only.txt", "user file\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "common.txt"), "left base\ncontext one\ncontext two\ncontext three\nright lane\n");
        File.Delete(Path.Combine(worktree.WorktreePath, "delete-me.txt"));
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "lane-added.txt"), "lane addition λ\n", new UTF8Encoding(false));

        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var head = fixture.Git("rev-parse", "HEAD").Trim();
        var refs = fixture.Git("for-each-ref", "--format=%(refname) %(objectname)");
        var objectCount = fixture.Git("count-objects", "-v");
        var workspaceCommon = File.ReadAllBytes(Path.Combine(fixture.Repository, "common.txt"));
        var workspaceOnly = File.ReadAllBytes(Path.Combine(fixture.Repository, "user-only.txt"));
        var laneCommon = File.ReadAllBytes(Path.Combine(worktree.WorktreePath, "common.txt"));
        var storeOutcome = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), CancellationToken.None);

        Assert.True(storeOutcome.Succeeded, storeOutcome.Error?.ToString());
        var preview = Assert.IsType<WorktreeIntegrationPreview>(storeOutcome.Preview);
        var repeated = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), CancellationToken.None);
        Assert.True(repeated.Succeeded, repeated.Error?.ToString());
        Assert.Equal(preview.ProposalId, repeated.Preview!.ProposalId);
        Assert.False(preview.HasConflicts);
        Assert.Contains(preview.Changes, change => change.RelativePath == "common.txt"
            && change.ExpectedPreSha256 == Sha256(workspaceCommon)
            && change.ExpectedPostSha256 == Sha256(Encoding.UTF8.GetBytes("left user\ncontext one\ncontext two\ncontext three\nright lane\n")));
        Assert.Contains(preview.Changes, change => change.RelativePath == "delete-me.txt"
            && change.ExpectedPreSha256 == Sha256(Encoding.UTF8.GetBytes("remove in lane\n"))
            && change.ExpectedPostSha256 is null);
        Assert.Contains(preview.Changes, change => change.RelativePath == "lane-added.txt"
            && change.ExpectedPreSha256 is null
            && change.ExpectedPostSha256 == Sha256(new UTF8Encoding(false).GetBytes("lane addition λ\n")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(refs, fixture.Git("for-each-ref", "--format=%(refname) %(objectname)"));
        Assert.Equal(objectCount, fixture.Git("count-objects", "-v"));
        Assert.Equal(workspaceCommon, File.ReadAllBytes(Path.Combine(fixture.Repository, "common.txt")));
        Assert.Equal(workspaceOnly, File.ReadAllBytes(Path.Combine(fixture.Repository, "user-only.txt")));
        Assert.Equal(laneCommon, File.ReadAllBytes(Path.Combine(worktree.WorktreePath, "common.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "lane-added.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "delete-me.txt")));
        Assert.Equal("remove in lane\n", File.ReadAllText(Path.Combine(fixture.Repository, "delete-me.txt")));

        var capture = await store.CaptureIntegrationForApplyAsync(new(worktree, preview.ProposalId,
            preview.Changes.Select(item => item.RelativePath).ToArray()), CancellationToken.None);
        Assert.True(capture.Succeeded, capture.Error?.ToString());
        Assert.Equal(preview.ProposalId, capture.Capture!.Preview.ProposalId);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(workspaceCommon, File.ReadAllBytes(Path.Combine(fixture.Repository, "common.txt")));
        var images = capture.Capture.Files.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        Assert.Equal(workspaceCommon, images["common.txt"].Preimage);
        Assert.Equal(Encoding.UTF8.GetBytes("left user\ncontext one\ncontext two\ncontext three\nright lane\n"),
            images["common.txt"].Postimage);
        Assert.Equal(Encoding.UTF8.GetBytes("remove in lane\n"), images["delete-me.txt"].Preimage);
        Assert.Null(images["delete-me.txt"].Postimage);
        Assert.Null(images["lane-added.txt"].Preimage);
        Assert.Equal(new UTF8Encoding(false).GetBytes("lane addition λ\n"), images["lane-added.txt"].Postimage);
    }

    [Fact]
    public async Task Apply_capture_rejects_a_stale_proposal_and_requires_exact_claimed_paths()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("common.txt", "base\ncontext\nend\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "common.txt"), "base\ncontext\nlane\n");
        var preview = Assert.IsType<WorktreeIntegrationPreview>(
            (await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None)).Preview);

        var omittedClaim = await store.CaptureIntegrationForApplyAsync(new(worktree, preview.ProposalId, []), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.OwnershipMismatch, omittedClaim.Error);

        fixture.Write("user-later.txt", "kept user edit\n");
        var changed = await store.CaptureIntegrationForApplyAsync(new(worktree, preview.ProposalId,
            preview.Changes.Select(item => item.RelativePath).ToArray()), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.WorkspaceChanged, changed.Error);
        Assert.Equal("kept user edit\n", File.ReadAllText(Path.Combine(fixture.Repository, "user-later.txt")));
        Assert.Equal("base\ncontext\nlane\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "common.txt")));
    }

    [Fact]
    public async Task Proposal_id_binds_real_head_and_branch_even_when_trees_are_identical()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("common.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "common.txt"), "lane edit\n");
        var first = Assert.IsType<WorktreeIntegrationPreview>(
            (await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None)).Preview);
        fixture.Git("checkout", "-b", "same-tree-new-branch");
        var second = Assert.IsType<WorktreeIntegrationPreview>(
            (await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None)).Preview);
        Assert.NotEqual(first.ProposalId, second.ProposalId);
        Assert.Equal(first.OursTree, second.OursTree);
        Assert.Equal(first.TheirsTree, second.TheirsTree);
    }

    [Fact]
    public async Task Preview_reports_same_region_conflicts_without_writing_either_worktree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("conflict.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        fixture.Write("conflict.txt", "ours\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "conflict.txt"), "theirs\n");
        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var head = fixture.Git("rev-parse", "HEAD").Trim();

        var result = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal("conflict.txt", Assert.Single(result.Preview!.Conflicts).RelativePath);
        Assert.Empty(result.Preview.Changes);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("ours\n", File.ReadAllText(Path.Combine(fixture.Repository, "conflict.txt")));
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "conflict.txt")));
    }

    [Fact]
    public async Task Preview_hashes_binary_lane_content_from_materialized_bytes()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        File.WriteAllBytes(Path.Combine(fixture.Repository, "asset.bin"), [0, 255, 1, 128, 13, 10]);
        fixture.CommitAll("binary base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var laneBytes = new byte[] { 0, 254, 1, 127, 13, 10, 0 };
        File.WriteAllBytes(Path.Combine(worktree.WorktreePath, "asset.bin"), laneBytes);

        var result = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.ToString());
        var change = Assert.Single(result.Preview!.Changes);
        Assert.Equal("asset.bin", change.RelativePath);
        Assert.Equal(Sha256(new byte[] { 0, 255, 1, 128, 13, 10 }), change.ExpectedPreSha256);
        Assert.Equal(Sha256(laneBytes), change.ExpectedPostSha256);
    }

    [Fact]
    public async Task Preview_rejects_lane_filter_before_git_add_can_execute_it()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var marker = Path.Combine(fixture.Root, "filter-ran.marker");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, ".gitattributes"), "*.payload filter=marker\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.payload"), "payload\n");
        fixture.Git("config", "filter.marker.clean", $"cmd /c echo ran > \"{marker}\"");

        var result = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), CancellationToken.None);

        Assert.Equal(GitWorktreeErrorCode.UnsupportedFilter, result.Error);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "sample.payload")));
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "tracked.txt")));
    }

    [Fact]
    public async Task Preview_rejects_identity_mismatch_and_honors_cancellation_without_mutation()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var incorrect = worktree with { GitCommonDirectory = Path.Combine(fixture.Root, "other-common") };
        var mismatch = await store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(incorrect), CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.OwnershipMismatch, mismatch.Error);

        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var head = fixture.Git("rev-parse", "HEAD").Trim();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.PreviewIntegrationAsync(new WorktreeIntegrationPreviewRequest(worktree), cancellation.Token));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(head, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "tracked.txt")));
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

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

    [Fact]
    public async Task Authorized_integration_applies_exact_batch_without_touching_git_index_or_head()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("edit.txt", "base one\ncontext\nbase end\n");
        fixture.Write("remove.txt", "remove me\n");
        fixture.Write("keep.txt", "user file\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "edit.txt"), "base one\ncontext\nlane end\n");
        File.Delete(Path.Combine(worktree.WorktreePath, "remove.txt"));
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "new.txt"), "added λ\n", new UTF8Encoding(false));
        fixture.Write("edit.txt", "user one\ncontext\nbase end\n");
        fixture.Git("add", "edit.txt");
        var originalIndex = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var originalHead = fixture.Git("rev-parse", "HEAD").Trim();
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var changes = preview.Preview!.Changes.Select(item => item.RelativePath).ToArray();
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var (tool, authorized, context, events) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview.ProposalId, changes);
        var spec = tool.DescribeReconciliation(authorized, context, CancellationToken.None);
        Assert.NotNull(spec?.BeforeStateRef);
        Assert.Equal(EffectClass.Reconcilable, authorized.Intent.Effect);

        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(EffectOutcome.Applied, result.EffectOutcome);
        Assert.Equal("user one\ncontext\nlane end\n", File.ReadAllText(Path.Combine(fixture.Repository, "edit.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "remove.txt")));
        Assert.Equal("added λ\n", File.ReadAllText(Path.Combine(fixture.Repository, "new.txt")));
        Assert.Equal("user file\n", File.ReadAllText(Path.Combine(fixture.Repository, "keep.txt")));
        Assert.Equal(originalIndex, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Equal(originalHead, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Contains(events, evt => evt is WorktreeIntegrationStateRecorded
            { State: WorktreeIntegrationState.Completed });
        Assert.True(artifacts.Verify(spec!.BeforeStateRef!.Hash, spec.BeforeStateRef.Size));

        // A directory at a deleted-file target is not the deleted postimage. It must not be
        // collapsed to the same null hash as a genuinely absent file during recovery.
        Directory.CreateDirectory(Path.Combine(fixture.Repository, "remove.txt"));
        var recovery = new WorktreeIntegrationAwareReconciler(fixture.Repository, artifacts,
            new FilesystemReconciler(new PathBoundaryValidator()), fixture.Data).Reconcile(
            fixture.Repository, spec.DurableMetadataJson!, spec.BeforeStateRef, CancellationToken.None);
        Assert.Equal(ReconciliationOutcome.Conflict, recovery.Outcome);
        Assert.Contains(Assert.IsType<WorktreeIntegrationStateRecorded>(recovery.SupplementalEvent).Files!,
            file => file.RelativePath == "remove.txt" && file.Outcome == ReconciliationOutcome.Conflict);
    }

    [Fact]
    public async Task Integration_batch_policy_preflight_rejects_excess_file_budget_before_barrier_or_artifact()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("a.txt", "a base\n");
        fixture.Write("b.txt", "b base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "a.txt"), "a lane\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "b.txt"), "b lane\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifactsPath = Path.Combine(fixture.Root, "artifacts");
        var artifacts = new FileArtifactStore(artifactsPath);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var registry = new FileReadRegistry(fixture.Repository);
        registry.Ledger.Bind(new FileMutationPolicy(FileMutationMode.Full, DestructiveActionPolicy.Allow,
            DestructiveActionPolicy.Allow, maxFilesPerTurn: 1, maxChangedLinesPerTurn: 100,
            maxRewriteRatio: 1.0, requirePriorRead: false, requireExpectedVersionToken: false,
            requirePostEditValidation: true, allowParallelMutations: false));
        var index = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var (tool, authorized, context, events) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId,
            preview.Preview.Changes.Select(change => change.RelativePath).ToArray(), registry);

        var error = Assert.Throws<ToolPreflightException>(() =>
            tool.DescribeReconciliation(authorized, context, CancellationToken.None));

        Assert.Equal(ToolErrorCode.LimitExceeded, error.ErrorCode);
        Assert.Equal("a base\n", File.ReadAllText(Path.Combine(fixture.Repository, "a.txt")));
        Assert.Equal("b base\n", File.ReadAllText(Path.Combine(fixture.Repository, "b.txt")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Empty(events);
        Assert.False(Directory.Exists(artifactsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Integration_recovery_is_idempotent_for_active_and_terminal_runs(bool terminal)
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("sample.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var (tool, authorized, context, executionEvents) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId,
            preview.Preview.Changes.Select(change => change.RelativePath).ToArray());
        var spec = tool.DescribeReconciliation(authorized, context, CancellationToken.None)!;

        // Simulate an exception after the final atomic file move. The integration code must
        // record a conservative Conflict/Unknown until restart classifies the complete post-set.
        coordinator.AfterWriteForTests = (_, _) => throw new IOException("crash after final file move");
        var interrupted = await tool.ExecuteAsync(authorized, context, CancellationToken.None);
        Assert.Equal(EffectOutcome.Unknown, interrupted.EffectOutcome);
        Assert.Equal("lane result\n", File.ReadAllText(Path.Combine(fixture.Repository, "sample.txt")));
        var session = SessionId.New();
        var journal = new InMemoryEventStore();
        var run = TestRun.Open(journal, session);
        var codecs = EventCodecs.Create();
        var stream = new EventStream(journal, codecs, session);
        var callId = authorized.Intent.ToolCallId;
        var statuses = preview.Preview.Changes.Select(change => new WorktreeIntegrationFileStatus(
            change.RelativePath, ReconciliationOutcome.NotApplied)).ToArray();
        stream.AppendBatch([
            new ToolCallRequested(callId, "fixture", "worktree.integrate", authorized.Intent.NormalizedArgumentsJson),
            new ToolCallPrepared(callId, "{}"),
            new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null),
            new ToolCallAuthorized(callId),
            new WorktreeIntegrationStateRecorded(callId, worktree.OwnershipId, preview.Preview.ProposalId,
                WorktreeIntegrationState.Started, statuses),
            new ToolCallStarted(callId, EffectClass.Reconcilable, spec.DurableMetadataJson)
            {
                BeforeStateRef = spec.BeforeStateRef,
                Reversibility = Reversibility.Unknown,
            },
        ], DurabilityClass.Barrier);
        var conflict = Assert.Single(executionEvents.OfType<WorktreeIntegrationStateRecorded>(), item =>
            item.State == WorktreeIntegrationState.Conflict);
        stream.AppendBatch([conflict, new ToolCallEffectUnknown(callId, EffectClass.Reconcilable)],
            DurabilityClass.Standard);
        if (terminal) new RunControlService(journal, codecs).CancelRun(session, run.RunId);

        var reconciler = new WorktreeIntegrationAwareReconciler(fixture.Repository, artifacts,
            new FilesystemReconciler(new PathBoundaryValidator()), fixture.Data);
        var resume = new RunResumeService(journal, codecs, reconciler, fixture.Repository);
        var first = terminal ? resume.ReconcileTerminalRuns(session) : resume.Resume(session, run.RunId);
        var second = terminal ? resume.ReconcileTerminalRuns(session) : resume.Resume(session, run.RunId);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var decoded = journal.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
        Assert.Single(decoded.OfType<WorktreeIntegrationStateRecorded>(), item =>
            item.State == WorktreeIntegrationState.Completed);
        Assert.Single(decoded.OfType<ToolCallReconciled>(), item => item.ToolCallId == callId);
        Assert.Equal(WorktreeIntegrationState.Completed,
            CanonicalStateTracker.Replay(codecs, journal.ReadFrom(session, 1)).WorktreeIntegration(callId));
        Assert.Equal("lane result\n", File.ReadAllText(Path.Combine(fixture.Repository, "sample.txt")));
    }

    [Fact]
    public async Task OmniServer_restart_recovers_integration_from_platform_workspace_data_root()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("sample.txt", "base\n");
        var durableRootIdentity = WorkspaceRootIdentity.Establish(fixture.Repository);
        fixture.CommitAll("base");

        var canonicalRoot = ProjectIdentity.CanonicalWorkspacePath(fixture.Repository);
        var workspaceId = WorkspaceId.Of(canonicalRoot);
        var platformData = Path.Combine(fixture.Root, "platform data");
        var dataRoot = Path.Combine(platformData, "workspaces", workspaceId.ToString());
        Directory.CreateDirectory(dataRoot);
        var request = new WorktreeCreateRequest(workspaceId, LaneId.New(), canonicalRoot, dataRoot);
        var git = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>(
            (await git.CreateAsync(request, CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n");
        var preview = await git.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());

        var artifactPath = Path.Combine(fixture.Root, "cas");
        var artifacts = new FileArtifactStore(artifactPath);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, dataRoot, artifacts, git);
        var (tool, authorized, context, _) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId,
            preview.Preview.Changes.Select(change => change.RelativePath).ToArray());
        var reconciliation = Assert.IsType<ReconciliationSpec>(
            tool.DescribeReconciliation(authorized, context, CancellationToken.None));

        var journalPath = Path.Combine(fixture.Root, "journal.db");
        var stateFile = Path.Combine(fixture.Root, "last-session.txt");
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var call = authorized.Intent.ToolCallId;
        var codecs = EventCodecs.Create();
        var initialStore = new SqliteEventStore(journalPath);
        try
        {
            var stream = new EventStream(initialStore, codecs, session);
            stream.Append(new SessionCreated(session, workspaceId.ToString(), fixture.Repository,
                ProfileId.New(), DateTimeOffset.UtcNow));
            stream.Append(new WorkspaceRootEstablished(session, canonicalRoot, DateTimeOffset.UtcNow,
                durableRootIdentity));
            stream.Append(new RunCreated(run, session, "recover integration", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                new TaskBudget(null, null, null, null), task, DateTimeOffset.UtcNow));
            stream.Append(new RunStarted(run));
            using (ExecutionScope.Begin(new ExecutionScopeState(run, task)))
            {
                stream.Append(new ToolCallRequested(call, "fixture", "worktree.integrate",
                    authorized.Intent.NormalizedArgumentsJson));
                stream.Append(new ToolCallPrepared(call, "{}"));
                stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
                stream.Append(new ToolCallAuthorized(call));
                var statuses = preview.Preview.Changes.Select(change => new WorktreeIntegrationFileStatus(
                    change.RelativePath, ReconciliationOutcome.NotApplied)).ToArray();
                stream.AppendBatch([
                    new WorktreeIntegrationStateRecorded(call, worktree.OwnershipId,
                        preview.Preview.ProposalId, WorktreeIntegrationState.Started, statuses),
                    new ToolCallStarted(call, EffectClass.Reconcilable, reconciliation.DurableMetadataJson)
                    {
                        BeforeStateRef = reconciliation.BeforeStateRef,
                        Reversibility = Reversibility.Unknown,
                    },
                ], DurabilityClass.Barrier);
                stream.Append(new ToolCallEffectUnknown(call, EffectClass.Reconcilable));
            }
            File.WriteAllText(stateFile, session + "\n" + run);
        }
        finally { initialStore.Close(); }
        File.WriteAllText(Path.Combine(fixture.Repository, "sample.txt"), "lane result\n");

        var oldDataDirectory = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, platformData);
            long afterFirstRecovery;
            var reopened = new SqliteEventStore(journalPath);
            try
            {
                var server = new OmniServer(reopened, codecs, new InMemoryAuditSink(), stateFile,
                    new FileArtifactStore(artifactPath));
                Assert.Null(server.LastRecoveryProblem());
                var payloads = reopened.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
                Assert.Single(payloads.OfType<ToolCallReconciled>(), item =>
                    item.Outcome == ReconciliationOutcome.Applied);
                Assert.Single(payloads.OfType<WorktreeIntegrationStateRecorded>(), item =>
                    item.ToolCallId == call && item.State == WorktreeIntegrationState.Completed);
                Assert.Equal("lane result\n", File.ReadAllText(Path.Combine(fixture.Repository, "sample.txt")));
                afterFirstRecovery = reopened.CurrentSequence(session);
            }
            finally { reopened.Close(); }

            var reopenedAgain = new SqliteEventStore(journalPath);
            try
            {
            var secondServer = new OmniServer(reopenedAgain, codecs, new InMemoryAuditSink(), stateFile,
                new FileArtifactStore(artifactPath));
            Assert.Null(secondServer.LastRecoveryProblem());
            Assert.Equal(afterFirstRecovery, reopenedAgain.CurrentSequence(session));
            }
            finally { reopenedAgain.Close(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, oldDataDirectory);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Integration_full_runtime_commits_validation_debt_and_entity_start_with_barrier()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("edit.txt", "base\n");
        fixture.Write("delete.txt", "delete\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "edit.txt"), "lane\n");
        File.Delete(Path.Combine(worktree.WorktreePath, "delete.txt"));
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "new.txt"), "new\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var boundary = CreateIntegrationBoundary(requirePostEditValidation: true);
        var executor = CreateExecutor(fixture.Repository, artifacts, coordinator, boundary,
            PermissionDecision.Allow, includeLease: true);
        var journal = new RecordingEventStore(new InMemoryEventStore());
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview!.ProposalId,
            paths = preview.Preview.Changes.Select(change => change.RelativePath).ToArray(),
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("worktree.integrate"), "pipeline", args);
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane));

        var outcome = executor.ExecuteTool(call, userApprovesAsk: false, CancellationToken.None, stream);

        Assert.True(outcome.Succeeded, outcome.Summary);
        Assert.Equal("lane\n", File.ReadAllText(Path.Combine(fixture.Repository, "edit.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "delete.txt")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(fixture.Repository, "new.txt")));
        var barrier = Assert.Single(journal.Batches, batch => batch.Durability == DurabilityClass.Barrier);
        var codecs = EventCodecs.Create();
        var barrierPayloads = barrier.Events.Select(codecs.Decode).ToArray();
        Assert.Contains(barrierPayloads, item => item is WorktreeIntegrationStateRecorded
            { State: WorktreeIntegrationState.Started });
        Assert.Contains(barrierPayloads, item => item is PostEditValidationPending debt
            && debt.ToolCallId == call.ToolCallId && debt.Paths.Count == 3);
        Assert.Contains(barrierPayloads, item => item is ToolCallStarted start && start.ToolCallId == call.ToolCallId);
        Assert.Single(journal.ReadFrom(session, 1).Select(codecs.Decode)
            .OfType<WorktreeIntegrationStateRecorded>(), item => item.State == WorktreeIntegrationState.Completed);
        Assert.Equal(3, boundary.ReadRegistry().Ledger.PendingValidations().Count);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("ask")]
    [InlineData("no-lease")]
    public async Task Integration_permission_ask_and_missing_lease_stop_before_barrier_or_effect(string gate)
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("sample.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifactPath = Path.Combine(fixture.Root, "artifacts");
        var artifacts = new FileArtifactStore(artifactPath);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var boundary = gate == "ask"
            ? CreateIntegrationBoundary(delete: DestructiveActionPolicy.Ask)
            : CreateIntegrationBoundary();
        var decision = gate == "deny" ? PermissionDecision.Deny : PermissionDecision.Allow;
        var executor = CreateExecutor(fixture.Repository, artifacts, coordinator, boundary, decision,
            includeLease: gate != "no-lease");
        var journal = new RecordingEventStore(new InMemoryEventStore());
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview!.ProposalId,
            paths = preview.Preview.Changes.Select(change => change.RelativePath).ToArray(),
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("worktree.integrate"), "pipeline", args);
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane));

        var outcome = executor.ExecuteTool(call, userApprovesAsk: false, CancellationToken.None, stream);

        Assert.False(outcome.Succeeded);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "sample.txt")));
        Assert.DoesNotContain(journal.Batches, batch => batch.Durability == DurabilityClass.Barrier);
        Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(EventCodecs.Create().Decode), item =>
            item is ToolCallStarted || item is WorktreeIntegrationStateRecorded);
        if (gate is "deny" or "ask") Assert.False(Directory.Exists(artifactPath));
        if (gate == "no-lease") Assert.False(Directory.Exists(artifactPath));
    }

    [Fact]
    public async Task Integration_barrier_failure_never_writes_workspace_files()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("sample.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var boundary = CreateIntegrationBoundary(requirePostEditValidation: true);
        var executor = CreateExecutor(fixture.Repository, artifacts, coordinator, boundary,
            PermissionDecision.Allow, includeLease: true);
        var journal = new RecordingEventStore(new InMemoryEventStore(), failBarrier: true);
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview!.ProposalId,
            paths = preview.Preview.Changes.Select(change => change.RelativePath).ToArray(),
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("worktree.integrate"), "pipeline", args);
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane));

        Assert.Throws<IOException>(() => executor.ExecuteTool(call, false, CancellationToken.None, stream));

        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "sample.txt")));
        Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(EventCodecs.Create().Decode),
            item => item is ToolCallStarted);
        var attempted = Assert.Single(journal.Batches, batch => batch.Durability == DurabilityClass.Barrier);
        var attemptedPayloads = attempted.Events.Select(EventCodecs.Create().Decode).ToArray();
        Assert.Contains(attemptedPayloads, item => item is WorktreeIntegrationStateRecorded
            { State: WorktreeIntegrationState.Started });
        Assert.Contains(attemptedPayloads, item => item is PostEditValidationPending);
        Assert.Contains(attemptedPayloads, item => item is ToolCallStarted);
    }

    [Fact]
    public async Task Integration_rechecks_each_preimage_and_recovers_partial_external_edit_as_conflict()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("a.txt", "a base\n");
        fixture.Write("b.txt", "b base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "a.txt"), "a lane\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "b.txt"), "b lane\n");
        var originalIndex = File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index"));
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var (tool, authorized, context, events) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId, preview.Preview.Changes.Select(c => c.RelativePath).ToArray());
        var spec = tool.DescribeReconciliation(authorized, context, CancellationToken.None);
        Assert.NotNull(spec);
        coordinator.AfterWriteForTests = (index, _) =>
        {
            if (index == 0) File.WriteAllText(Path.Combine(fixture.Repository, "b.txt"), "later user edit\n");
        };

        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(EffectOutcome.Unknown, result.EffectOutcome);
        Assert.Equal("a lane\n", File.ReadAllText(Path.Combine(fixture.Repository, "a.txt")));
        Assert.Equal("later user edit\n", File.ReadAllText(Path.Combine(fixture.Repository, "b.txt")));
        Assert.Equal(originalIndex, File.ReadAllBytes(Path.Combine(fixture.Repository, ".git", "index")));
        Assert.Contains(events, evt => evt is WorktreeIntegrationStateRecorded
            { State: WorktreeIntegrationState.Conflict });

        var reconciler = new WorktreeIntegrationAwareReconciler(fixture.Repository, artifacts,
            new FilesystemReconciler(new PathBoundaryValidator()), fixture.Data);
        var recovery = reconciler.Reconcile(fixture.Repository, Assert.IsType<string>(spec!.DurableMetadataJson),
            spec.BeforeStateRef, CancellationToken.None);
        Assert.Equal(ReconciliationOutcome.Conflict, recovery.Outcome);
        var entity = Assert.IsType<WorktreeIntegrationStateRecorded>(recovery.SupplementalEvent);
        Assert.Contains(entity.Files!, file => file.RelativePath == "b.txt"
            && file.Outcome == ReconciliationOutcome.Conflict);
    }

    [Fact]
    public async Task Integration_detects_edit_to_already_written_file_before_completed_outcome()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("a.txt", "a base\n");
        fixture.Write("b.txt", "b base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "a.txt"), "a lane\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "b.txt"), "b lane\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var (tool, authorized, context, events) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId, preview.Preview.Changes.Select(c => c.RelativePath).ToArray());
        var spec = tool.DescribeReconciliation(authorized, context, CancellationToken.None);
        coordinator.AfterWriteForTests = (index, path) =>
        {
            if (index == 0) File.WriteAllText(Path.Combine(fixture.Repository, path), "later edit to first target\n");
        };

        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(EffectOutcome.Unknown, result.EffectOutcome);
        Assert.Equal("later edit to first target\n", File.ReadAllText(Path.Combine(fixture.Repository, "a.txt")));
        Assert.Contains(events, evt => evt is WorktreeIntegrationStateRecorded state
            && state.State == WorktreeIntegrationState.Conflict
            && state.Files!.Any(file => file.RelativePath == "a.txt" && file.Outcome == ReconciliationOutcome.Conflict));
        var recovered = new WorktreeIntegrationAwareReconciler(fixture.Repository, artifacts,
            new FilesystemReconciler(new PathBoundaryValidator()), fixture.Data).Reconcile(fixture.Repository,
            spec!.DurableMetadataJson!, spec.BeforeStateRef, CancellationToken.None);
        Assert.Equal(ReconciliationOutcome.Conflict, recovered.Outcome);
    }

    [Fact]
    public async Task Integration_create_does_not_overwrite_file_appearing_before_publication()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("base.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "new.txt"), "lane proposal\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var (tool, authorized, context, events) = MakeIntegrationCall(fixture.Repository, artifacts, coordinator,
            worktree.OwnershipId, preview.Preview!.ProposalId, ["new.txt"]);
        var spec = tool.DescribeReconciliation(authorized, context, CancellationToken.None);
        Assert.NotNull(spec);
        coordinator.BeforePublishForTests = (_, path) =>
            File.WriteAllText(Path.Combine(fixture.Repository, path), "external creation\n");

        var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(EffectOutcome.Unknown, result.EffectOutcome);
        Assert.Equal("external creation\n", File.ReadAllText(Path.Combine(fixture.Repository, "new.txt")));
        Assert.Contains(events, evt => evt is WorktreeIntegrationStateRecorded
            { State: WorktreeIntegrationState.Conflict });
        var reconciler = new WorktreeIntegrationAwareReconciler(fixture.Repository, artifacts,
            new FilesystemReconciler(new PathBoundaryValidator()), fixture.Data);
        var recovered = reconciler.Reconcile(fixture.Repository, spec!.DurableMetadataJson!,
            spec.BeforeStateRef, CancellationToken.None);
        Assert.Equal(ReconciliationOutcome.Conflict, recovered.Outcome);
    }

    private static (WorktreeIntegrationApplyTool Tool, AuthorizedToolIntent Authorized,
        ToolExecutionContext Context, List<DomainEventPayload> Events) MakeIntegrationCall(string repository, IArtifactStore artifacts,
        WorktreeIntegrationHostCoordinator coordinator, string ownershipId, string proposalId,
        string[] paths, FileReadRegistry? registry = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { ownershipId, proposalId, paths });
        var callId = ToolCallId.New();
        var tool = new WorktreeIntegrationApplyTool(new PathBoundaryValidator(), coordinator);
        var call = new ValidatedToolCall(callId, tool.Descriptor.Id, "integration-test", json);
        var prepared = Assert.IsType<Prepared>(tool.Prepare(call, new ToolPreparationContext(repository, DateTimeOffset.UtcNow)));
        var authorized = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).Authorize(prepared.Intent);
        var events = new List<DomainEventPayload>();
        var context = new ToolExecutionContext(repository, registry, events.Add, artifacts: artifacts);
        return (tool, authorized, context, events);
    }

    private static ModelCapabilityBoundary CreateIntegrationBoundary(
        bool requirePostEditValidation = false, DestructiveActionPolicy delete = DestructiveActionPolicy.Allow,
        int maxFiles = 8)
    {
        var key = ModelPolicyKey.For("m7-test", "fixture-model");
        var full = ModelPolicyPresets.FullAgent();
        var mutation = new FileMutationPolicy(FileMutationMode.Full, delete, DestructiveActionPolicy.Allow,
            maxFiles, 2000, 1.0, requirePriorRead: false, requireExpectedVersionToken: false,
            requirePostEditValidation, allowParallelMutations: false);
        var user = new UserModelPolicy(full.Category, full.ToolPolicy, mutation, full.Source, full.Note);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16,
            GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
        var effective = EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, user, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), harness);
        return new ModelCapabilityBoundary(effective);
    }

    private static ScriptedToolExecutor CreateExecutor(string workspace, IArtifactStore artifacts,
        WorktreeIntegrationHostCoordinator coordinator, ModelCapabilityBoundary boundary,
        PermissionDecision permission, bool includeLease)
    {
        var tool = new WorktreeIntegrationApplyTool(new PathBoundaryValidator(), coordinator);
        var catalog = new FakeCatalog().Add(tool);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["worktree.integrate"] = permission,
        });
        return new ScriptedToolExecutor(catalog, policy, workspace, boundary, null, null, false,
            artifacts: artifacts,
            workspaceWriteLeases: includeLease ? HostWorkspaceWriteLeases.Shared : null);
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

    private sealed record RecordedBatch(DurabilityClass Durability, IReadOnlyList<DomainEvent> Events);

    private sealed class RecordingEventStore(IEventStore inner, bool failBarrier = false) : IEventStore
    {
        public List<RecordedBatch> Batches { get; } = new();
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, [evt], durability, cancellationToken);
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            Batches.Add(new RecordedBatch(durability, evts.ToArray()));
            if (failBarrier && durability == DurabilityClass.Barrier) throw new IOException("fixture barrier failure");
            inner.AppendBatch(sessionId, evts, durability, cancellationToken);
        }
        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);
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
