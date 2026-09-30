using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class Epic001ProjectIdentityTests
{
    [Fact]
    public void Workspace_id_is_the_same_through_a_directory_link_and_direct_path()
    {
        var temp = TempRoot();
        var physicalRoot = Path.Combine(temp, "actual");
        var linkRoot = Path.Combine(temp, "linked");
        Directory.CreateDirectory(physicalRoot);
        try
        {
            if (!TryCreateDirectoryLink(linkRoot, physicalRoot))
            {
                Assert.Skip("El entorno no permite crear enlaces de directorio.");
            }

            var paths = OmniHost.CreatePlatformPaths(Path.Combine(temp, "data"));
            var direct = OmniHost.WorkspaceDataDirectory(paths, physicalRoot);
            var linked = OmniHost.WorkspaceDataDirectory(paths, linkRoot);
            Assert.Equal(direct, linked);
        }
        finally
        {
            RemoveLink(linkRoot);
            RemoveDirectory(temp);
        }
    }

    [Fact]
    public void Project_id_is_shared_by_repository_worktree_and_equivalent_origin_urls()
    {
        var temp = TempRoot();
        var repo = Path.Combine(temp, "repo");
        var commonGit = Path.Combine(repo, ".git");
        var worktree = Path.Combine(temp, "worktree");
        var worktreeGitDir = Path.Combine(commonGit, "worktrees", "worktree-1");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(commonGit, "config"),
            "[remote \"origin\"]\n\turl = https://user:token@GitHub.COM/Org/Repo.git\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + worktreeGitDir + Environment.NewLine);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), Path.GetRelativePath(worktreeGitDir, commonGit));

        var repositoryId = ProjectIdentity.FromWorkspaceRoot(repo);
        var worktreeId = ProjectIdentity.FromWorkspaceRoot(worktree);
        var equivalentUrlId = ProjectId.Derive(ProjectIdentity.NormalizeOrigin("git@github.com:Org/Repo"));

        Assert.Equal(repositoryId, worktreeId);
        Assert.Equal(repositoryId, equivalentUrlId);
        Assert.Matches("^[0-9a-f]{16}$", repositoryId.ToString());
        RemoveDirectory(temp);
    }

    [Fact]
    public void Workspace_case_folding_is_platform_rule_not_domain_behavior()
    {
        var path = Path.Combine(Path.GetTempPath(), "OmniCore", "MixedCase");
        var windowsCanonical = ProjectIdentity.CanonicalWorkspacePath(path, foldCase: true);
        var caseSensitiveCanonical = ProjectIdentity.CanonicalWorkspacePath(path, foldCase: false);

        Assert.Equal(windowsCanonical, windowsCanonical.ToLowerInvariant());
        Assert.NotEqual(caseSensitiveCanonical, caseSensitiveCanonical.ToLowerInvariant());
        Assert.NotEqual(WorkspaceId.Of(windowsCanonical).ToString(), WorkspaceId.Of(caseSensitiveCanonical).ToString());
    }

    [Fact]
    public void Different_repositories_get_different_project_ids()
    {
        var first = ProjectId.Derive(ProjectIdentity.NormalizeOrigin("https://github.com/team/first.git"));
        var second = ProjectId.Derive(ProjectIdentity.NormalizeOrigin("ssh://git@github.com/team/second.git"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Project_without_origin_uses_physical_git_common_directory()
    {
        var temp = TempRoot();
        var repo = Path.Combine(temp, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var canonicalCommonDirectory = ProjectIdentity.ResolvePhysicalWorkspaceRoot(Path.Combine(repo, ".git"))
            .Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows()) canonicalCommonDirectory = canonicalCommonDirectory.ToLowerInvariant();
        var expected = ProjectId.Derive(canonicalCommonDirectory);

        Assert.Equal(expected, ProjectIdentity.FromWorkspaceRoot(repo));
        RemoveDirectory(temp);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(linkPath);
                start.ArgumentList.Add(target);
                using var process = System.Diagnostics.Process.Start(start);
                process?.WaitForExit(15000);
                return process is { ExitCode: 0 } && Directory.Exists(linkPath);
            }

            Directory.CreateSymbolicLink(linkPath, target);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-epic001", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RemoveLink(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, false);
        }
        catch (Exception)
        {
        }
    }

    private static void RemoveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Separator_and_trailing_slash_spellings_give_the_same_canonical_workspace_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-canon-" + Guid.NewGuid().ToString("N"), "Repo");
        var slashes = root.Replace('\\', '/');
        var backslashes = root.Replace('/', '\\') + Path.DirectorySeparatorChar;

        Assert.Equal(ProjectIdentity.CanonicalWorkspacePath(slashes, foldCase: true),
            ProjectIdentity.CanonicalWorkspacePath(backslashes, foldCase: true));
        Assert.Equal(WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(slashes, foldCase: false)).ToString(),
            WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(root + "/", foldCase: false)).ToString());
    }
}
