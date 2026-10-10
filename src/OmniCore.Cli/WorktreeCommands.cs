using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Cli;

/// <summary>M7 repository inspection and integration preview through the Host.</summary>
public static class WorktreeCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var loc = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en"
            ? Localization.English() : Localization.Spanish();
        if (args.Length == 2 && args[1] is "--help" or "-h" or "help")
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
            return 0;
        }
        if (args.Length >= 2 && args[1] == "preview")
            return await PreviewAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length is < 2 or > 3 || args[1] != "inspect"
            || (args.Length == 3 && (string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-'))))
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
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

    private static async Task<int> PreviewAsync(string[] args, Localization loc, CancellationToken cancellationToken)
    {
        if (args.Length != 4 || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-')
            || !Guid.TryParseExact(args[3], "N", out _))
        {
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
            return 2;
        }
        try
        {
            var result = await WorktreePreviewHost.PreviewAsync(args[2], args[3], cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var key = "worktree.error." + result.ErrorCode;
                Console.WriteLine(loc.Resolve(Localization.HasInBoth(key) ? key : "worktree.error.GitCommandFailed"));
                return 1;
            }
            Console.WriteLine(loc.Resolve("worktree.preview.proposal", "value", result.ProposalId));
            if (result.WorkspaceHeadChanged || result.WorkspaceBranchChanged)
                Console.WriteLine(loc.Resolve("worktree.preview.identityChanged"));
            foreach (var change in result.Changes)
                Console.WriteLine(loc.Resolve("worktree.preview.change", "value", EscapePath(change.Path)));
            foreach (var path in result.Conflicts)
                Console.WriteLine(loc.Resolve("worktree.preview.conflict", "value", EscapePath(path)));
            Console.WriteLine(loc.Resolve(result.Conflicts.Count == 0 ? "worktree.preview.ready" : "worktree.preview.blocked"));
            return result.Conflicts.Count == 0 ? 0 : 3;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(loc.Resolve("worktree.preview.cancelled"));
            return 130;
        }
    }

    private static string EscapePath(string path) => string.Concat(path.Select(character => char.IsControl(character)
        ? "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : character.ToString()));
}
