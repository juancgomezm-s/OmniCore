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
    private const string MutationHistoryPhaseVariable = "OMNICORE_M9_HISTORY_PHASE";
    private const string MutationHistoryRootVariable = "OMNICORE_M9_HISTORY_ROOT";
    private const string MutationFencePhaseVariable = "OMNICORE_M9_FENCE_PHASE";

    [Fact]
    public void Closing_legacy_event_store_releases_database_while_history_object_remains_alive()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-history-lifetime-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var databasePath = Path.Combine(data, "journal.db");
        SqliteWorkspaceMutationHistoryStore? history = null;
        try
        {
            history = new SqliteWorkspaceMutationHistoryStore(data);
            Assert.Empty(history.ReadPending(Path.Combine(root, "workspace")));

            var legacyStore = SqliteEventStore.OpenUnpooled(databasePath);
            legacyStore.Close();

            using (var exclusive = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                exclusive.Flush(flushToDisk: true);
            File.Delete(databasePath);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            history?.Dispose();
            M7FixtureCleanup.DeleteOwnedTempDirectory(root);
        }
    }

    [Fact]
    public void Recovery_does_not_apply_a_delete_receipt_without_a_faithful_preimage()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-missing-preimage-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        var toolCallId = ToolCallId.New();
        var preHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("before\n")));
        try
        {
            var artifacts = new FileArtifactStore(data);
            var preimage = FilesystemPreimage.CaptureHistoryImage(artifacts, Encoding.UTF8.GetBytes("before\n"));
            using (var firstHost = new SqliteWorkspaceMutationHistoryStore(data))
            {
                var receipt = new WorkspaceMutationReceipt(firstHost.GetWorkspaceId(workspace),
                    "deleted.txt", toolCallId, "run-1", "turn-1", "DeleteFile", preHash, null,
                    preimage, null, 1, 0, WorkspaceMutationReceiptState.Prepared);
                firstHost.Prepare([receipt], CancellationToken.None);
            }

            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(data, "journal.db"), Pooling = false }.ToString();
            using (var corrupt = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                corrupt.Open();
                using var command = corrupt.CreateCommand();
                command.CommandText = "UPDATE workspace_mutation_receipts SET preimage_ref=NULL WHERE tool_call_id=:call";
                command.Parameters.AddWithValue("call", toolCallId.ToString());
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            using var reopenedHost = new SqliteWorkspaceMutationHistoryStore(data);
            reopenedHost.RecoverPending(workspace, CancellationToken.None);

            Assert.NotEqual(WorkspaceMutationReceiptState.Applied,
                reopenedHost.ReadReceipt(workspace, toolCallId, "deleted.txt")?.State);
            Assert.NotEqual(WorkspaceFileHistoryState.Tombstone,
                reopenedHost.ReadFile(workspace, "deleted.txt")?.State);

            var orphan = artifacts.PutText("old orphan", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            var orphanPath = Path.Combine(data, "blobs", "sha256", orphan.Hash.Value[..2], orphan.Hash.Value.Substring(2, 2), orphan.Hash.Value);
            File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-2));
            Assert.Throws<InvalidDataException>(() => new ArtifactGc(data).Sweep(null, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(File.Exists(orphanPath), "GC swept an orphan even though the durable history root was corrupt.");
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopened_recovery_resolves_prepared_delete_on_either_side_of_the_effect(bool deleteBeforeRestart)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-recovery-cut-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "cut.txt");
        var content = Encoding.UTF8.GetBytes("before\n");
        File.WriteAllBytes(target, content);
        var preHash = Convert.ToHexStringLower(SHA256.HashData(content));
        try
        {
            var artifacts = new FileArtifactStore(data);
            var callId = ToolCallId.New();
            using (var firstHost = new SqliteWorkspaceMutationHistoryStore(data))
            {
                firstHost.RecordObservation(workspace, "cut.txt", preHash, CancellationToken.None);
                var receipt = new WorkspaceMutationReceipt(firstHost.GetWorkspaceId(workspace), "cut.txt", callId,
                    "run-cut", "turn-cut", "DeleteFile", preHash, null,
                    FilesystemPreimage.CaptureHistoryImage(artifacts, content), null, 1, 0,
                    WorkspaceMutationReceiptState.Prepared);
                firstHost.Prepare([receipt], CancellationToken.None);
                if (deleteBeforeRestart) File.Delete(target);
            }

            using var reopenedHost = new SqliteWorkspaceMutationHistoryStore(data);
            reopenedHost.RecoverPending(workspace, CancellationToken.None);

            var resolved = reopenedHost.ReadReceipt(workspace, callId, "cut.txt");
            Assert.Equal(deleteBeforeRestart ? WorkspaceMutationReceiptState.Applied : WorkspaceMutationReceiptState.NotApplied,
                resolved?.State);
            Assert.Equal(!deleteBeforeRestart, File.Exists(target));
            var budget = reopenedHost.ReadBudgetChargesForRun(workspace, "run-cut");
            if (deleteBeforeRestart)
                Assert.Contains(budget, receipt => receipt.ToolCallId == callId
                    && receipt.State == WorkspaceMutationReceiptState.Applied);
            else
                Assert.DoesNotContain(budget, receipt => receipt.ToolCallId == callId);
            if (deleteBeforeRestart)
            {
                var tombstone = reopenedHost.ReadFile(workspace, "cut.txt");
                Assert.Equal(WorkspaceFileHistoryState.Tombstone, tombstone?.State);
                Assert.NotNull(FilesystemPreimage.Read(artifacts, tombstone!.ImageRef!));
            }
            else
            {
                Assert.Equal("before\n", File.ReadAllText(target));
                Assert.Equal(WorkspaceFileHistoryState.Present, reopenedHost.ReadFile(workspace, "cut.txt")?.State);
            }
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Fact]
    public void Run_budget_replay_includes_durable_unknown_receipts_from_other_turns()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-unknown-budget-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        try
        {
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            var callId = ToolCallId.New();
            var uncertainBytes = Encoding.UTF8.GetBytes("uncertain");
            var hash = Convert.ToHexStringLower(SHA256.HashData(uncertainBytes));
            var preimage = FilesystemPreimage.CaptureHistoryImage(new FileArtifactStore(data), uncertainBytes);
            var prepared = new WorkspaceMutationReceipt(history.GetWorkspaceId(workspace), "changed.txt", callId,
                "run-shared", "turn-other-lane", "ReplaceFile", hash, null, preimage, null, 2, 1,
                WorkspaceMutationReceiptState.Prepared);
            history.Prepare([prepared], CancellationToken.None);
            Assert.DoesNotContain(history.ReadBudgetChargesForRun(workspace, "run-shared"),
                receipt => receipt.ToolCallId == callId);
            history.Complete([prepared with { State = WorkspaceMutationReceiptState.Unknown }],
                [new WorkspaceFileHistoryEntry(prepared.WorkspaceId, prepared.RelativePath,
                    WorkspaceFileHistoryState.Unknown, hash, null, 1, callId)], CancellationToken.None);

            var durable = history.ReadBudgetChargesForRun(workspace, "run-shared");

            Assert.Contains(durable, receipt => receipt.ToolCallId == callId
                && receipt.State == WorkspaceMutationReceiptState.Unknown
                && receipt.TurnId == "turn-other-lane");
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Fact]
    public void Unknown_receipt_from_another_turn_blocks_a_fresh_tool_runtime_before_create()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-runtime-unknown-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        try
        {
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            var journal = new RecordingEventStore(new InMemoryEventStore());
            var session = SessionId.New();
            var run = TestRun.Open(journal, session);
            var oldCall = ToolCallId.New();
            var uncertainBytes = Encoding.UTF8.GetBytes("uncertain");
            var unknownHash = Convert.ToHexStringLower(SHA256.HashData(uncertainBytes));
            var artifacts = new FileArtifactStore(data);
            var preimage = FilesystemPreimage.CaptureHistoryImage(artifacts, uncertainBytes);
            var prepared = new WorkspaceMutationReceipt(history.GetWorkspaceId(workspace), "old.txt", oldCall,
                run.RunId.ToString(), TurnId.New().ToString(), "PatchExisting", unknownHash, null, preimage, null,
                2, 1, WorkspaceMutationReceiptState.Prepared);
            history.Prepare([prepared], CancellationToken.None);
            history.Complete([prepared with { State = WorkspaceMutationReceiptState.Unknown }],
                [new WorkspaceFileHistoryEntry(prepared.WorkspaceId, prepared.RelativePath,
                    WorkspaceFileHistoryState.Unknown, unknownHash, null, 1, oldCall)], CancellationToken.None);

            var tool = new FilesystemWriteTool(new PathBoundaryValidator());
            var catalog = new FakeCatalog().Add(tool);
            var permissions = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { ["filesystem.write"] = PermissionDecision.Allow });
            var boundary = CreateIntegrationBoundary(requirePriorRead: false);
            var executor = new ScriptedToolExecutor(catalog, permissions, workspace, boundary, null, null, false,
                artifacts: artifacts, workspaceWriteLeases: HostWorkspaceWriteLeases.Shared,
                mutationHistory: history);
            Assert.NotNull(boundary.ReadRegistry().Ledger.MutationPolicy);
            Assert.Contains(history.ReadBudgetChargesForRun(workspace, run.RunId.ToString()),
                receipt => receipt.ToolCallId == oldCall && receipt.State == WorkspaceMutationReceiptState.Unknown);
            var currentTurn = TurnId.New();
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, currentTurn));
            var stream = new EventStream(journal, EventCodecs.Create(), session);
            var call = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "create",
                "{\"path\":\"new.txt\",\"content\":\"new content\\n\"}");

            var outcome = executor.ExecuteTool(call, false, CancellationToken.None, stream);

            Assert.True(boundary.ReadRegistry().Ledger.RunBudgetIsUnknown, outcome.Summary);
            Assert.False(outcome.Succeeded);
            Assert.Null(outcome.Effect);
            Assert.False(File.Exists(Path.Combine(workspace, "new.txt")));
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Fact]
    public void Workspace_history_normalizes_root_aliases_but_isolates_physical_child_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-root-identity-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "child-worktree");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(child);
        var bytes = Encoding.UTF8.GetBytes("root content\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var file = Path.Combine(root, "same.txt");
        File.WriteAllBytes(file, bytes);
        try
        {
            var artifacts = new FileArtifactStore(data);
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            var alias = Path.Combine(root, ".", "sub", "..", "");
            Assert.Equal(history.GetWorkspaceId(root), history.GetWorkspaceId(alias));
            Assert.NotEqual(history.GetWorkspaceId(root), history.GetWorkspaceId(child));
            history.RecordObservation(root, "same.txt", hash, CancellationToken.None);
            var callId = ToolCallId.New();
            var receipt = new WorkspaceMutationReceipt(history.GetWorkspaceId(root), "same.txt", callId,
                "run", "turn", "DeleteFile", hash, null, FilesystemPreimage.CaptureHistoryImage(artifacts, bytes), null,
                1, 0, WorkspaceMutationReceiptState.Prepared);
            history.Prepare([receipt], CancellationToken.None);
            File.Delete(file);
            history.Complete([receipt with { State = WorkspaceMutationReceiptState.Applied }],
                [new WorkspaceFileHistoryEntry(receipt.WorkspaceId, receipt.RelativePath,
                    WorkspaceFileHistoryState.Tombstone, hash, receipt.PreimageRef, 2, callId)], CancellationToken.None);

            Assert.Equal(WorkspaceFileHistoryState.Tombstone, history.ReadFile(alias, "same.txt")?.State);
            Assert.Null(history.ReadFile(child, "same.txt"));
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Theory]
    [InlineData("no-read")]
    [InlineData("stale-token")]
    [InlineData("partial-read")]
    [InlineData("replace-cap-denied")]
    [InlineData("create-cap-denied")]
    [InlineData("deny-patch")]
    [InlineData("ask-patch")]
    public void Tombstone_recreation_rejects_missing_proof_stale_versions_and_authorization_denials(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-tombstone-negative-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        var bytes = Encoding.UTF8.GetBytes("before\nsecond\n");
        var preHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var target = Path.Combine(workspace, "target.txt");
        File.WriteAllBytes(target, bytes);
        try
        {
            var artifacts = new FileArtifactStore(data);
            using var history = new SqliteWorkspaceMutationHistoryStore(data);
            history.RecordObservation(workspace, "target.txt", preHash, CancellationToken.None);
            var deleteCall = ToolCallId.New();
            var deleteReceipt = new WorkspaceMutationReceipt(history.GetWorkspaceId(workspace), "target.txt", deleteCall,
                "seed-run", "seed-turn", "DeleteFile", preHash, null,
                FilesystemPreimage.CaptureHistoryImage(artifacts, bytes), null, 1, 0,
                WorkspaceMutationReceiptState.Prepared);
            history.Prepare([deleteReceipt], CancellationToken.None);
            File.Delete(target);
            history.Complete([deleteReceipt with { State = WorkspaceMutationReceiptState.Applied }],
                [new WorkspaceFileHistoryEntry(deleteReceipt.WorkspaceId, deleteReceipt.RelativePath,
                    WorkspaceFileHistoryState.Tombstone, preHash, deleteReceipt.PreimageRef, 2, deleteCall)],
                CancellationToken.None);

            var mode = scenario == "replace-cap-denied" ? FileMutationMode.PatchAndCreate : FileMutationMode.Full;
            Func<SessionSecurityPolicySet?>? sessionPolicy = null;
            if (scenario == "create-cap-denied")
            {
                var full = SessionSecurityPolicySet.FromPreset(SessionId.New(), 1, ModelPolicyCategory.FullAgent,
                    SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserSelection, DateTimeOffset.UtcNow);
                var deniedCreate = full with
                { CapabilityCeiling = full.CapabilityCeiling.Where(item => item != ModelToolCapability.CreateFile).ToArray() };
                deniedCreate.Validate();
                sessionPolicy = () => deniedCreate;
            }
            var boundary = CreateIntegrationBoundary(requirePriorRead: true, mutationMode: mode,
                sessionSecurityPolicy: sessionPolicy);
            var read = new ReadFileTool(new PathBoundaryValidator());
            var write = new FilesystemWriteTool(new PathBoundaryValidator());
            var catalog = new FakeCatalog().Add(read).Add(write);
            var patchDecision = scenario == "deny-patch" ? PermissionDecision.Deny
                : scenario == "ask-patch" ? PermissionDecision.Ask : PermissionDecision.Allow;
            var permissions = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            {
                ["filesystem.read"] = PermissionDecision.Allow,
                ["filesystem.write"] = PermissionDecision.Allow,
                ["filesystem.patch"] = patchDecision,
            });
            var executor = new ScriptedToolExecutor(catalog, permissions, workspace, boundary, null, null, false,
                artifacts: artifacts, workspaceWriteLeases: HostWorkspaceWriteLeases.Shared, mutationHistory: history);
            var journal = new RecordingEventStore(new InMemoryEventStore());
            var session = SessionId.New();
            var run = TestRun.Open(journal, session);
            var turn = TurnId.New();
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn));
            var stream = new EventStream(journal, EventCodecs.Create(), session);
            if (scenario != "no-read")
            {
                var limit = scenario == "partial-read" ? 1 : 500;
                var readCall = new ValidatedToolCall(ToolCallId.New(), read.Descriptor.Id, "read",
                    "{\"path\":\"target.txt\",\"offset\":1,\"limit\":" + limit + "}");
                Assert.True(executor.ExecuteTool(readCall, false, CancellationToken.None, stream).Succeeded);
            }
            var version = scenario == "stale-token" ? new string('0', 64) : preHash;
            var call = new ValidatedToolCall(ToolCallId.New(), write.Descriptor.Id, "recreate",
                System.Text.Json.JsonSerializer.Serialize(new
                    { path = "target.txt", content = "after\nsecond\n", expectedVersion = version }));

            var outcome = executor.ExecuteTool(call, false, CancellationToken.None, stream);

            Assert.False(outcome.Succeeded, scenario + " unexpectedly recreated the deleted file.");
            Assert.False(File.Exists(target));
            Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(EventCodecs.Create().Decode),
                item => item is ToolCallStarted started && started.ToolCallId == call.ToolCallId);
            Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(EventCodecs.Create().Decode),
                item => item is ToolCallStarted started && started.ToolCallId == call.ToolCallId);
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(root); }
    }

    [Fact]
    public async Task Native_worktree_move_then_delete_survives_process_exit_and_source_can_be_recreated()
    {
        var phase = Environment.GetEnvironmentVariable(MutationHistoryPhaseVariable);
        if (phase is not null)
        {
            var root = Environment.GetEnvironmentVariable(MutationHistoryRootVariable)!;
            if (phase == "delete") await DeleteInChildProcess(root);
            else if (phase == "recreate") RecreateInChildProcess(root);
            else Assert.Fail("Unknown durable mutation history phase.");
            return;
        }

        var rootPath = Path.Combine(Path.GetTempPath(), "omnicore-m9-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            await RunHistoryProcess("delete", rootPath);
            var data = Path.Combine(rootPath, "user data");
            var artifactStore = new FileArtifactStore(data);
            var orphan = artifactStore.PutText("unreferenced aged blob", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            var orphanPath = Path.Combine(data, "blobs", "sha256", orphan.Hash.Value[..2], orphan.Hash.Value.Substring(2, 2), orphan.Hash.Value);
            File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-2));
            var gc = new ArtifactGc(data);
            var retained = gc.Sweep(null, TimeSpan.Zero, false, DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            Assert.True(retained.LiveReferenced > 0, "The durable tombstone CAS image was not marked live by GC.");
            Assert.True(retained.Deleted > 0, "GC did not remove an aged orphan alongside retained history roots.");
            Assert.False(File.Exists(orphanPath));
            await RunHistoryProcess("recreate", rootPath);
            var fixture = new GitFixture(rootPath);
            var source = Path.Combine(fixture.Repository, "source.txt");
            Assert.Equal("restored from protected history\n", File.ReadAllText(source));
            Assert.Equal("restored from protected history\n", File.ReadAllText(Path.Combine(fixture.Repository, "moved.txt")));
            using var history = new SqliteWorkspaceMutationHistoryStore(fixture.Data);
            Assert.Equal(WorkspaceFileHistoryState.Present, history.ReadFile(fixture.Repository, "source.txt")?.State);
        }
        finally { M7FixtureCleanup.DeleteOwnedTempDirectory(rootPath); }
    }

    [Fact]
    public async Task Workspace_mutation_fence_excludes_a_second_process_until_the_first_releases_it()
    {
        var phase = Environment.GetEnvironmentVariable(MutationFencePhaseVariable);
        if (phase is not null)
        {
            var root = Environment.GetEnvironmentVariable(MutationHistoryRootVariable)!;
            Assert.Equal("hold", phase);
            using var history = new SqliteWorkspaceMutationHistoryStore(Path.Combine(root, "data"));
            using var held = history.AcquireFence(Path.Combine(root, "workspace"), TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(root, "ready"), "held");
            var release = Path.Combine(root, "release");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!File.Exists(release) && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(release), "Parent did not release the cross-process fence probe.");
            return;
        }

        var rootPath = Path.Combine(Path.GetTempPath(), "omnicore-m9-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(Path.Combine(rootPath, "workspace"));
        Process? holder = null;
        try
        {
            holder = StartFenceProcess(rootPath);
            await WaitUntil(() => File.Exists(Path.Combine(rootPath, "ready")), TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            using var contender = new SqliteWorkspaceMutationHistoryStore(Path.Combine(rootPath, "data"));
            var acquired = Task.Run(() =>
            {
                using var fence = contender.AcquireFence(Path.Combine(rootPath, "workspace"), TestContext.Current.CancellationToken);
                return true;
            });
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Assert.False(acquired.IsCompleted, "A second process entered while the first still held the OS fence.");
            File.WriteAllText(Path.Combine(rootPath, "release"), "release");
            Assert.True(await acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, holder.ExitCode);
        }
        finally
        {
            if (holder is { HasExited: false }) holder.Kill(entireProcessTree: true);
            holder?.Dispose();
            M7FixtureCleanup.DeleteOwnedTempDirectory(rootPath);
        }
    }

    private static Process StartFenceProcess(string root)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(GitWorktreeStoreTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*Workspace_mutation_fence_excludes_a_second_process_until_the_first_releases_it");
        start.ArgumentList.Add("-noColor");
        start.Environment[MutationFencePhaseVariable] = "hold";
        start.Environment[MutationHistoryRootVariable] = root;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fence probe.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10, cancellationToken);
        Assert.True(predicate(), "Timed out waiting for the child process fence marker.");
    }

    [Fact]
    public async Task Integration_batch_over_limit_is_rejected_before_any_delete_or_create_effect()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("source.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.Delete(Path.Combine(worktree.WorktreePath, "source.txt"));
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "moved.txt"), "base\n");
        var preview = await store.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(fixture.Data);
        using var history = new SqliteWorkspaceMutationHistoryStore(fixture.Data);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, store);
        var executor = CreateExecutor(fixture.Repository, artifacts, coordinator,
            CreateIntegrationBoundary(maxFiles: 1), PermissionDecision.Allow, includeLease: true,
            mutationHistory: history);
        var journal = new RecordingEventStore(new InMemoryEventStore());
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var turn = TurnId.New();
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview!.ProposalId,
            paths = preview.Preview.Changes.Select(change => change.RelativePath).ToArray(),
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("worktree.integrate"), "pipeline", args);
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn));

        var outcome = executor.ExecuteTool(call, userApprovesAsk: false, CancellationToken.None, stream);

        Assert.False(outcome.Succeeded);
        Assert.Equal("base\n", File.ReadAllText(Path.Combine(fixture.Repository, "source.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "moved.txt")));
        Assert.Empty(history.ReadBudgetCharges(fixture.Repository, run.RunId.ToString(), turn.ToString()));
        Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(EventCodecs.Create().Decode),
            item => item is ToolCallStarted);
    }

    private static async Task RunHistoryProcess(string phase, string root)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(GitWorktreeStoreTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*Native_worktree_move_then_delete_survives_process_exit_and_source_can_be_recreated");
        start.ArgumentList.Add("-noColor");
        start.Environment[MutationHistoryPhaseVariable] = phase;
        start.Environment[MutationHistoryRootVariable] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start history probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, phase + " process failed: " + output);
    }

    private static async Task DeleteInChildProcess(string root)
    {
        var fixture = new GitFixture(root);
        fixture.Init();
        fixture.Write("source.txt", "restored from protected history\n");
        fixture.CommitAll("base");
        var git = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>((await git.CreateAsync(fixture.Request(),
            CancellationToken.None)).Worktree);
        File.Delete(Path.Combine(worktree.WorktreePath, "source.txt"));
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "moved.txt"), "restored from protected history\n");
        var preview = await git.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        var artifacts = new FileArtifactStore(fixture.Data);
        using var history = new SqliteWorkspaceMutationHistoryStore(fixture.Data);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, git);
        var executor = CreateExecutor(fixture.Repository, artifacts, coordinator, CreateIntegrationBoundary(),
            PermissionDecision.Allow, includeLease: true, mutationHistory: history);
        using var journal = new SqliteEventStore(Path.Combine(fixture.Data, "journal.db"));
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var turn = TurnId.New();
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview!.ProposalId,
            paths = preview.Preview.Changes.Select(change => change.RelativePath).ToArray(),
        });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("worktree.integrate"), "pipeline", args);
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn));
        var outcome = executor.ExecuteTool(call, userApprovesAsk: false, CancellationToken.None, stream);
        Assert.True(outcome.Succeeded, outcome.Summary);
        Assert.True(journal.PurgeSession(session, CancellationToken.None) > 0);
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "source.txt")));
        Assert.Equal("restored from protected history\n", File.ReadAllText(Path.Combine(fixture.Repository, "moved.txt")));
        var tombstone = history.ReadFile(fixture.Repository, "source.txt");
        Assert.Equal(WorkspaceFileHistoryState.Tombstone, tombstone?.State);
        Assert.NotNull(tombstone?.ImageRef);
        Assert.False(File.Exists(Path.Combine(worktree.WorktreePath, "source.txt")));
        Assert.True(File.Exists(Path.Combine(worktree.WorktreePath, "moved.txt")));
    }

    private static void RecreateInChildProcess(string root)
    {
        var fixture = new GitFixture(root);
        var artifacts = new FileArtifactStore(fixture.Data);
        using var history = new SqliteWorkspaceMutationHistoryStore(fixture.Data);
        var boundary = CreateIntegrationBoundary(requirePriorRead: true);
        var tools = OmniHost.CreateActTools();
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.write"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        var executor = new ScriptedToolExecutor(tools.Catalog(), policy, fixture.Repository, boundary,
            audit: null, interactionResponder: null, isInteractive: false, artifacts: artifacts,
            workspaceWriteLeases: HostWorkspaceWriteLeases.Shared, mutationHistory: history);
        var journal = new RecordingEventStore(new InMemoryEventStore());
        var session = SessionId.New();
        var run = TestRun.Open(journal, session);
        var turn = TurnId.New();
        var stream = new EventStream(journal, EventCodecs.Create(), session);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn));
        var read = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
            "read", "{\"path\":\"source.txt\"}"), false, CancellationToken.None, stream);
        Assert.True(read.Succeeded, read.Summary);
        Assert.Contains("restored from protected history", read.Preview);
        Assert.Contains("[source:protected tombstone history]", read.Preview);
        var token = FileVersion.VersionToken(FileVersion.Encode("restored from protected history\n", FileVersion.FileEncoding.Utf8NoBom));
        var writeArgs = System.Text.Json.JsonSerializer.Serialize(new
        { path = "source.txt", content = "restored from protected history\n", expectedVersion = token });
        var write = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.write"),
            "write", writeArgs), false, CancellationToken.None, stream);
        Assert.True(write.Succeeded, write.Summary);
        Assert.Equal("restored from protected history\n", File.ReadAllText(Path.Combine(fixture.Repository, "source.txt")));
        Assert.Equal(WorkspaceFileHistoryState.Present, history.ReadFile(fixture.Repository, "source.txt")?.State);
        var reread = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
            "read-current", "{\"path\":\"source.txt\"}"), false, CancellationToken.None, stream);
        Assert.True(reread.Succeeded, reread.Summary);
        Assert.DoesNotContain("[source:protected tombstone history]", reread.Preview);
    }

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
    public async Task Snapshot_retention_prunes_only_removed_ownership_refs_after_seven_days()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        // Different trees make distinct snapshot commits even when both are created in one second.
        fixture.Write("tracked.txt", "active snapshot\n");
        var activeCreate = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var active = Assert.IsType<WorktreeIdentity>(activeCreate.Worktree);
        Assert.NotEqual(worktree.SnapshotCommit, active.SnapshotCommit);

        Assert.True((await store.CleanupAsync(worktree.Ownership, CancellationToken.None)).Removed);
        var refBefore = fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim();
        var dataRoot = fixture.Request().WorkspaceDataRoot;
        var workspaceId = fixture.Request().WorkspaceId.ToString();
        var underRetention = await store.PruneExpiredSnapshotRefsAsync(fixture.Repository, dataRoot, workspaceId,
            DateTimeOffset.UtcNow.AddDays(6), dryRun: false, CancellationToken.None);
        Assert.Equal(0, underRetention.Eligible);
        Assert.Equal(0, underRetention.Pruned);
        Assert.Equal(refBefore, fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim());

        var dryRun = await store.PruneExpiredSnapshotRefsAsync(fixture.Repository, dataRoot, workspaceId,
            DateTimeOffset.UtcNow.AddDays(8), dryRun: true, CancellationToken.None);
        Assert.Equal(1, dryRun.Eligible);
        Assert.True(dryRun.Preserved >= 1);
        Assert.Equal(0, dryRun.Pruned);
        Assert.Equal(refBefore, fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim());

        var applied = await store.PruneExpiredSnapshotRefsAsync(fixture.Repository, dataRoot, workspaceId,
            DateTimeOffset.UtcNow.AddDays(8), dryRun: false, CancellationToken.None);
        Assert.Equal(1, applied.Eligible);
        Assert.Equal(1, applied.Pruned);
        Assert.Equal(0, applied.Failed);
        Assert.Equal(active.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", active.SnapshotRef + "^{commit}").Trim());
        Assert.False(Directory.Exists(worktree.WorktreePath));
        _ = fixture.GitExpectFailure("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}");
        Assert.True((await store.CleanupAsync(active.Ownership, CancellationToken.None)).Removed);
    }

    [Fact]
    public async Task Snapshot_retention_preserves_a_removed_ref_when_an_active_worktree_uses_the_same_commit()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var removed = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(
            fixture.Request() with { Base = WorktreeBase.Head }, CancellationToken.None)).Worktree);
        var active = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(
            fixture.Request() with { Base = WorktreeBase.Head }, CancellationToken.None)).Worktree);
        Assert.Equal(removed.SnapshotCommit, active.SnapshotCommit);
        Assert.True((await store.CleanupAsync(removed.Ownership, CancellationToken.None)).Removed);

        var outcome = await store.PruneExpiredSnapshotRefsAsync(fixture.Repository, fixture.Data,
            removed.WorkspaceId.ToString(), DateTimeOffset.UtcNow.AddDays(8), dryRun: false,
            CancellationToken.None);

        Assert.Equal(0, outcome.Eligible);
        Assert.Equal(0, outcome.Pruned);
        Assert.Equal(2, outcome.Preserved);
        Assert.Equal(removed.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", removed.SnapshotRef + "^{commit}").Trim());
        Assert.True(Directory.Exists(active.WorktreePath));
        Assert.True((await store.CleanupAsync(active.Ownership, CancellationToken.None)).Removed);
    }

    [Fact]
    public async Task Snapshot_retention_preserves_metadata_bound_to_another_workspace_id()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        Assert.True((await store.CleanupAsync(worktree.Ownership, CancellationToken.None)).Removed);
        var referenceBefore = fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim();

        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(worktree.OwnershipMetadataPath))!;
        var workspaceIdProperty = Assert.Single(metadata.AsObject(), item =>
            string.Equals(item.Key, "WorkspaceId", StringComparison.OrdinalIgnoreCase)).Key;
        metadata[workspaceIdProperty] = fixture.Request().WorkspaceId.ToString() == "0000000000000000"
            ? "0000000000000001" : "0000000000000000";
        File.WriteAllText(worktree.OwnershipMetadataPath, metadata.ToJsonString());

        var outcome = await store.PruneExpiredSnapshotRefsAsync(fixture.Repository,
            fixture.Request().WorkspaceDataRoot, fixture.Request().WorkspaceId.ToString(),
            DateTimeOffset.UtcNow.AddDays(8), dryRun: false, CancellationToken.None);

        Assert.Equal(0, outcome.Pruned);
        Assert.Equal(1, outcome.Preserved);
        Assert.Equal(referenceBefore,
            fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim());
    }

    [Fact]
    public async Task Startup_sweep_cleans_only_old_terminal_orphan_worktrees_and_keeps_active_or_dirty_data()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var terminal = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(), CancellationToken.None)).Worktree);
        var active = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(), CancellationToken.None)).Worktree);
        var dirtyOrphan = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(), CancellationToken.None)).Worktree);
        var cleanOrphan = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(fixture.Request(), CancellationToken.None)).Worktree);
        var now = DateTimeOffset.UtcNow;
        SetMetadataCreatedAt(terminal, now.AddDays(-9));
        SetMetadataCreatedAt(active, now.AddDays(-9));
        SetMetadataCreatedAt(dirtyOrphan, now.AddDays(-9));
        SetMetadataCreatedAt(cleanOrphan, now.AddDays(-9));
        File.WriteAllText(Path.Combine(dirtyOrphan.WorktreePath, "tracked.txt"), "keep user data\n");

        var outcome = await WorktreeStartupRecoveryHost.SweepExpiredAsync(fixture.Repository, fixture.Data,
            fixture.Request().WorkspaceId.ToString(), terminal.GitCommonDirectory,
            new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal)
            {
                [terminal.OwnershipId] = now.AddDays(-8),
                [active.OwnershipId] = null,
            }, new HashSet<LaneId>(), new Dictionary<LaneId, DateTimeOffset>
            {
                [dirtyOrphan.LaneId] = now.AddDays(-8),
                [cleanOrphan.LaneId] = now.AddDays(-8),
            }, new HashSet<LaneId>(), new HashSet<string>(StringComparer.Ordinal),
            now, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.ErrorCode?.ToString());
        Assert.Equal(1, outcome.WorktreesRemoved);
        Assert.Equal(3, outcome.WorktreesPreserved);
        Assert.True(Directory.Exists(terminal.WorktreePath));
        Assert.True(Directory.Exists(active.WorktreePath));
        Assert.True(Directory.Exists(dirtyOrphan.WorktreePath));
        Assert.False(Directory.Exists(cleanOrphan.WorktreePath));
        Assert.Equal("keep user data\n", File.ReadAllText(Path.Combine(dirtyOrphan.WorktreePath, "tracked.txt")));
        Assert.Equal(cleanOrphan.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", cleanOrphan.SnapshotRef + "^{commit}").Trim());
    }

    [Fact]
    public async Task Startup_sweep_uses_verified_workspace_journal_and_keeps_active_lane_worktree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write(".gitignore", ".omnicore/\n");
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(fixture.Repository);
        var rootIdentity = WorkspaceRootIdentity.Establish(physicalRoot);
        var paths = OmniHost.CreatePlatformPaths(fixture.Data);
        var dataRoot = OmniHost.WorkspaceDataDirectory(paths, physicalRoot);
        var request = fixture.Request() with
        {
            WorkspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalRoot)),
            WorkspaceDataRoot = dataRoot,
        };
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var terminal = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(request with { LaneId = LaneId.New() }, CancellationToken.None)).Worktree);
        var active = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(request with { LaneId = LaneId.New() }, CancellationToken.None)).Worktree);
        var provisioning = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(request with { LaneId = LaneId.New() }, CancellationToken.None)).Worktree);
        var orphan = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(request with { LaneId = LaneId.New() }, CancellationToken.None)).Worktree);
        var unknown = Assert.IsType<WorktreeIdentity>((await store.CreateAsync(request with { LaneId = LaneId.New() }, CancellationToken.None)).Worktree);
        var now = DateTimeOffset.UtcNow;
        SetMetadataCreatedAt(unknown, now.AddDays(-8));

        var eventStore = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var terminalTask = TaskId.New();
        var terminalLane = terminal.LaneId;
        var terminalExecution = ExecutionId.New();
        var activeTask = TaskId.New();
        var activeLane = active.LaneId;
        var activeExecution = ExecutionId.New();
        AddJournalEvent(eventStore, codecs, session, new SessionCreated(session, request.WorkspaceId.ToString(),
            physicalRoot, ProfileId.New(), now));
        AddJournalEvent(eventStore, codecs, session, new WorkspaceRootEstablished(session, physicalRoot, now,
            rootIdentity));
        AddJournalEvent(eventStore, codecs, session, new LaneWorktreeCreated(terminalTask, terminalLane,
            terminalExecution, terminal.OwnershipId, terminal.RepositoryRoot, terminal.GitCommonDirectory,
            terminal.WorktreePath, terminal.SnapshotCommit, terminal.SnapshotTree, FixtureArtifactRef(), FixtureArtifactRef()));
        AddJournalEvent(eventStore, codecs, session, new LaneCompleted(terminalLane, null));
        AddJournalEvent(eventStore, codecs, session, new LaneWorktreeCreated(activeTask, activeLane,
            activeExecution, active.OwnershipId, active.RepositoryRoot, active.GitCommonDirectory,
            active.WorktreePath, active.SnapshotCommit, active.SnapshotTree, FixtureArtifactRef(), FixtureArtifactRef()));
        AddJournalEvent(eventStore, codecs, session, new LaneCreated(provisioning.LaneId, TaskId.New(), ProfileId.New()));
        AddJournalEvent(eventStore, codecs, session, new LaneProvisioning(provisioning.LaneId));
        var failedTask = TaskId.New();
        AddJournalEventAt(eventStore, codecs, session, new LaneCreated(orphan.LaneId, failedTask, ProfileId.New()), now.AddDays(-8));
        AddJournalEventAt(eventStore, codecs, session, new LaneProvisioning(orphan.LaneId), now.AddDays(-8));
        AddJournalEventAt(eventStore, codecs, session, new LaneFailed(orphan.LaneId, "fixture failure"), now.AddDays(-8));
        Assert.Contains(eventStore.ReadFrom(session, 1), envelope => codecs.Decode(envelope) is LaneCreated created
            && created.LaneId == provisioning.LaneId);
        Assert.DoesNotContain(eventStore.ReadFrom(session, 1), envelope => codecs.Decode(envelope) is LaneCompleted completed
            && completed.LaneId == provisioning.LaneId);
        Assert.NotEqual(terminal.LaneId, provisioning.LaneId);
        Assert.True(WorkspaceRootIdentity.Verify(physicalRoot, rootIdentity));
        Assert.Equal(request.WorkspaceId.ToString(),
            WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalRoot)).ToString());
        var inspected = await store.InspectAsync(physicalRoot, CancellationToken.None);
        Assert.True(inspected.Succeeded, inspected.ErrorCode?.ToString());
        Assert.Equal(terminal.GitCommonDirectory, inspected.Identity!.GitCommonDirectory);

        var outcome = await WorktreeStartupRecoveryHost.SweepAsync(eventStore, codecs, physicalRoot,
            paths, now, CancellationToken.None);

        Assert.True(outcome.Succeeded,
            $"error={outcome.ErrorCode}; removed={outcome.WorktreesRemoved}; preserved={outcome.WorktreesPreserved}; refs={outcome.SnapshotRefsPruned}; refFailures={outcome.SnapshotRefsFailed}");
        Assert.Equal(1, outcome.WorktreesRemoved);
        Assert.True(outcome.WorktreesPreserved >= 4);
        Assert.True(Directory.Exists(terminal.WorktreePath));
        Assert.True(Directory.Exists(active.WorktreePath));
        Assert.True(Directory.Exists(provisioning.WorktreePath));
        Assert.False(Directory.Exists(orphan.WorktreePath));
        Assert.True(Directory.Exists(unknown.WorktreePath));
        Assert.Equal(orphan.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", orphan.SnapshotRef + "^{commit}").Trim());
    }

    [Fact]
    public async Task OmniServer_startup_runs_the_bounded_sweep_only_from_verified_last_session_root()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write(".gitignore", ".omnicore/\n");
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(fixture.Repository);
        var rootIdentity = WorkspaceRootIdentity.Establish(root);
        var paths = OmniHost.CreatePlatformPaths(fixture.Data);
        var dataRoot = OmniHost.WorkspaceDataDirectory(paths, root);
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root));
        var request = new WorktreeCreateRequest(workspace, LaneId.New(), root, dataRoot);
        var worktree = Assert.IsType<WorktreeIdentity>((await new GitWorktreeStore(new SystemProcessRuntime())
            .CreateAsync(request, CancellationToken.None)).Worktree);
        SetMetadataCreatedAt(worktree, DateTimeOffset.UtcNow.AddDays(-8));

        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var now = DateTimeOffset.UtcNow;
        AddJournalEvent(store, codecs, session, new SessionCreated(session, workspace.ToString(), root,
            ProfileId.New(), now));
        AddJournalEvent(store, codecs, session, new WorkspaceRootEstablished(session, root, now, rootIdentity));
        AddJournalEvent(store, codecs, session, new RunCreated(run, session, "startup maintenance fixture",
            RunMode.Plan, ExecutionStrategy.Direct, FailurePolicy.FailRun, new TaskBudget(1m, 1000, 1, 1),
            TaskId.New(), now));
        AddJournalEvent(store, codecs, session, new RunStarted(run));
        var failedProvisioningAt = now.AddDays(-8);
        var failedTask = new TaskCreated(TaskId.New(), run, "orphan provisioning fixture", [],
            new TaskBudget(1m, 1000, 1, 1));
        AddJournalEventAt(store, codecs, session, failedTask, failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session, new TaskReady(failedTask.TaskId), failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session,
            new LaneCreated(worktree.LaneId, failedTask.TaskId, ProfileId.New()), failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session, new TaskStarted(failedTask.TaskId, worktree.LaneId),
            failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session, new LaneProvisioning(worktree.LaneId), failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session, new TaskFailed(failedTask.TaskId, "provisioning failed"),
            failedProvisioningAt, run);
        AddJournalEventAt(store, codecs, session, new LaneFailed(worktree.LaneId, "provisioning failed"),
            failedProvisioningAt, run);
        var stateFile = Path.Combine(fixture.Data, "last-session.txt");
        File.WriteAllText(stateFile, session + "\n" + run);

        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile,
            platformPathsFactory: () => paths);

        Assert.True(server.LastRecoveryProblem() is null, server.LastRecoveryProblem());
        Assert.Null(server.LastWorktreeMaintenanceProblem());
        Assert.False(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(worktree.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim());
    }

    [Fact]
    public async Task OmniServer_without_last_session_sweeps_only_after_configured_root_matches_old_journal()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write(".gitignore", ".omnicore/\n");
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var root = ProjectIdentity.ResolvePhysicalWorkspaceRoot(fixture.Repository);
        var rootIdentity = WorkspaceRootIdentity.Establish(root);
        var paths = OmniHost.CreatePlatformPaths(fixture.Data);
        var dataRoot = OmniHost.WorkspaceDataDirectory(paths, root);
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root));
        var worktree = Assert.IsType<WorktreeIdentity>((await new GitWorktreeStore(new SystemProcessRuntime())
            .CreateAsync(new WorktreeCreateRequest(workspace, LaneId.New(), root, dataRoot), CancellationToken.None)).Worktree);
        SetMetadataCreatedAt(worktree, DateTimeOffset.UtcNow.AddDays(-8));

        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-8);
        AddJournalEvent(store, codecs, session, new SessionCreated(session, workspace.ToString(), root,
            ProfileId.New(), createdAt));
        AddJournalEvent(store, codecs, session, new WorkspaceRootEstablished(session, root, createdAt, rootIdentity));
        AddJournalEvent(store, codecs, session, new RunCreated(run, session, "orphan startup fixture",
            RunMode.Plan, ExecutionStrategy.Direct, FailurePolicy.FailRun, new TaskBudget(1m, 1000, 1, 1),
            TaskId.New(), createdAt));
        AddJournalEvent(store, codecs, session, new RunStarted(run));
        var failedTask = new TaskCreated(TaskId.New(), run, "old provisioning failure", [],
            new TaskBudget(1m, 1000, 1, 1));
        AddJournalEventAt(store, codecs, session, failedTask, createdAt, run);
        AddJournalEventAt(store, codecs, session, new TaskReady(failedTask.TaskId), createdAt, run);
        AddJournalEventAt(store, codecs, session, new LaneCreated(worktree.LaneId, failedTask.TaskId, ProfileId.New()), createdAt, run);
        AddJournalEventAt(store, codecs, session, new TaskStarted(failedTask.TaskId, worktree.LaneId), createdAt, run);
        AddJournalEventAt(store, codecs, session, new LaneProvisioning(worktree.LaneId), createdAt, run);
        AddJournalEventAt(store, codecs, session, new TaskFailed(failedTask.TaskId, "provisioning failed"), createdAt, run);
        AddJournalEventAt(store, codecs, session, new LaneFailed(worktree.LaneId, "provisioning failed"), createdAt, run);

        // There is no last-session pointer at startup. The configured root arrives only after
        // the server constructor, and the sweep must use that root plus historical durable proof.
        var stateFile = Path.Combine(fixture.Data, "absent-last-session.txt");
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile,
            platformPathsFactory: () => paths);
        Assert.Null(server.LastSessionId());
        Assert.True(Directory.Exists(worktree.WorktreePath));

        server.ConfigureWorkspaceRoot(root);

        Assert.Null(server.LastWorktreeMaintenanceProblem());
        Assert.False(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(worktree.SnapshotCommit,
            fixture.Git("rev-parse", "--verify", worktree.SnapshotRef + "^{commit}").Trim());
    }

    private static void AddJournalEvent(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        DomainEventPayload payload)
    {
        var codec = codecs.CodecFor(payload.Type());
        store.Append(session, DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, null, null,
            null, null, null, null, null, [], codec.Encode(payload)), DurabilityClass.Barrier, CancellationToken.None);
    }

    private static void AddJournalEventAt(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        DomainEventPayload payload, DateTimeOffset timestamp, RunId? runId = null)
    {
        var codec = codecs.CodecFor(payload.Type());
        var lane = payload switch
        {
            LaneCreated created => created.LaneId,
            TaskStarted started => started.LaneId,
            LaneProvisioning provisioning => provisioning.LaneId,
            LaneFailed failed => failed.LaneId,
            _ => null,
        };
        var task = payload switch
        {
            TaskCreated created => created.TaskId,
            TaskReady ready => ready.TaskId,
            TaskStarted started => started.TaskId,
            TaskFailed failed => failed.TaskId,
            _ => null,
        };
        var envelope = DomainEvent.Stored(EventId.New(), session, 0, payload.Type(), payload.SchemaVersion(),
            timestamp, null, null, runId, task, lane, null, null, null, [], codec.Encode(payload));
        store.Append(session, envelope, DurabilityClass.Barrier, CancellationToken.None);
    }

    private static ArtifactRef FixtureArtifactRef() => new(ArtifactId.New(),
        ContentHash.Sha256(new string('0', 64)), 0, "application/octet-stream", ArtifactKind.Other,
        Sensitivity.Normal);

    private static void SetMetadataCreatedAt(WorktreeIdentity identity, DateTimeOffset createdAt)
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(identity.OwnershipMetadataPath))!;
        var key = Assert.Single(metadata.AsObject(), item =>
            string.Equals(item.Key, "CreatedAtUtc", StringComparison.OrdinalIgnoreCase)).Key;
        metadata[key] = createdAt;
        File.WriteAllText(identity.OwnershipMetadataPath, metadata.ToJsonString());
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
    public async Task Cleanup_removes_clean_lane_only_after_its_commit_is_reachable_from_workspace_head()
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
        fixture.Git("merge", "--ff-only", laneHead);

        var cleanup = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);

        Assert.True(cleanup.Removed);
        Assert.False(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(laneHead, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(worktree.SnapshotCommit, fixture.Git("rev-parse", worktree.SnapshotRef).Trim());
    }

    [Fact]
    public async Task Explicit_discard_archives_dirty_lane_tree_before_removal_and_retention()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("base.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var path = Path.Combine(worktree.WorktreePath, "untracked output.txt");
        File.WriteAllText(path, "archive this output\n");
        var discarded = await store.DiscardAsync(worktree.Ownership, CancellationToken.None);
        Assert.True(discarded.Removed, discarded.Error?.ToString());
        Assert.False(Directory.Exists(worktree.WorktreePath));
        var retainedRef = Assert.Single(fixture.Git("for-each-ref", "--format=%(refname)",
            "refs/omnicore/retained-lanes").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("archive this output\n", fixture.Git("show", retainedRef + ":untracked output.txt"));
        var metadataJson = File.ReadAllText(worktree.Ownership.MetadataPath);
        using (var metadata = System.Text.Json.JsonDocument.Parse(metadataJson))
            Assert.Equal("discard", metadata.RootElement.GetProperty("RetainedLaneDisposition").GetString());
    }

    [Fact]
    public async Task Explicit_discard_refuses_ignored_outputs_that_would_not_be_in_retained_tree()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write(".gitignore", ".local-output\n");
        fixture.Write("base.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        var ignored = Path.Combine(worktree.WorktreePath, ".local-output");
        File.WriteAllText(ignored, "keep this file\n");

        var discarded = await store.DiscardAsync(worktree.Ownership, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, discarded.Error);
        Assert.True(Directory.Exists(worktree.WorktreePath));
        Assert.Equal("keep this file\n", File.ReadAllText(ignored));
        Assert.Empty(fixture.Git("for-each-ref", "--format=%(refname)", "refs/omnicore/retained-lanes"));
    }

    [Fact]
    public async Task Cleanup_preserves_clean_worktree_with_unmerged_lane_commit()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("tracked.txt", "base\n");
        fixture.CommitAll("base");
        var store = new GitWorktreeStore(new SystemProcessRuntime());
        var created = await store.CreateAsync(fixture.Request(), CancellationToken.None);
        var worktree = Assert.IsType<WorktreeIdentity>(created.Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "tracked.txt"), "unmerged lane edit\n");
        fixture.Git("-C", worktree.WorktreePath, "add", "tracked.txt");
        fixture.Git("-C", worktree.WorktreePath, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "lane result");
        var laneHead = fixture.Git("-C", worktree.WorktreePath, "rev-parse", "HEAD").Trim();

        var cleanup = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);

        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, cleanup.Error);
        Assert.True(Directory.Exists(worktree.WorktreePath));
        Assert.Equal(laneHead, fixture.Git("-C", worktree.WorktreePath, "rev-parse", "HEAD").Trim());
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
        var completedIntegration = Assert.IsType<WorktreeIntegrationStateRecorded>(Assert.Single(events,
            evt => evt is WorktreeIntegrationStateRecorded { State: WorktreeIntegrationState.Completed }));
        Assert.Equal(preview.Preview!.TheirsTree, completedIntegration.LaneTreeAtCapture);
        Assert.Equal(preview.Preview.LaneHeadAtCapture, completedIntegration.LaneHeadAtCapture);
        Assert.True(completedIntegration.CompleteLaneResult);
        Assert.True(artifacts.Verify(spec!.BeforeStateRef!.Hash, spec.BeforeStateRef.Size));

        // Cleanup never treats a successful earlier integration as authorization to discard
        // edits made to the lane afterward. The source worktree and those edits remain owned.
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "edit.txt"), "later lane edit\n");
        var cleanupAfterLaneEdit = await store.CleanupAsync(worktree.Ownership, CancellationToken.None);
        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, cleanupAfterLaneEdit.Error);
        Assert.Equal("later lane edit\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "edit.txt")));

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
    public async Task Host_cleanup_archives_and_removes_only_a_fully_integrated_terminal_lane()
    {
        var scenario = await CreateCleanupScenario();
        var session = Assert.IsType<SessionCreated>(scenario.Journal.ReadEvents(EventType.Of("session.created"))
            .Select(scenario.Codecs.Decode).Single());
        var journalPayloads = scenario.Journal.ReadFrom(session.SessionId, 1).Select(envelope =>
            (Envelope: envelope, Payload: scenario.Codecs.Decode(envelope))).ToArray();
        var toolStarted = Assert.IsType<ToolCallStarted>(journalPayloads.Single(item => item.Payload is ToolCallStarted).Payload);
        var integrationCompleted = Assert.IsType<WorktreeIntegrationStateRecorded>(journalPayloads.Single(item =>
            item.Payload is WorktreeIntegrationStateRecorded state && state.State == WorktreeIntegrationState.Completed).Payload);
        var rootEstablished = Assert.IsType<WorkspaceRootEstablished>(journalPayloads.Single(item =>
            item.Payload is WorkspaceRootEstablished).Payload);
        var snapshots = new WorkspaceSnapshotStore(scenario.Fixture.Repository, scenario.Journal, scenario.Codecs,
            scenario.Artifacts, rootEstablished.DurableIdentity, verifyPhysicalRoot: false);
        Assert.Equal(2, snapshots.ReadBatchEvidence(toolStarted.ToolCallId, toolStarted, integrationCompleted)?.Count);
        var undoPlan = snapshots.BuildPlan(session.SessionId, toolStarted.ToolCallId);
        Assert.Equal(2, undoPlan.Entries.Count);
        Assert.All(undoPlan.Entries, entry => Assert.Equal("Ready", entry.State));
        Assert.Equal(2, ChangedFilesReader.Read(scenario.Journal, scenario.Codecs, session.SessionId,
            scenario.Artifacts).Files.Count);

        var outcome = await WorktreeCleanupHost.CleanupWithDependenciesAsync(scenario.Fixture.Repository,
            scenario.Fixture.Data, scenario.Worktree.OwnershipId, scenario.Journal, scenario.Codecs,
            scenario.Artifacts, scenario.Git, CancellationToken.None);

        Assert.True(outcome.Removed, outcome.Error?.ToString());
        Assert.False(Directory.Exists(scenario.Worktree.WorktreePath));
        var retained = scenario.Fixture.Git("for-each-ref", "--format=%(refname)", "refs/omnicore/retained-lanes");
        var retainedRef = Assert.Single(retained.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(scenario.Preview.TheirsTree, scenario.Fixture.Git("show", "-s", "--format=%T", retainedRef).Trim());
        Assert.Equal(scenario.Preview.LaneHeadAtCapture,
            scenario.Fixture.Git("rev-parse", "--verify", retainedRef + "^1").Trim());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Host_cleanup_preserves_lane_when_proposal_is_partial_or_a_consumer_is_active(
        bool subset, bool activeConsumer)
    {
        var scenario = await CreateCleanupScenario(subset, activeConsumer);
        var before = File.ReadAllText(Path.Combine(scenario.Worktree.WorktreePath, "sample.txt"));
        var outcome = await WorktreeCleanupHost.CleanupWithDependenciesAsync(scenario.Fixture.Repository,
            scenario.Fixture.Data, scenario.Worktree.OwnershipId, scenario.Journal, scenario.Codecs,
            scenario.Artifacts, scenario.Git, CancellationToken.None);

        Assert.False(outcome.Removed);
        Assert.Equal(GitWorktreeErrorCode.WorktreeDirty, outcome.Error);
        Assert.True(Directory.Exists(scenario.Worktree.WorktreePath));
        Assert.Equal(before, File.ReadAllText(Path.Combine(scenario.Worktree.WorktreePath, "sample.txt")));
        Assert.Empty(scenario.Fixture.Git("for-each-ref", "--format=%(refname)", "refs/omnicore/retained-lanes"));
    }

    [Fact]
    public async Task Host_cleanup_keeps_lane_after_an_edit_since_the_integrated_capture()
    {
        var scenario = await CreateCleanupScenario();
        File.WriteAllText(Path.Combine(scenario.Worktree.WorktreePath, "sample.txt"), "after integration\n");

        var outcome = await WorktreeCleanupHost.CleanupWithDependenciesAsync(scenario.Fixture.Repository,
            scenario.Fixture.Data, scenario.Worktree.OwnershipId, scenario.Journal, scenario.Codecs,
            scenario.Artifacts, scenario.Git, CancellationToken.None);

        Assert.False(outcome.Removed);
        Assert.Equal(GitWorktreeErrorCode.WorkspaceChanged, outcome.Error);
        Assert.Equal("after integration\n", File.ReadAllText(Path.Combine(scenario.Worktree.WorktreePath, "sample.txt")));
        Assert.True(Directory.Exists(scenario.Worktree.WorktreePath));
    }

    private static async Task<CleanupScenario> CreateCleanupScenario(bool subset = false, bool activeConsumer = false)
    {
        var fixture = new GitFixture();
        fixture.Init();
        fixture.Write("sample.txt", "base\n");
        fixture.Write("second.txt", "second base\n");
        fixture.CommitAll("base");
        var git = new GitWorktreeStore(new SystemProcessRuntime());
        var worktree = Assert.IsType<WorktreeIdentity>(
            (await git.CreateAsync(fixture.Request(), CancellationToken.None)).Worktree);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "second.txt"), "second result\n");
        var previewOutcome = await git.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(previewOutcome.Succeeded, previewOutcome.Error?.ToString());
        var preview = previewOutcome.Preview!;
        var artifacts = new FileArtifactStore(Path.Combine(fixture.Root, "artifacts"));
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, git);
        var paths = subset ? new[] { "sample.txt" } : preview.Changes.Select(change => change.RelativePath).ToArray();
        var (tool, authorized, context, executionEvents) = MakeIntegrationCall(fixture.Repository, artifacts,
            coordinator, worktree.OwnershipId, preview.ProposalId, paths);
        ReconciliationSpec? spec = null;
        WorktreeIntegrationStateRecorded? completed = null;
        if (subset)
        {
            Assert.Throws<InvalidOperationException>(() => tool.DescribeReconciliation(authorized, context, CancellationToken.None));
            Assert.Equal("lane result\n", File.ReadAllText(Path.Combine(worktree.WorktreePath, "sample.txt")));
        }
        else
        {
            spec = Assert.IsType<ReconciliationSpec>(tool.DescribeReconciliation(authorized, context, CancellationToken.None));
            var result = await tool.ExecuteAsync(authorized, context, CancellationToken.None);
            Assert.False(result.IsError, result.Summary);
            completed = Assert.Single(executionEvents.OfType<WorktreeIntegrationStateRecorded>(),
                state => state.State == WorktreeIntegrationState.Completed);
        }

        var journal = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var rootTask = TaskId.New();
        var rootLane = LaneId.New();
        var rootExecution = ExecutionId.New();
        var childTask = TaskId.New();
        var childExecution = ExecutionId.New();
        var now = DateTimeOffset.UtcNow;
        AddJournalEvent(journal, codecs, session, new SessionCreated(session, worktree.WorkspaceId.ToString(),
            fixture.Repository, ProfileId.New(), now));
        AddJournalEvent(journal, codecs, session, new WorkspaceRootEstablished(session,
            ProjectIdentity.CanonicalWorkspacePath(fixture.Repository), now,
            WorkspaceRootIdentity.Establish(fixture.Repository)));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new RunCreated(run, session, "cleanup fixture", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), rootTask, now));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new TaskCreated(rootTask, run, "root", [], new TaskBudget(null, null, null, null)));
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new TaskCreated(childTask, run, "lane", [], new TaskBudget(null, null, null, null)));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new LaneCreated(rootLane, rootTask, ProfileId.New()));
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new LaneCreated(worktree.LaneId, childTask, ProfileId.New()));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new TaskStarted(rootTask, rootLane));
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new TaskStarted(childTask, worktree.LaneId));
        var selectedProjection = artifacts.PutText("{}", ContextInheritanceService.DelegationPacketMediaType,
            ArtifactKind.Other, Sensitivity.Sensitive);
        var taskPacket = new TaskPacket(1, session, run, childTask, worktree.LaneId, childExecution,
            DelegationId.New(), rootExecution, ProfileId.New(), "cleanup fixture lane",
            new TaskBudget(null, null, null, null), selectedProjection, worktree.OwnershipId,
            worktree.RepositoryRoot, worktree.GitCommonDirectory, worktree.WorktreePath,
            worktree.Head, worktree.SnapshotCommit, worktree.SnapshotTree,
            WorktreeBase.SnapshotOfWorkingTree, "bounded cleanup fixture working state");
        var packetJson = System.Text.Json.JsonSerializer.Serialize(taskPacket, TaskPacketJsonContext.Default.TaskPacket);
        var packetRef = artifacts.PutText(packetJson, LaneWorktreeProvisioner.TaskPacketMediaType,
            ArtifactKind.Other, Sensitivity.Sensitive);
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new LaneWorktreeCreated(childTask, worktree.LaneId, childExecution, worktree.OwnershipId,
                fixture.Repository, worktree.GitCommonDirectory, worktree.WorktreePath, worktree.SnapshotCommit,
                worktree.SnapshotTree, packetRef, selectedProjection));
        if (activeConsumer)
        {
            var pendingTask = TaskId.New(); var pendingLane = LaneId.New();
            var pendingExecution = ExecutionId.New();
            AppendScoped(journal, codecs, session, run, pendingTask, pendingLane, pendingExecution,
                new TaskCreated(pendingTask, run, "pending verify", [], new TaskBudget(null, null, null, null)));
            AppendScoped(journal, codecs, session, run, pendingTask, pendingLane, pendingExecution,
                new LaneCreated(pendingLane, pendingTask, ProfileId.New()));
        }

        if (spec is not null && completed is not null)
        {
            AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
                new ToolCallRequested(authorized.Intent.ToolCallId, "fixture", "worktree.integrate",
                    authorized.Intent.NormalizedArgumentsJson));
            var startState = Assert.IsType<WorktreeIntegrationStateRecorded>(spec.StartedEntityEvent);
            var started = new ToolCallStarted(authorized.Intent.ToolCallId, EffectClass.Reconcilable,
                spec.DurableMetadataJson)
            {
                BeforeStateRef = spec.BeforeStateRef,
                Reversibility = Reversibility.Unknown,
            };
            AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution, startState);
            AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution, started);
            AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution, completed);
            AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
                new ToolCallSucceeded(authorized.Intent.ToolCallId, "{}"));
        }
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new LaneCompleted(worktree.LaneId, null));
        AppendScoped(journal, codecs, session, run, childTask, worktree.LaneId, childExecution,
            new TaskCompleted(childTask, null));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new LaneCompleted(rootLane, null));
        AppendScoped(journal, codecs, session, run, rootTask, rootLane, rootExecution,
            new TaskCompleted(rootTask, null));
        return new CleanupScenario(fixture, git, worktree, preview, journal, codecs, artifacts);
    }

    private static void AppendScoped(IEventStore store, IEventCodecRegistry codecs, SessionId session,
        RunId run, TaskId task, LaneId lane, ExecutionId execution, DomainEventPayload payload)
    {
        var codec = codecs.CodecFor(payload.Type());
        var callId = payload switch
        {
            ToolCallRequested value => value.ToolCallId,
            ToolCallStarted value => value.ToolCallId,
            ToolCallSucceeded value => value.ToolCallId,
            WorktreeIntegrationStateRecorded value => value.ToolCallId,
            _ => (ToolCallId?)null,
        };
        var envelope = DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, run, run,
            task, lane, new TurnId(execution.Value), null, callId, [], codec.Encode(payload), execution);
        store.Append(session, envelope, DurabilityClass.Barrier, CancellationToken.None);
    }

    private sealed record CleanupScenario(GitFixture Fixture, GitWorktreeStore Git, WorktreeIdentity Worktree,
        WorktreeIntegrationPreview Preview, InMemoryEventStore Journal, IEventCodecRegistry Codecs, IArtifactStore Artifacts);

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
        var startedState = Assert.IsType<WorktreeIntegrationStateRecorded>(spec.StartedEntityEvent);
        stream.AppendBatch([
            new ToolCallRequested(callId, "fixture", "worktree.integrate", authorized.Intent.NormalizedArgumentsJson),
            new ToolCallPrepared(callId, "{}"),
            new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null),
            new ToolCallAuthorized(callId),
            startedState,
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
        int maxFiles = 8, bool requirePriorRead = false, FileMutationMode mutationMode = FileMutationMode.Full,
        Func<SessionSecurityPolicySet?>? sessionSecurityPolicy = null)
    {
        var key = ModelPolicyKey.For("m7-test", "fixture-model");
        var full = ModelPolicyPresets.FullAgent();
        var mutation = new FileMutationPolicy(mutationMode, delete, DestructiveActionPolicy.Allow,
            maxFiles, 2000, 1.0, requirePriorRead, requireExpectedVersionToken: false,
            requirePostEditValidation, allowParallelMutations: false);
        var user = new UserModelPolicy(full.Category, full.ToolPolicy, mutation, full.Source, full.Note);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 16,
            GuidanceLevel.Full, 3, PlanControl.ModelDriven, 8);
        var effective = EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, user, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), harness);
        return new ModelCapabilityBoundary(effective, ModelCapabilityBoundary.CoreTools, new FileReadRegistry(),
            sessionSecurityPolicy);
    }

    private static ScriptedToolExecutor CreateExecutor(string workspace, IArtifactStore artifacts,
        WorktreeIntegrationHostCoordinator coordinator, ModelCapabilityBoundary boundary,
        PermissionDecision permission, bool includeLease, IWorkspaceMutationHistoryStore? mutationHistory = null)
    {
        var tool = new WorktreeIntegrationApplyTool(new PathBoundaryValidator(), coordinator);
        var catalog = new FakeCatalog().Add(tool);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["worktree.integrate"] = permission,
        });
        return new ScriptedToolExecutor(catalog, policy, workspace, boundary, null, null, false,
            artifacts: artifacts,
            workspaceWriteLeases: includeLease ? HostWorkspaceWriteLeases.Shared : null,
            mutationHistory: mutationHistory);
    }

    private sealed class GitFixture : IDisposable
    {
        public string Root { get; }
        public string Repository => Path.Combine(Root, "repo with spaces");
        public string Data => Path.Combine(Root, "user data");

        public GitFixture(string? root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "omnicore-m7-git", Guid.NewGuid().ToString("N"));
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

        public WorktreeCreateRequest Request() => new(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(Repository)),
            LaneId.New(), Repository, Data);

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
