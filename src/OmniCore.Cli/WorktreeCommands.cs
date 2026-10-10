using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Cli;

/// <summary>M7 repository inspection through the Host, without creating or integrating worktrees.</summary>
public static class WorktreeCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var loc = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en"
            ? Localization.English() : Localization.Spanish();
        if (args.Length == 2 && args[1] is "--help" or "-h" or "help")
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            return 0;
        }
        if (args.Length is < 2 or > 3 || args[1] != "inspect"
            || (args.Length == 3 && (string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-'))))
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            return 2;
        }
        try
        {
            var inspected = await WorktreeInspectionHost.InspectAsync(
                args.Length == 3 ? args[2] : Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
            if (!inspected.Succeeded)
            {
                var key = "worktree.error." + inspected.ErrorCode;
                Console.WriteLine(loc.Resolve(Localization.HasInBoth(key) ? key : "worktree.error.GitCommandFailed"));
                return 1;
            }
            Console.WriteLine(loc.Resolve("worktree.inspect.repository", "value", inspected.RepositoryRoot));
            Console.WriteLine(loc.Resolve("worktree.inspect.common", "value", inspected.GitCommonDirectory));
            Console.WriteLine(loc.Resolve("worktree.inspect.head", "value", inspected.HeadCommit));
            Console.WriteLine(loc.Resolve("worktree.inspect.branch", "value",
                inspected.Branch ?? loc.Resolve("worktree.inspect.detached")));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.cancelled"));
            return 130;
        }
    }
}
