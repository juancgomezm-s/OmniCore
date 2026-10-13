namespace OmniCore.Protocol;

/// <summary>User-owned command aliases. Rewrites only the command head, leaving all arguments literal.</summary>
public sealed class UserCommandAliases
{
    private readonly Dictionary<string, string> _aliases;

    public UserCommandAliases(IReadOnlyDictionary<string, string>? aliases = null)
    {
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (aliases is null) return;
        foreach (var (alias, target) in aliases)
        {
            ValidatePair(alias, target);
            if (!_aliases.TryAdd(alias, target))
                throw new ArgumentException("User command aliases are ambiguous.", nameof(aliases));
        }
    }

    public IReadOnlyDictionary<string, string> Entries => _aliases;

    public static void ValidatePair(string alias, string target)
    {
        if (!IsAliasName(alias) || !IsCommandTarget(target) || IsCoreCommandName(alias)
            || string.Equals(alias, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("User command alias is invalid or collides with a reserved Core command.");
    }

    private static bool IsCoreCommandName(string name) => HumanCommandAliases.IsReserved(name)
        || HumanCommandAliases.Entries.Any(entry => entry.Canonical.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string NormalizeSlashHead(string input)
    {
        if (input.Length < 2 || input[0] != '/') return input;
        var end = 1;
        while (end < input.Length && !char.IsWhiteSpace(input[end])) end++;
        var head = input[1..end];
        return _aliases.TryGetValue(head, out var canonical)
            ? "/" + canonical + input[end..] : input;
    }

    public string[] NormalizeCli(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] == "--") return arguments;
        if (arguments[0].StartsWith('/'))
        {
            var rewritten = NormalizeSlashHead(arguments[0]);
            return rewritten == arguments[0] ? arguments : new[] { rewritten }.Concat(arguments.Skip(1)).ToArray();
        }
        return _aliases.TryGetValue(arguments[0], out var canonical)
            ? new[] { canonical }.Concat(arguments.Skip(1)).ToArray() : arguments;
    }

    public IEnumerable<string> CompletionNames(IEnumerable<string> canonicalNames) =>
        canonicalNames.SelectMany(name => new[] { name }.Concat(_aliases.Where(alias =>
            string.Equals(alias.Value, name, StringComparison.OrdinalIgnoreCase)).Select(alias => alias.Key)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsAliasName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && char.IsAsciiLetterOrDigit(value[0]);

    private static bool IsCommandTarget(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
        && char.IsAsciiLetterOrDigit(value[0]) && value[^1] != ':'
        && !value.Contains("::", StringComparison.Ordinal);
}
