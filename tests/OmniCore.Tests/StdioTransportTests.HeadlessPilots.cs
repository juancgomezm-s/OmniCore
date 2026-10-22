using System.Diagnostics;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Independent_Node_headless_client_accepts_all_three_pilots_and_reopens_the_alternative()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-headless-pilots-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var repo = FindRepositoryRoot();
        void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var error = process.StandardError.ReadToEnd(); process.StandardOutput.ReadToEnd();
            process.WaitForExit(); Assert.True(process.ExitCode == 0, error);
        }
        try
        {
            Git("init"); Git("config", "user.name", "Fixture"); Git("config", "user.email", "fixture@localhost");
            File.WriteAllText(Path.Combine(workspace, "captured.txt"), "base version"); Git("add", "."); Git("commit", "-m", "fixture base");
            var start = new ProcessStartInfo("node") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var inherited = new HashSet<string>(["PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "DOTNET_ROOT", "HOME"], StringComparer.OrdinalIgnoreCase);
            foreach (var variable in start.Environment.Keys.Where(key => !inherited.Contains(key)).ToArray()) start.Environment.Remove(variable);
            start.Environment["OMNICORE_DATA_DIR"] = Path.Combine(root, "data");
            start.Environment["OMNICORE_CONFIG_DIR"] = Path.Combine(root, "config");
            start.ArgumentList.Add(Path.Combine(repo, "sdk", "extensions", "test", "headless-pilots.mjs"));
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "omni.dll"));
            start.ArgumentList.Add(workspace); start.ArgumentList.Add(Path.Combine(repo, "extensions", "pilots"));
            start.ArgumentList.Add(ExtensionPlatformTests.ContextExample(root));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            var result = await output; var error = await errors;
            Assert.True(process.ExitCode == 0, result + "\n" + error);
            Assert.Contains("\"originalUntouched\":true", result);
            Assert.Contains("\"conversationRestored\":true", result);
            Assert.Contains("\"agentFlow\":true", result);
            Assert.Contains("\"contextAuthorization\":true", result);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
