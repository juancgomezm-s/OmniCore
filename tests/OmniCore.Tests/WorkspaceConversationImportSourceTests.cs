namespace OmniCore.Tests;

using System.Diagnostics;
using System.Text;
using OmniCore.Host;
using Xunit;

public sealed class WorkspaceConversationImportSourceTests
{
    [Fact]
    public void Confined_source_returns_exact_unicode_document_and_retains_workspace_lease()
    {
        RequireSupported();
        var (root, workspace, _) = CreateOwnedFixture();
        var relative = "docs/inspección.json";
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        var bytes = Encoding.UTF8.GetBytes("{\"source\":\"ñ-é-資料\"}");
        File.WriteAllBytes(Path.Combine(workspace, relative), bytes);
        using var source = WorkspaceConversationImportSource.Open(workspace, relative);
        Assert.Equal(bytes, source.Bytes);
        Assert.Equal(Path.GetFullPath(workspace), source.CanonicalRoot);
        if (OperatingSystem.IsWindows())
        {
            var error = Record.Exception(() => Directory.Move(workspace, Path.Combine(root, "renamed-workspace")));
            Assert.IsAssignableFrom<IOException>(error);
            Assert.True(Directory.Exists(workspace));
        }
    }

    [Fact]
    public void Directory_substitution_before_open_is_rejected_without_returning_external_bytes()
    {
        RequireSupported();
        var (_, workspace, external) = CreateOwnedFixture();
        var directory = Path.Combine(workspace, "docs"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "source.json"), "INSIDE");
        File.WriteAllText(Path.Combine(external, "source.json"), "OUTSIDE");
        var attempted = false;
        byte[]? returned = null;
        var error = Record.Exception(() =>
        {
            using var source = WorkspaceConversationImportSource.Open(workspace, "docs/source.json", path =>
            {
                if (attempted || path != (OperatingSystem.IsWindows() ? directory : Path.Combine(directory, "source.json"))) return;
                attempted = true;
                Directory.Move(directory, directory + "-before");
                CreateDirectoryLink(directory, external);
            });
            returned = source.Bytes;
        });
        Assert.True(attempted); Assert.Null(returned);
        Assert.True(error is InvalidDataException or IOException, error?.GetType().FullName ?? "missing rejection");
        Assert.Equal("OUTSIDE", File.ReadAllText(Path.Combine(external, "source.json")));
    }

    [Fact]
    public void Already_external_directory_link_is_rejected()
    {
        RequireSupported();
        var (_, workspace, external) = CreateOwnedFixture();
        File.WriteAllText(Path.Combine(external, "source.json"), "OUTSIDE");
        CreateDirectoryLink(Path.Combine(workspace, "docs"), external);
        var error = Record.Exception(() => { using var source = WorkspaceConversationImportSource.Open(workspace, "docs/source.json"); });
        Assert.IsType<InvalidDataException>(error);
    }

    [Theory]
    [InlineData("../source.json")]
    [InlineData("/source.json")]
    [InlineData("docs/../source.json")]
    [InlineData("docs\\source.json")]
    [InlineData("source.json:secret")]
    [InlineData("docs./source.json")]
    [InlineData("docs /source.json")]
    public void Ambiguous_or_escaping_path_is_rejected_without_open(string path)
    {
        Assert.False(WorkspaceConversationImportSource.IsRelativePath(path));
        Assert.Throws<InvalidDataException>(() => WorkspaceConversationImportSource.Open(Path.GetTempPath(), path));
    }

    [Fact]
    public void Source_must_be_a_bounded_regular_file()
    {
        RequireSupported();
        var (_, workspace, _) = CreateOwnedFixture();
        File.WriteAllBytes(Path.Combine(workspace, "large.json"), new byte[384 * 1024 + 1]);
        var large = Record.Exception(() => { using var source = WorkspaceConversationImportSource.Open(workspace, "large.json"); });
        Assert.IsType<InvalidDataException>(large);
        Directory.CreateDirectory(Path.Combine(workspace, "directory.json"));
        var directory = Record.Exception(() => { using var source = WorkspaceConversationImportSource.Open(workspace, "directory.json"); });
        Assert.IsType<InvalidDataException>(directory);
    }

    private static void RequireSupported()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) Assert.Skip("Confined source requires Windows or Linux openat2.");
    }
    private static (string Root, string Workspace, string External) CreateOwnedFixture()
    {
        var root = Directory.CreateTempSubdirectory("oc-import-confinement-").FullName;
        var workspace = Path.Combine(root, "workspace"); var external = Path.Combine(root, "external");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(external);
        // Retain owned fixture for personal review; no recursive deletion of junctions.
        return (root, workspace, external);
    }
    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", path, target },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("junction process missing");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }
}
