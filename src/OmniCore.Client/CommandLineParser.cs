namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>Parser mínimo del plain renderer: convierte /name args a una invocation tipada.</summary>
public static class CommandLineParser
{
    public static bool TryParse(string input, out CommandInvocation? invocation)
    {
        invocation = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("/", StringComparison.Ordinal)
            || parts[0].Length < 2) return false;
        invocation = new CommandInvocation(parts[0].Substring(1), parts.Skip(1).ToArray(), "Typed");
        return true;
    }
}
