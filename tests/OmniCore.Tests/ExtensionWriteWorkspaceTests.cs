namespace OmniCore.Tests;

using System.Diagnostics;
using OmniCore.Host;

public sealed class ExtensionWriteWorkspaceTests
{
    [Fact]
    public void Private_copy_excludes_untracked_inputs_and_captures_new_ignored_outputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-write-workspace-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var temporary = Path.Combine(root, "temporary");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "ignored.txt\nforced.txt\n");
            File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "before\n");
            File.WriteAllText(Path.Combine(workspace, "outside-scope.txt"), "must not be copied\n");
            File.WriteAllText(Path.Combine(workspace, "forced.txt"), "forced ignored secret\n");
            File.WriteAllText(Path.Combine(workspace, "ignored.txt"), "user secret\n");
            RunGit(workspace, "init");
            RunGit(workspace, "config", "user.name", "Test");
            RunGit(workspace, "config", "user.email", "test@localhost");
            RunGit(workspace, "add", ".gitignore", "tracked.txt", "outside-scope.txt");
            RunGit(workspace, "add", "-f", "forced.txt");
            RunGit(workspace, "commit", "-m", "base");

            var claims = new OmniCore.Domain.ResourceClaims(["tracked.txt"], ["tracked.txt", "ignored.txt"], [], null, []);
            using var copy = ExtensionWriteWorkspace.Create(workspace, temporary, claims);
            Assert.NotEqual(workspace, copy.Root, StringComparer.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(copy.Root, ".git")));
            Assert.False(File.Exists(Path.Combine(copy.Root, "ignored.txt")));
            Assert.False(File.Exists(Path.Combine(copy.Root, "outside-scope.txt")));
            Assert.False(File.Exists(Path.Combine(copy.Root, "forced.txt")));
            File.WriteAllText(Path.Combine(copy.Root, "tracked.txt"), "after\n");
            File.WriteAllText(Path.Combine(copy.Root, "ignored.txt"), "new extension output\n");

            var changes = copy.CaptureChanges().ToDictionary(change => change.RelativePath, StringComparer.Ordinal);
            Assert.Equal("before\n", File.ReadAllText(Path.Combine(workspace, "tracked.txt")));
            Assert.Equal("user secret\n", File.ReadAllText(Path.Combine(workspace, "ignored.txt")));
            Assert.Equal("after\n", System.Text.Encoding.UTF8.GetString(changes["tracked.txt"].AfterBytes!));
            Assert.Equal("new extension output\n", System.Text.Encoding.UTF8.GetString(changes["ignored.txt"].AfterBytes!));
            Assert.Null(changes["ignored.txt"].BeforeSha256);
        }
        finally
        {
            if (Directory.Exists(root)) DeleteTree(root);
        }
    }

    [Fact]
    public void Private_copy_rejects_output_outside_declared_write_scope()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-write-scope-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "read.txt"), "read only\n");
            File.WriteAllText(Path.Combine(workspace, "write.txt"), "write scope\n");
            RunGit(workspace, "init"); RunGit(workspace, "add", "read.txt", "write.txt");
            RunGit(workspace, "config", "user.name", "Test"); RunGit(workspace, "config", "user.email", "test@localhost");
            RunGit(workspace, "commit", "-m", "base");
            var claims = new OmniCore.Domain.ResourceClaims(["read.txt"], ["write.txt"], [], null, []);
            using var copy = ExtensionWriteWorkspace.Create(workspace, Path.Combine(root, "temp"), claims);
            Assert.True(File.Exists(Path.Combine(copy.Root, "read.txt")));
            Assert.True(File.Exists(Path.Combine(copy.Root, "write.txt")));
            File.WriteAllText(Path.Combine(copy.Root, "read.txt"), "tampered\n");
            Assert.Throws<InvalidOperationException>(() => copy.CaptureChanges());
        }
        finally { if (Directory.Exists(root)) DeleteTree(root); }
    }
    private static void DeleteTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
    private static void RunGit(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }
}
