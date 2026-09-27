namespace OmniCore.Domain;

/// <summary>
/// Texto visible generado por el servidor: viaja como clave + argumentos, nunca como frase
/// traducida (ADR-0040). El cliente lo resuelve con sus recursos es/en.
/// </summary>
public record LocalizedText(string Key, Dictionary<string, string> Args)
{
    private static readonly Dictionary<string, string> Empty = new();

    public static LocalizedText Of(string key) => new(key, Empty);

    public static LocalizedText Of(string key, string argName, string argValue) =>
        new(key, new Dictionary<string, string> { [argName] = argValue });

    /// <summary>Formato estable para logs y tests (no para UI).</summary>
    public string Render()
    {
        if (Args.Count == 0)
        {
            return Key;
        }

        var parts = new string[Args.Count];
        var i = 0;
        foreach (var kv in Args)
        {
            parts[i++] = kv.Key + "=" + kv.Value;
        }

        return Key + "(" + string.Join(",", parts.Order()) + ")";
    }
}

/// <summary>Categoría con la que un contributor aporta un ContextItem (ADR-0029).</summary>
public enum ContributionCategory
{
    System,
    Task,
    Skills,
    Conversation,
    WorkingState,
    Memory,
    Knowledge,
    ToolObservations,
    UserReference,
}

/// <summary>Rol de un ContextItem en la priorización del presupuesto (spec §24).</summary>
public enum ContextPriority
{
    Pinned,
    High,
    Normal,
    Low,
}

/// <summary>Política de retención de un ContextItem (spec §24).</summary>
public enum RetentionPolicy
{
    KeepForever,
    RegenerateEachTurn,
    ConversationWindow,
}

/// <summary>Metadatos de procedencia de un ContextItem (ADR-0029, spec §24). Nunca contenido sensible.</summary>
public record ContextProvenance(
    string ContributorId,
    ContributionCategory Category,
    string ComponentSource,
    ScopeLevel Scope,
    bool Sensitive) { }