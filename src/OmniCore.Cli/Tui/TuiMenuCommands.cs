using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>Commands whose interactive destination is already owned by Preferences.</summary>
internal static class TuiMenuCommands
{
    // Keep wire/CLI compatibility; this catalog governs only the visible TUI surface.
    internal static IReadOnlyDictionary<string, string> Categories { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["preferences"] = "general", ["sidebar"] = "general", ["animation"] = "general",
        ["login"] = "accounts_connections", ["logout"] = "accounts_connections", ["openrouter"] = "accounts_connections",
        ["models"] = "models_execution",
        ["tools"] = "tools_security", ["permissions"] = "tools_security", ["trust"] = "tools_security",
        ["context"] = "context_sessions", ["memory"] = "context_sessions",
        ["extensions"] = "extensions_skills", ["skills"] = "extensions_skills",
        ["hooks"] = "extensions_skills", ["mcp"] = "extensions_skills", ["commands"] = "extensions_skills",
        ["doctor"] = "diagnostics_data", ["verify-journal"] = "diagnostics_data", ["gc"] = "diagnostics_data",
        ["help"] = "diagnostics_data",
    };

    internal static bool IsMenuOwned(string name, UserCommandAliases? aliases = null)
    {
        var rewritten = aliases?.NormalizeSlashHead("/" + name) ?? "/" + name;
        return Categories.ContainsKey(HumanCommandAliases.CanonicalName(rewritten[1..]));
    }
}
