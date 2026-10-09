namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>Convierte comandos slash del CLI/TUI en invocations tipadas sin perder límites de argv.</summary>
public static class CommandLineParser
{
    /// <summary>
    /// Parses process arguments that have already been tokenized by the user's shell. The first
    /// argument is the slash command and every remaining argument is preserved verbatim; quotes
    /// have already done their job and must not be interpreted a second time. A single argument
    /// retains the raw-command-line form used by scripts and the TUI.
    /// </summary>
    public static bool TryParseArguments(IReadOnlyList<string> arguments, out CommandInvocation? invocation)
    {
        invocation = null;
        if (arguments is null || arguments.Count == 0) return false;
        if (arguments.Count == 1) return TryParse(arguments[0], out invocation);

        var command = arguments[0];
        if (string.IsNullOrWhiteSpace(command) || command.Length < 2 || command[0] != '/'
            || command.Skip(1).Any(character => !char.IsLetterOrDigit(character)
                && character is not '-' and not '_' and not '.' and not ':'))
            return false;

        invocation = new CommandInvocation(command[1..], arguments.Skip(1).ToArray(), "Typed");
        return true;
    }

    public static bool TryParse(string input, out CommandInvocation? invocation)
    {
        invocation = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        var started = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (started) { parts.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            if (quote == '\0' && character is '\'' or '"')
            {
                quote = character;
                started = true;
                continue;
            }
            if (quote != '\0' && character == quote)
            {
                quote = '\0';
                continue;
            }
            // Inside double quotes only, a backslash escapes a quote/backslash. Other
            // backslashes remain literal so Windows paths and JSON argv survive parsing.
            if (quote == '"' && character == '\\' && index + 1 < input.Length
                && input[index + 1] == '"')
            {
                current.Append(input[++index]);
                started = true;
                continue;
            }
            current.Append(character);
            started = true;
        }
        if (quote != '\0') return false;
        if (started) parts.Add(current.ToString());
        if (parts.Count == 0 || !parts[0].StartsWith("/", StringComparison.Ordinal)
            || parts[0].Length < 2) return false;
        invocation = new CommandInvocation(parts[0].Substring(1), parts.Skip(1).ToArray(), "Typed");
        return true;
    }
}
