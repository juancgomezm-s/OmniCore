namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>Parser mínimo del plain renderer: convierte /name args a una invocation tipada.</summary>
public static class CommandLineParser
{
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
