namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Protocol;
/// <summary>The user-declared integration check is data, not a command interpreted by Engine.</summary>
internal sealed record WorkflowRequestSpecification(string Objective, string Executable,
    IReadOnlyList<string> Argv, string WorkingDirectory, int TimeoutSeconds = 120)
{
    internal string ArgvJson => "[" + string.Join(",", Argv.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]";

    internal static WorkflowRequestSpecification Parse(IReadOnlyList<string> arguments, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var executableFlag = IndexOf(arguments, "--verify-executable");
        var argvFlag = IndexOf(arguments, "--verify-argv-json");
        if (executableFlag <= 0 || argvFlag != executableFlag + 2 || argvFlag + 1 != arguments.Count - 1)
            throw new ArgumentException("/orq-auth requires: <objective> --verify-executable <path> --verify-argv-json <JSON array>.");
        var objective = string.Join(" ", arguments.Take(executableFlag)).Trim();
        var executable = arguments[executableFlag + 1];
        if (objective.Length is 0 or > 2000 || string.IsNullOrWhiteSpace(executable) || executable.Length > 512)
            throw new ArgumentException("Workflow objective and verifier executable must be bounded and non-empty.");
        if (executable.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Verifier executable contains an invalid control character.");
        string[] argv;
        try
        {
            using var argvDocument = JsonDocument.Parse(arguments[argvFlag + 1]);
            if (argvDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("argv must be an array");
            argv = argvDocument.RootElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new JsonException("argv entries must be strings.")).ToArray();
        }
        catch (JsonException)
        { throw new ArgumentException("Verifier argv must be a JSON array of literal strings."); }
        if (argv.Length is 0 or > 64 || argv.Any(value => value is null || value.Length > 2048
            || value.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("Verifier argv must contain 1 to 64 bounded literal strings.");
        return new WorkflowRequestSpecification(objective, executable, Array.AsReadOnly(argv),
            Path.GetFullPath(workspaceRoot));
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        var found = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], value, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1;
            found = index;
        }
        return found;
    }
}
