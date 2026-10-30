namespace OmniCore.Host;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Qualification;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Disposable Git worktrees containing only pinned benchmark inputs. Only exact pinned
/// postimages are compiled/executed; arbitrary model output is never run, imported, or used as argv.</summary>
internal sealed class FullQualificationFixtures : IProbeFixtureValidator, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-full-qualification-" + Guid.NewGuid().ToString("N"));
    private string Seed => Path.Combine(_root, "seed");
    private bool _initialized;

    public async ValueTask<ProbeFixtureValidation> ValidateAsync(Probe probe, string output, CancellationToken token)
    {
        var fixture = FullProbeSuite.Fixtures.SingleOrDefault(item => "code-" + item.Id == probe.Id.ToString())
            ?? throw new InvalidOperationException("Unknown code fixture identity.");
        // Defense in depth: never turn a provider's unconstrained code into an executable.
        if (ProbeScorer.Score(ProbeKind.CodeFixture, output, probe.Expected) != 1)
            return new(false, "code fixture edit differs from its oracle");
        if (!_initialized) await InitializeAsync(token).ConfigureAwait(false);
        var worktree = Path.Combine(_root, "case-" + fixture.Id);
        await RunAsync("git", ["-C", Seed, "worktree", "add", "--detach", worktree, "HEAD"], _root, token).ConfigureAwait(false);
        try
        {
            var file = Path.Combine(worktree, "Logic.cs");
            await File.WriteAllTextAsync(file, fixture.Before, new UTF8Encoding(false), token).ConfigureAwait(false);
            var test = FormattableString.Invariant($"if (Logic.Calculate({fixture.A}, {fixture.B}) != {fixture.Expected}) return 1; System.Console.WriteLine(\"FIXTURE_PASS\"); return 0;\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "Program.cs"), test, new UTF8Encoding(false), token).ConfigureAwait(false);
            var edit = JsonSerializer.Deserialize(output, FullFixtureJson.Default.CodeFixtureEdit)
                ?? throw new InvalidOperationException("Invalid code fixture edit.");
            if (edit.Path != "Logic.cs" || edit.ExpectedVersion != FileVersion.VersionToken(Encoding.UTF8.GetBytes(fixture.Before)))
                return new(false, "code fixture path or version mismatch");
            var tool = new FilesystemPatchTool(new PathBoundaryValidator());
            var prepared = tool.Prepare(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "qualification-fixture", output),
                new ToolPreparationContext(worktree, DateTimeOffset.UtcNow));
            if (prepared is not Prepared ready) return new(false, "code fixture preparation failed");
            // Qualification consent authorizes this owned fixture, never the user's workspace.
            var policy = ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow);
            var result = await tool.ExecuteAsync(policy.Authorize(ready.Intent), new ToolExecutionContext(worktree), token).ConfigureAwait(false);
            if (result.IsError || await File.ReadAllTextAsync(file, token).ConfigureAwait(false) != fixture.After)
                return new(false, "code fixture postimage mismatch");
            // The edit is now byte-for-byte the shipped oracle. Build/test execute only this pinned code.
            await RunAsync("dotnet", ["restore", "Fixture.csproj", "--configfile", "NuGet.Config", "--disable-parallel"], worktree, token).ConfigureAwait(false);
            await RunAsync("dotnet", ["build", "Fixture.csproj", "--no-restore", "--disable-build-servers", "-v:q", "-p:UseSharedCompilation=false"], worktree, token).ConfigureAwait(false);
            var tested = await RunAsync("dotnet", [Path.Combine(worktree, "bin", "Debug", "net10.0", "Fixture.dll")], worktree, token).ConfigureAwait(false);
            return tested.Trim() == "FIXTURE_PASS" ? new(true) : new(false, "code fixture tests failed");
        }
        finally
        {
            // Remove only the owned worktree, never a user repository or checkout.
            try { await RunAsync("git", ["-C", Seed, "worktree", "remove", "--force", worktree], _root, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException
                or System.ComponentModel.Win32Exception)
            { }
        }
    }

    private async System.Threading.Tasks.Task InitializeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Seed);
        await File.WriteAllTextAsync(Path.Combine(Seed, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>"
            + "<ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>"
            + "<ImportDirectoryPackagesProps>false</ImportDirectoryPackagesProps><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>", token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Seed, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>", token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Seed, "Logic.cs"), FullProbeSuite.Fixtures[0].Before, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Seed, "Program.cs"), "return 1;\n", token).ConfigureAwait(false);
        await RunAsync("git", ["init", Seed], _root, token).ConfigureAwait(false);
        await RunAsync("git", ["-C", Seed, "add", "."], _root, token).ConfigureAwait(false);
        await RunAsync("git", ["-C", Seed, "-c", "user.name=OmniCore qualification", "-c", "user.email=qualification@localhost", "-c", "commit.gpgsign=false", "commit", "-m", "Pinned qualification fixture"], _root, token).ConfigureAwait(false);
        _initialized = true;
    }

    private async System.Threading.Tasks.Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        var home = Path.Combine(_root, "home"); Directory.CreateDirectory(home);
        start.Environment["TEMP"] = home; start.Environment["TMP"] = home;
        start.Environment["HOME"] = home; start.Environment["USERPROFILE"] = home;
        foreach (var (name, folder) in new[] { ("APPDATA", "roaming"), ("LOCALAPPDATA", "local"),
            ("NUGET_PACKAGES", "packages"), ("NUGET_HTTP_CACHE_PATH", "nuget-http"), ("NUGET_SCRATCH", "nuget-scratch") })
        { var owned = Path.Combine(home, folder); Directory.CreateDirectory(owned); start.Environment[name] = owned; }
        start.Environment["DOTNET_CLI_HOME"] = home; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["MSBuildEnableWorkloadResolver"] = "false";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1"; start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start) ?? throw new IOException("Fixture process did not start.");
        try
        {
            var stdout = ReadBounded(process.StandardOutput, deadline.Token);
            var stderr = ReadBounded(process.StandardError, deadline.Token);
            await System.Threading.Tasks.Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Fixture " + executable + " failed with exit code "
                + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + stderr.Result + "\n" + stdout.Result);
            return stdout.Result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private static async System.Threading.Tasks.Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) is var count && count > 0)
        { if (result.Length + count > 64 * 1024) throw new IOException("Fixture output limit."); result.Append(buffer, 0, count); }
        return result.ToString();
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar + "omni-full-qualification-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup boundary mismatch.");
        if (!Directory.Exists(full)) return;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture root was replaced by a link.");
        try { Directory.Delete(full, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}