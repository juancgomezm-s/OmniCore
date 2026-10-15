using System.Diagnostics;
using OmniCore.Execution;
using OmniCore.Sandbox;

namespace OmniCore.Tests;

// Temporary investigation of the hosted Windows failure; removed before final CI acceptance.
public sealed class WindowsCiDiagnosticTests
{
    [Fact]
    public async Task Real_git_repository_and_strong_access_diagnostic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "omnicore-git-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new SystemExecutableResolver().Resolve("git", root).ResolvedPath;
        Console.WriteLine("Git executable: " + git + "; workspace: " + root);
        foreach (var args in new string[][] { ["init", "--quiet"], ["config", "core.autocrlf", "false"] })
        {
            var start = new ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        var launcher = new WindowsAppContainerProcessLauncher();
        var helper = Path.Combine(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.exe");
        var failures = new List<string>();
        foreach (var fullInstallation in new[] { false, true })
        {
            var paths = new List<SandboxAllowedPath> { new(root, SandboxPathAccess.Read | SandboxPathAccess.Write) };
            if (fullInstallation) paths.Add(new(Path.GetDirectoryName(Path.GetDirectoryName(git))!, SandboxPathAccess.Read));
            foreach (var probe in new (string Exe, string[] Args)[]
            {
                (helper, ["read", Path.Combine(root, ".git", "HEAD")]),
                (helper, ["read", Path.Combine(root, ".git", "config")]),
                (git, ["rev-parse", "--is-inside-work-tree", "--is-bare-repository", "--git-dir", "--show-toplevel"]),
                (git, ["config", "--show-origin", "--get", "core.bare"]),
                (git, ["diff", "--check"]),
            })
            {
                var spec = new SandboxLaunchSpec(SandboxStrength.Strong, probe.Exe, probe.Args, root,
                    new Dictionary<string, string> { ["GIT_TRACE_SETUP"] = "1", ["GIT_TRACE"] = "1" },
                    paths, new(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(20));
                await using var process = await launcher.StartAsync(spec, TestContext.Current.CancellationToken);
                var result = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Console.WriteLine($"Full installation={fullInstallation}; argv={string.Join(' ', probe.Args)}; exit={result.ExitCode}\n"
                    + string.Concat(result.Chunks.Select(chunk => chunk.Text)));
                if (result.ExitCode != 0) failures.Add($"fullInstallation={fullInstallation}; {string.Join(' ', probe.Args)}; exit={result.ExitCode}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
