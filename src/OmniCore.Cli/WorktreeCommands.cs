using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Cli;

/// <summary>M7 repository inspection and integration preview through the Host.</summary>
public static class WorktreeCommands
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken,
        Localization? selectedLocalization = null)
    {
        var loc = selectedLocalization ?? new Localization(LocaleSelectionResolver.Resolve(
            null, Environment.GetEnvironmentVariable("OMNI_LOCALE"), null).Locale);
        if (args.Length == 2 && args[1] is "--help" or "-h" or "help")
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
            Console.WriteLine(loc.Resolve("worktree.undo.usage"));
            Console.WriteLine(loc.Resolve("worktree.cleanup.usage"));
            Console.WriteLine(loc.Resolve("worktree.discard.usage"));
            Console.WriteLine(loc.Resolve("worktree.retention.usage"));
            return 0;
        }
        if (args.Length >= 2 && args[1] == "preview")
            return await PreviewAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length >= 2 && args[1] == "retention")
            return await RetentionAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length >= 2 && args[1] == "cleanup")
            return await CleanupAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length >= 2 && args[1] == "discard")
            return await DiscardAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length >= 2 && args[1] == "undo")
            return Undo(args, loc, cancellationToken);
        if (args.Length is < 2 or > 3 || args[1] != "inspect"
            || (args.Length == 3 && (string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-'))))
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
            Console.WriteLine(loc.Resolve("worktree.undo.usage"));
            Console.WriteLine(loc.Resolve("worktree.cleanup.usage"));
            Console.WriteLine(loc.Resolve("worktree.discard.usage"));
            Console.WriteLine(loc.Resolve("worktree.retention.usage"));
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

    private static int Undo(string[] args, Localization loc, CancellationToken cancellationToken)
    {
        if (args.Length > 3 || (args.Length == 3 && !Guid.TryParse(args[2], out _)))
        {
            Console.WriteLine(loc.Resolve("worktree.undo.usage"));
            return 2;
        }
        try
        {
            var effect = args.Length == 3 ? args[2] : null;
            if (effect is null)
            {
                var plan = WorktreeUndoHost.PlanForCli(Environment.CurrentDirectory);
                if (plan is null) { Console.WriteLine(loc.Resolve("worktree.undo.noSession")); return 1; }
                foreach (var entry in plan.Entries)
                    Console.WriteLine($"{entry.OriginalEffect}  {UndoState(loc, entry.State)}  {EscapePath(entry.RelativePath)}");
                Console.WriteLine(loc.Resolve("worktree.undo.select"));
                return 0;
            }
            var result = WorktreeUndoHost.UndoForCli(Environment.CurrentDirectory, effect, cancellationToken);
            foreach (var file in result.Files)
                Console.WriteLine($"{UndoFileOutcome(loc, file.Outcome)}  {EscapePath(file.RelativePath)}");
            Console.WriteLine(loc.Resolve("worktree.undo.outcome." + result.Outcome));
            return result.Outcome switch { "Applied" or "NoOp" => 0, "Conflict" => 3, _ => 1 };
        }
        catch (OperationCanceledException) { Console.WriteLine(loc.Resolve("worktree.undo.cancelled")); return 130; }
        catch (Exception)
        {
            Console.WriteLine(loc.Resolve("worktree.undo.error"));
            return 1;
        }
    }

    private static async Task<int> RetentionAsync(string[] args, Localization loc,
        CancellationToken cancellationToken)
    {
        var dryRun = true;
        var repositoryPath = Environment.CurrentDirectory;
        var repositorySpecified = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] == "--apply" && dryRun) dryRun = false;
            else if (!repositorySpecified && !args[index].StartsWith('-'))
            {
                repositoryPath = args[index];
                repositorySpecified = true;
            }
            else
            {
                Console.WriteLine(loc.Resolve("worktree.retention.usage"));
                return 2;
            }
        }
        try
        {
            var result = await WorktreeRetentionHost.SweepForCliAsync(repositoryPath, dryRun, cancellationToken)
                .ConfigureAwait(false);
            if (result.Error is { } error)
            {
                var key = "worktree.error." + error;
                Console.WriteLine(loc.Resolve(Localization.HasInBoth(key) ? key : "worktree.error.GitCommandFailed"));
                return 1;
            }
            Console.WriteLine(loc.Resolve("worktree.retention.summary", new Dictionary<string, string>
            {
                ["eligible"] = result.Eligible.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pruned"] = result.Pruned.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["preserved"] = result.Preserved.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failed"] = result.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            if (dryRun && result.Eligible > 0)
                Console.WriteLine(loc.Resolve("worktree.retention.applyHint"));
            return result.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(loc.Resolve("worktree.retention.cancelled"));
            return 130;
        }
    }

    private static async Task<int> CleanupAsync(string[] args, Localization loc,
        CancellationToken cancellationToken)
    {
        if (args.Length != 5 || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-')
            || !Guid.TryParseExact(args[3], "N", out _) || args[4] != "--apply")
        {
            Console.WriteLine(loc.Resolve("worktree.cleanup.usage"));
            return 2;
        }
        try
        {
            var result = await WorktreeCleanupHost.CleanupForCliAsync(args[2], args[3], cancellationToken)
                .ConfigureAwait(false);
            if (result.Removed)
            {
                Console.WriteLine(loc.Resolve("worktree.cleanup.removed"));
                return 0;
            }
            var key = "worktree.error." + result.Error;
            Console.WriteLine(loc.Resolve(Localization.HasInBoth(key) ? key : "worktree.cleanup.preserved"));
            return result.Error == "WorktreeDirty" ? 3 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(loc.Resolve("worktree.cleanup.cancelled"));
            return 130;
        }
    }

    private static async Task<int> DiscardAsync(string[] args, Localization loc,
        CancellationToken cancellationToken)
    {
        if (args.Length != 5 || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-')
            || !Guid.TryParseExact(args[3], "N", out _) || args[4] != "--apply")
        {
            Console.WriteLine(loc.Resolve("worktree.discard.usage"));
            return 2;
        }
        try
        {
            var result = await WorktreeCleanupHost.DiscardForCliAsync(args[2], args[3], cancellationToken)
                .ConfigureAwait(false);
            if (result.Removed)
            {
                Console.WriteLine(loc.Resolve("worktree.discard.removed"));
                return 0;
            }
            var key = "worktree.error." + result.Error;
            Console.WriteLine(loc.Resolve(Localization.HasInBoth(key) ? key : "worktree.cleanup.preserved"));
            return result.Error == "WorktreeDirty" ? 3 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(loc.Resolve("worktree.cleanup.cancelled"));
            return 130;
        }
    }

    private static string UndoState(Localization localization, string state) =>
        localization.Resolve(Localization.HasInBoth("worktree.undo.state." + state)
            ? "worktree.undo.state." + state : "worktree.undo.state.Unsupported");

    private static string UndoFileOutcome(Localization localization, string outcome) =>
        localization.Resolve(Localization.HasInBoth("worktree.undo.fileOutcome." + outcome)
            ? "worktree.undo.fileOutcome." + outcome : "worktree.undo.fileOutcome.Unresolvable");

    private static string EscapePath(string path) => string.Concat(path.Select(character => char.IsControl(character)
        ? "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : character.ToString()));
}
