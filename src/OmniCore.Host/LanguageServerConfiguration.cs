namespace OmniCore.Host;

/// <summary>Literal, user-owned server entries. Workspace and model input never define a command.</summary>
public sealed class LanguageServerSettingsYaml
{
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public string? LanguageId { get; set; }
    public List<string>? Extensions { get; set; }
}

internal sealed record LanguageServerDefinition(string Name, string Command, IReadOnlyList<string> Args,
    string LanguageId, IReadOnlySet<string> Extensions)
{
    internal static IReadOnlyList<LanguageServerDefinition> FromSettings(UserSettingsYaml? settings)
    {
        if (settings?.LanguageServers is not { Count: > 0 } configured) return [];
        if (configured.Count > 32) throw new InvalidDataException("Too many configured language servers.");
        var result = new List<LanguageServerDefinition>();
        foreach (var (name, value) in configured.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsToken(name, 1, 64) || value is null || string.IsNullOrWhiteSpace(value.Command)
                || value.Command.Length > 1024 || value.Command.IndexOfAny(['\0', '\r', '\n']) >= 0
                || string.IsNullOrWhiteSpace(value.LanguageId) || !IsToken(value.LanguageId, 1, 64)
                || value.Args is { Count: > 64 } || value.Args?.Any(arg => arg is null || arg.Length > 1024
                    || arg.IndexOf('\0') >= 0 || arg.IndexOf('\r') >= 0 || arg.IndexOf('\n') >= 0
                    || ContainsCredentialArgument(arg)) == true
                || value.Extensions is not { Count: > 0 and <= 32 }
                || value.Extensions.Any(extension => extension is null || extension.Length is < 2 or > 16
                    || extension[0] != '.' || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character))))
                throw new InvalidDataException("Invalid user language-server entry: " + name);
            result.Add(new LanguageServerDefinition(name, value.Command,
                value.Args?.ToArray() ?? [], value.LanguageId,
                value.Extensions.Select(extension => extension.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)));
        }
        var seenExtensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in result)
            foreach (var extension in server.Extensions)
                if (!seenExtensions.Add(extension))
                    throw new InvalidDataException("A file extension can belong to only one configured language server: " + extension);
        return result;
    }

    internal bool Matches(LanguageServerDefinition other) => Name == other.Name && Command == other.Command
        && LanguageId == other.LanguageId && Args.SequenceEqual(other.Args, StringComparer.Ordinal)
        && Extensions.SetEquals(other.Extensions);

    internal static bool ContainsCredentialArgument(string argument)
    {
        var value = argument.Trim().ToLowerInvariant();
        return value.StartsWith("--token", StringComparison.Ordinal)
            || value.StartsWith("--api-key", StringComparison.Ordinal)
            || value.StartsWith("--apikey", StringComparison.Ordinal)
            || value.StartsWith("--secret", StringComparison.Ordinal)
            || value.Contains("bearer ", StringComparison.Ordinal)
            || value.StartsWith("sk-", StringComparison.Ordinal);
    }

    private static bool IsToken(string value, int minimum, int maximum) => value.Length is >= 1 and <= 64
        && value.Length >= minimum && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}