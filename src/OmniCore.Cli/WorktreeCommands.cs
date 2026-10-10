using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Domain;

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
            Console.WriteLine(loc.Resolve("worktree.undo.usage"));
            return 0;
        }
        if (args.Length >= 2 && args[1] == "preview")
            return await PreviewAsync(args, loc, cancellationToken).ConfigureAwait(false);
        if (args.Length >= 2 && args[1] == "undo")
            return Undo(args, loc, cancellationToken);
        if (args.Length is < 2 or > 3 || args[1] != "inspect"
            || (args.Length == 3 && (string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith('-'))))
        {
            Console.WriteLine(loc.Resolve("worktree.inspect.usage"));
            Console.WriteLine(loc.Resolve("worktree.preview.usage"));
            Console.WriteLine(loc.Resolve("worktree.undo.usage"));
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
            var effect = args.Length == 3 ? ToolCallId.Parse(args[2]) : (ToolCallId?)null;
            if (effect is null)
            {
                var plan = WorktreeUndoHost.Plan(Environment.CurrentDirectory);
                if (plan is null) { Console.WriteLine("No hay una sesión verificada de OmniCore para este workspace."); return 1; }
                foreach (var entry in plan.Entries)
                    Console.WriteLine($"{entry.OriginalEffect}  {entry.State}  {EscapePath(entry.RelativePath)}");
                Console.WriteLine("Selecciona un toolCallId con: omni worktree undo <toolCallId>");
                return 0;
            }
            var result = WorktreeUndoHost.Undo(Environment.CurrentDirectory, effect, cancellationToken);
            foreach (var file in result.Files)
                Console.WriteLine($"{file.Outcome}  {EscapePath(file.RelativePath)}");
            Console.WriteLine(result.Summary);
            return result.Outcome switch { UndoOutcome.Applied or UndoOutcome.NoOp => 0, UndoOutcome.Conflict => 3, _ => 1 };
        }
        catch (OperationCanceledException) { Console.WriteLine("Undo cancelado."); return 130; }
        catch (Exception)
        {
            Console.WriteLine(loc.Resolve("worktree.undo.error"));
            return 1;
        }
    }

    private static string EscapePath(string path) => string.Concat(path.Select(character => char.IsControl(character)
        ? "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : character.ToString()));
}
