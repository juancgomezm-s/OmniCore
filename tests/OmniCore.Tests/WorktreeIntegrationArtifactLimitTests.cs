namespace OmniCore.Tests;

using System.Diagnostics;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class WorktreeIntegrationArtifactLimitTests
{
    [Fact]
    public async System.Threading.Tasks.Task Escaped_manifest_over_limit_is_rejected_before_CAS_or_start_barrier()
    {
        using var fixture = new GitFixture();
        fixture.Init();
        var content = new string('é', 512 * 1024); // 1 MiB UTF-8 per image; 8 MiB total pre/post set.
        var paths = Enumerable.Range(0, 4).Select(index => $"file-{index}.txt").ToArray();
        foreach (var path in paths) fixture.WriteBytes(path, Encoding.UTF8.GetBytes(content));
        fixture.CommitAll("base");
        var git = new GitWorktreeStore(new SystemProcessRuntime());
        var worktreeResult = await git.CreateAsync(fixture.Request(), CancellationToken.None);
        Assert.True(worktreeResult.Succeeded, worktreeResult.Error?.ToString());
        var worktree = Assert.IsType<WorktreeIdentity>(worktreeResult.Worktree);
        var changed = Encoding.UTF8.GetBytes(new string('ø', 512 * 1024));
        foreach (var path in paths) File.WriteAllBytes(Path.Combine(worktree.WorktreePath, path), changed);
        var preview = await git.PreviewIntegrationAsync(new(worktree), CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error?.ToString());
        Assert.Equal(4, preview.Preview!.Changes.Count);

        var artifactsPath = Path.Combine(fixture.Root, "artifacts");
        var artifacts = new FileArtifactStore(artifactsPath);
        var coordinator = new WorktreeIntegrationHostCoordinator(fixture.Repository, fixture.Data, artifacts, git);
        var json = System.Text.Json.JsonSerializer.Serialize(new { ownershipId = worktree.OwnershipId,
            proposalId = preview.Preview.ProposalId, paths });
        var tool = new WorktreeIntegrationApplyTool(new PathBoundaryValidator(), coordinator);
        var call = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "integration-limit-test", json);
        var prepared = Assert.IsType<Prepared>(tool.Prepare(call, new ToolPreparationContext(fixture.Repository, DateTimeOffset.UtcNow)));
        var authorized = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).Authorize(prepared.Intent);
        var events = new List<DomainEventPayload>();
        var context = new ToolExecutionContext(fixture.Repository, null, events.Add,
            artifacts: artifacts);

        var failure = Assert.Throws<ToolPreflightException>(() => tool.DescribeReconciliation(authorized, context,
            CancellationToken.None));

        Assert.Equal(ToolErrorCode.LimitExceeded, failure.ErrorCode);
        Assert.Empty(events); // No ToolCallStarted, so no Barrier or validation-debt transition can be committed.
        foreach (var path in paths) Assert.Equal(content, File.ReadAllText(Path.Combine(fixture.Repository, path)));
        Assert.False(Directory.Exists(artifactsPath)); // Rejected before protected CAS publication.
    }

    private sealed class GitFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-m7-manifest-limit", Guid.NewGuid().ToString("N"));
        public string Repository => Path.Combine(Root, "repo");
        public string Data => Path.Combine(Root, "data");
        public GitFixture() { Directory.CreateDirectory(Repository); Directory.CreateDirectory(Data); }
        public void Init() { Git("init", "--quiet", "--initial-branch=main"); Git("config", "core.autocrlf", "false"); }
        public void WriteBytes(string path, byte[] bytes)
        { var full = Path.Combine(Repository, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); }
        public void CommitAll(string message)
        { Git("add", "-A"); Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message); }
        public WorktreeCreateRequest Request() => new(WorkspaceId.Of(Repository), LaneId.New(), Repository, Data);
        private string Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Repository, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
            return stdout;
        }
        public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { } }
    }
}
