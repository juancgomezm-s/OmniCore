using System.Diagnostics;
using OmniCore.Cli;
using OmniCore.Client;

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
