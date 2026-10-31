using System.Text;
using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Cli;

public sealed partial class CliApp
{
    private static Task<int> RunInitialize(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(Loc().Resolve("project.initialize.usage"));
            return Task.FromResult(2);
        }
        try
        {
            var plan = Runtime.InitializeProject(CancellationToken.None);
            Console.WriteLine(ProjectInitializationPresentation.Format(plan, Loc()));
            return Task.FromResult(0);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            var key = exception.Message.StartsWith("project.initialize.", StringComparison.Ordinal)
                ? exception.Message : "project.initialize.failed";
            Console.Error.WriteLine(Loc().Resolve(key));
            return Task.FromResult(1);
        }
    }
}

internal static class ProjectInitializationPresentation
{
    internal static string Format(ProjectInitializationPlan plan, Localization localization)
    {
        var output = new StringBuilder(localization.Resolve(!plan.Changed ? "project.initialize.unchanged"
            : plan.PreviousContent is null ? "project.initialize.created" : "project.initialize.updated"));
        output.AppendLine(" " + Path.GetFileName(plan.TargetPath));
        output.AppendLine(localization.Resolve("project.initialize.sources"));
        foreach (var source in plan.Sources)
            output.AppendLine("- " + source.RelativePath + " (" + source.Kind + "; SHA-256 " + source.Sha256 + ")");
        if (plan.Diagnostics.Count > 0)
        {
            output.AppendLine(localization.Resolve("project.initialize.diagnostics"));
            foreach (var diagnostic in plan.Diagnostics.Distinct(StringComparer.Ordinal)) output.AppendLine("- " + diagnostic);
        }
        return output.ToString().TrimEnd();
    }
}
