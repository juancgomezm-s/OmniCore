using System.Diagnostics;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class WorktreeInspectionCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-worktree-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public WorktreeInspectionCliTests()
    {
        _repo = Path.Combine(_root, "repo with spaces");
        Directory.CreateDirectory(_repo);
    }

    [Theory]
    [InlineData("es", "Repositorio:")]
    [InlineData("en", "Repository:")]
    public async Task Actual_cli_inspects_dirty_repository_without_changing_index_head_or_files(string locale, string label)
    {
        await Git("init", "--template=");
        await File.WriteAllTextAsync(Path.Combine(_repo, "sample.txt"), "committed\n", TestContext.Current.CancellationToken);
        await Git("add", "sample.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        var head = (await Git("rev-parse", "HEAD")).Output.Trim();
        await File.WriteAllTextAsync(Path.Combine(_repo, "sample.txt"), "staged\n", TestContext.Current.CancellationToken);
        await Git("add", "sample.txt");
        await File.WriteAllTextAsync(Path.Combine(_repo, "sample.txt"), "working\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo, "untracked.txt"), "untracked\n", TestContext.Current.CancellationToken);
        var index = await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken);
        var refs = (await Git("show-ref")).Output;
        var status = (await Git("--no-optional-locks", "status", "--porcelain=v1")).Output;

        var result = await Cli(locale, "worktree", "inspect", _repo);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(label, result.Output);
        Assert.Contains(head, result.Output);
        Assert.Contains(_repo, result.Output);
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken));
        Assert.Equal(head, (await Git("rev-parse", "HEAD")).Output.Trim());
        Assert.Equal(refs, (await Git("show-ref")).Output);
        Assert.Equal(status, (await Git("--no-optional-locks", "status", "--porcelain=v1")).Output);
        Assert.Equal("working\n", await File.ReadAllTextAsync(Path.Combine(_repo, "sample.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("untracked\n", await File.ReadAllTextAsync(Path.Combine(_repo, "untracked.txt"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".git", "worktrees")));
    }

    [Theory]
    [InlineData("es", "repositorio Git")]
    [InlineData("en", "Git repository")]
    public async Task Non_repository_is_localized_without_echoing_git_stderr(string locale, string text)
    {
        var result = await Cli(locale, "worktree", "inspect", _repo);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(text, result.Output);
        Assert.DoesNotContain("fatal:", result.Output);
        Assert.DoesNotContain("not a git repository", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creation_is_not_exposed_as_an_unjournaled_cli_operation()
    {
        var result = await Cli("en", "worktree", "create", _repo);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("omni worktree inspect", result.Output);
        Assert.DoesNotContain("ask", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_repo));
    }

    [Fact]
    public void Inspection_strings_have_both_locales()
    {
        var keys = Localization.SpanishKeys.Where(key => key.StartsWith("worktree.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(Localization.HasInBoth(key), key));
    }

    [Theory]
    [InlineData("es", "Preview sin conflictos.")]
    [InlineData("en", "Preview has no conflicts.")]
    public async Task Actual_cli_previews_uncommitted_lane_changes_without_changing_user_state(string locale, string ready)
    {
        var worktree = await CreateWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo, "user.txt"), "later user work\n", TestContext.Current.CancellationToken);
        var index = await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken);
        var refs = (await Git("show-ref")).Output;
        var head = (await Git("rev-parse", "HEAD")).Output;

        var result = await Cli(locale, "worktree", "preview", _repo, worktree.OwnershipId);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains(ready, result.Output);
        Assert.Contains("sample.txt", result.Output);
        Assert.DoesNotContain("later user work", result.Output);
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken));
        Assert.Equal(head, (await Git("rev-parse", "HEAD")).Output);
        Assert.Equal(refs, (await Git("show-ref")).Output);
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(_repo, "sample.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("later user work\n", await File.ReadAllTextAsync(Path.Combine(_repo, "user.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("lane result\n", await File.ReadAllTextAsync(Path.Combine(worktree.WorktreePath, "sample.txt"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("es", "Conflicto:")]
    [InlineData("en", "Conflict:")]
    public async Task Actual_cli_reports_conflict_without_applying_any_changes(string locale, string conflict)
    {
        var worktree = await CreateWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repo, "sample.txt"), "user result\n", TestContext.Current.CancellationToken);
        var index = await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken);

        var result = await Cli(locale, "worktree", "preview", _repo, worktree.OwnershipId);

        Assert.True(result.ExitCode == 3, result.Output);
        Assert.Contains(conflict, result.Output);
        Assert.Contains("sample.txt", result.Output);
        Assert.Equal("user result\n", await File.ReadAllTextAsync(Path.Combine(_repo, "sample.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preview_cannot_select_foreign_metadata_and_does_not_expose_apply()
    {
        var worktree = await CreateWorktree();
        var foreign = await Cli("en", "worktree", "preview", _repo, Guid.NewGuid().ToString("N"));
        Assert.Equal(1, foreign.ExitCode);
        Assert.DoesNotContain("Exception", foreign.Output);
        var invalid = await Cli("en", "worktree", "preview", _repo, "../foreign.json");
        Assert.Equal(2, invalid.ExitCode);
        var apply = await Cli("en", "worktree", "apply", _repo, worktree.OwnershipId);
        Assert.Equal(2, apply.ExitCode);
        Assert.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(_repo, "sample.txt"), TestContext.Current.CancellationToken));
    }

    private async Task<WorktreeIdentity> CreateWorktree()
    {
        await Git("init", "--template=");
        await Git("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(_repo, "sample.txt"), "base\n", TestContext.Current.CancellationToken);
        await Git("add", "sample.txt");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "base");
        var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(_repo);
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalRoot));
        var dataRoot = Path.Combine(_root, "data", "workspaces", workspace.ToString());
        var created = await new GitWorktreeStore(SystemProcessRuntime.Instance()).CreateAsync(
            new(workspace, LaneId.New(), _repo, dataRoot), TestContext.Current.CancellationToken);
        Assert.True(created.Succeeded, created.Error?.ToString());
        return Assert.IsType<WorktreeIdentity>(created.Worktree);
    }

    [Fact]
    public async Task Change_during_merge_invalidates_proposal_and_preserves_later_user_edit()
    {
        var worktree = await CreateWorktree();
        await File.WriteAllTextAsync(Path.Combine(worktree.WorktreePath, "sample.txt"), "lane result\n", TestContext.Current.CancellationToken);
        var index = await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken);
        var runtime = new BeforeMergeRuntime(() => File.WriteAllText(Path.Combine(_repo, "sample.txt"), "new edit during preview\n"));

        var outcome = await new GitWorktreeStore(runtime).PreviewIntegrationAsync(new(worktree), TestContext.Current.CancellationToken);

        Assert.True(runtime.Triggered, outcome.Error + ": " + runtime.LastFailedCommand);
        Assert.Equal(GitWorktreeErrorCode.WorkspaceChanged, outcome.Error);
        Assert.Null(outcome.Preview);
        Assert.Equal("new edit during preview\n", await File.ReadAllTextAsync(Path.Combine(_repo, "sample.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(_repo, ".git", "index"), TestContext.Current.CancellationToken));
    }

    private sealed class BeforeMergeRuntime(Action beforeMerge) : IProcessRuntime
    {
        private readonly IProcessRuntime _inner = SystemProcessRuntime.Instance();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ProcessLaunch> _launches = new();
        private int _triggered;
        public string? LastFailedCommand { get; private set; }
        public bool Triggered => Volatile.Read(ref _triggered) != 0;
        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            if (launch.Args.Contains("merge-tree") && Interlocked.CompareExchange(ref _triggered, 1, 0) == 0)
                beforeMerge();
            var handle = _inner.Launch(launch, cancellationToken);
            _launches[handle.Pid] = launch;
            return handle;
        }
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = _inner.Wait(handle, timeout, cancellationToken);
            if (result.ExitCode != 0 && _launches.TryGetValue(handle.Pid, out var launch)
                && !launch.Args.Contains("symbolic-ref"))
                LastFailedCommand = string.Join(" ", launch.Args) + ": " + result.Stderr;
            return result;
        }
        public void CancelTree(ProcessHandle handle) => _inner.CancelTree(handle);
        public bool IsAlive(ProcessHandle handle) => _inner.IsAlive(handle);
    }

    private async Task<ProcessOutput> Git(params string[] args)
    {
        var result = await Run("git", args, "en");
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private Task<ProcessOutput> Cli(string locale, params string[] args)
    {
        var assembly = typeof(CliApp).Assembly.Location;
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "OmniCore.Tests.runtimeconfig.json");
        var deps = Path.Combine(AppContext.BaseDirectory, "OmniCore.Tests.deps.json");
        return Run("dotnet", new[] { "exec", "--runtimeconfig", runtimeConfig, "--depsfile", deps, assembly }.Concat(args), locale);
    }

    private async Task<ProcessOutput> Run(string executable, IEnumerable<string> args, string locale)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _repo,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["OMNICORE_DATA_DIR"] = Path.Combine(_root, "data");
        start.Environment["OMNICORE_CONFIG_DIR"] = Path.Combine(_root, "config");
        start.Environment["OMNI_LOCALE"] = locale;
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return new(process.ExitCode, await output + await error);
    }

    public void Dispose()
    {
        M7FixtureCleanup.DeleteOwnedTempDirectory(_root);
    }

    private sealed record ProcessOutput(int ExitCode, string Output);
}
